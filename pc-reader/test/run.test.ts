import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { PostOutcome } from '../src/ingest.ts';
import { run, type Deps } from '../src/run.ts';
import type { Page, Reading, WorkItem } from '../src/types.ts';
import { fixture } from './support.ts';

const aldi = (n: number): WorkItem => ({ store: 'aldi', url: `https://www.aldi.ie/product/item-${String(n).padStart(12, '0')}` });
const dunnes = (n: number): WorkItem => ({ store: 'dunnes', url: `https://www.dunnesstoresgrocery.com/product/item-${n}00000` });
const tesco = (n: number): WorkItem => ({ store: 'tesco', url: `https://www.tesco.ie/groceries/en-IE/products/${n}` });

function harness(pages: (item: string) => Page | Error, postOutcome?: (readings: Reading[]) => PostOutcome) {
  let clock = 1_000_000;
  const loads: { url: string; at: number; inFlight: number }[] = [];
  const posts: Reading[][] = [];
  let inFlight = 0;
  const deps: Deps = {
    now: () => clock,
    sleep: async (ms) => {
      clock += ms;
    },
    log: () => undefined,
    load: async (url) => {
      inFlight++;
      loads.push({ url, at: clock, inFlight });
      clock += 300; // a page takes a moment
      await Promise.resolve();
      inFlight--;
      const page = pages(url);
      if (page instanceof Error) {
        throw page;
      }
      return page;
    },
    post: async (readings) => {
      posts.push(readings);
      return postOutcome?.(readings) ?? { status: 200, accepted: readings.length, rejected: [] };
    },
  };
  return { deps, loads, posts };
}

const aldiPage: Page = { status: 200, html: fixture('aldi-product-page.html') };
const tescoPage: Page = { status: 200, html: fixture('tesco-product-page-plain.html') };
const dunnesPage: Page = { status: 200, html: fixture('dunnes-product-page.html') };
const challenged: Page = { status: 403, html: fixture('cloudflare-just-a-moment.html') };
const denied: Page = { status: 200, html: fixture('akamai-access-denied.html') };

test('pages on one site are loaded one at a time, at least 5 seconds apart', async () => {
  const { deps, loads, posts } = harness(() => aldiPage);
  const [report] = await run([aldi(1), aldi(2), aldi(3)], deps);

  assert.deepEqual(loads.map((l) => l.inFlight), [1, 1, 1]);
  const gaps = loads.slice(1).map((l, i) => l.at - (loads[i]?.at ?? 0));
  assert.ok(gaps.every((gap) => gap >= 5_000), `gaps ${gaps}`);
  assert.equal(report?.read, 3);
  assert.equal(posts.length, 1);
  assert.equal(posts[0]?.length, 3);
});

test('a refusal stops that store at once, is not retried, and keeps what was read before it', async () => {
  const { deps, loads, posts } = harness((url) => (url.endsWith('2') ? denied : aldiPage));
  const reports = await run([aldi(1), aldi(2), aldi(3), aldi(4), tesco(5555)], deps);

  const aldiLoads = loads.filter((l) => l.url.includes('aldi'));
  assert.equal(aldiLoads.length, 2, 'the refused URL is asked once, the ones after it not at all');
  const [a, t] = reports;
  assert.equal(a?.blocked?.notAsked, 2);
  assert.equal(a?.read, 1);
  assert.equal(posts[0]?.length, 1, 'the one reading before the refusal is still posted');
  assert.equal(t?.store, 'tesco', 'another store is still tried');
});

test('a store that refuses its first page posts nothing and the other store carries on', async () => {
  const { deps, posts } = harness((url) => (url.includes('tesco') ? denied : aldiPage));
  const reports = await run([tesco(1111), tesco(2222), aldi(1)], deps);
  const tescoReport = reports.find((r) => r.store === 'tesco');
  assert.equal(tescoReport?.blocked?.notAsked, 1);
  assert.equal(tescoReport?.posted.batches, 0);
  assert.equal(posts.length, 1);
  assert.equal(posts[0]?.[0]?.store, 'aldi');
});

test('an unreadable or failed page is skipped and reported, the rest still read', async () => {
  const { deps, posts } = harness((url) => (url.endsWith('2') ? new Error('timeout') : url.endsWith('3') ? { status: 200, html: '<title>x</title>' } : tescoPage));
  const [report] = await run([tesco(1111), tesco(2), tesco(3), tesco(4444)], deps);
  assert.equal(report?.read, 2);
  assert.equal(report?.unreadable.length, 2);
  assert.match(report?.unreadable[0]?.why ?? '', /did not load: timeout/);
  assert.equal(posts[0]?.length, 2);
});

test('batches are cut at 200 readings and a refusing server ends that store without retry', async () => {
  const many = Array.from({ length: 450 }, (_, i) => aldi(i + 1));
  const { deps, posts } = harness(() => aldiPage);
  await run(many, deps, 0);
  assert.deepEqual(posts.map((p) => p.length), [200, 200, 50]);

  const refusing = harness(() => aldiPage, () => ({ status: 401, accepted: 0, rejected: [], error: 'unauthorized: A valid price token is required.' }));
  const [report] = await run(many, refusing.deps, 0);
  assert.equal(refusing.posts.length, 1);
  assert.match(report?.posted.error ?? '', /HTTP 401 unauthorized/);
});

test('a post that throws is reported against its store and does not stop the other stores', async () => {
  const { deps, loads } = harness(
    (url) => (url.includes('aldi') ? aldiPage : tescoPage),
    () => {
      throw new Error('fetch failed');
    },
  );
  const reports = await run([aldi(1), tesco(5555)], deps, 0);
  assert.deepEqual(reports.map((r) => r.store), ['aldi', 'tesco']);
  assert.equal(loads.length, 2);
  assert.deepEqual(reports.map((r) => r.read), [1, 1]);
  assert.ok(reports.every((r) => /fetch failed/.test(r.posted.error ?? '')));
});

test('per-reading refusals are reported against their URL', async () => {
  const { deps } = harness(() => aldiPage, (readings) => ({ status: 200, accepted: readings.length - 1, rejected: [{ index: 1, accepted: false, reason: 'unknown_listing' }] }));
  const [report] = await run([aldi(1), aldi(2)], deps);
  assert.deepEqual(report?.posted.rejected, [{ url: aldi(2).url, reason: 'unknown_listing' }]);
});

test('dunnes: pages are 5 seconds apart, the reading has the same shape as the others, and a challenge stops the store at once', async () => {
  const ok = harness(() => dunnesPage);
  const [report] = await run([dunnes(1), dunnes(2), dunnes(3)], ok.deps);
  const gaps = ok.loads.slice(1).map((l, i) => l.at - (ok.loads[i]?.at ?? 0));
  assert.ok(gaps.every((gap) => gap >= 5_000), `gaps ${gaps}`);
  assert.equal(report?.store, 'dunnes');
  assert.equal(report?.read, 3);
  assert.deepEqual(ok.posts[0]?.[0], { store: 'dunnes', url: dunnes(1).url, title: 'Dunnes Stores Irish Whole Milk 2L', price: 2.2, inStock: true, category: 'Dairy & Eggs > Milk' });

  const refused = harness((url) => (url.includes('item-1') ? dunnesPage : challenged));
  const reports = await run([dunnes(1), dunnes(2), dunnes(3), tesco(5555)], refused.deps);
  const d = reports.find((r) => r.store === 'dunnes');
  assert.equal(refused.loads.filter((l) => l.url.includes('dunnes')).length, 2, 'the challenged URL is asked once, the one after it not at all');
  assert.equal(d?.blocked?.notAsked, 1);
  assert.match(d?.blocked?.why ?? '', /HTTP 403/);
  assert.equal(refused.posts[0]?.length, 1, 'the reading before the challenge is still posted');
  assert.ok(reports.some((r) => r.store === 'tesco'), 'another store is still tried');
});
