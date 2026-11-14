import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { PostOutcome } from '../src/ingest.ts';
import { run, type Deps } from '../src/run.ts';
import { STORES, type Page, type Reading, type WorkItem } from '../src/types.ts';
import { fixture } from './support.ts';

const aldi = (n: number): WorkItem => ({ store: 'aldi', url: `https://www.aldi.ie/product/item-${String(n).padStart(12, '0')}` });
const dunnes = (n: number): WorkItem => ({ store: 'dunnes', url: `https://www.dunnesstoresgrocery.com/product/item-${n}00000` });
const tesco = (n: number): WorkItem => ({ store: 'tesco', url: `https://www.tesco.ie/groceries/en-IE/products/${n}` });

/** The run with every store on, so the Tesco parser's paths stay covered whatever ENABLED_STORES says. */
const runAll = (items: WorkItem[], deps: Deps, minGapMs?: number) => run(items, deps, minGapMs, STORES);

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
/** The recorded Dunnes page, with the product's sku made the number at the end of the address asked for (the parser reads only the page's own product). */
const dunnesPageFor = (url: string): Page => ({ status: 200, html: fixture('dunnes-product-page.html').replace('"sku": "100836186"', `"sku": "${/(\d+)$/.exec(url)?.[1]}"`) });
const challenged: Page = { status: 403, html: fixture('cloudflare-just-a-moment.html') };
const denied: Page = { status: 200, html: fixture('akamai-access-denied.html') };

test('pages on one site are loaded one at a time, at least 5 seconds apart', async () => {
  const { deps, loads, posts } = harness(() => aldiPage);
  const [report] = await runAll([aldi(1), aldi(2), aldi(3)], deps);

  assert.deepEqual(loads.map((l) => l.inFlight), [1, 1, 1]);
  const gaps = loads.slice(1).map((l, i) => l.at - (loads[i]?.at ?? 0));
  assert.ok(gaps.every((gap) => gap >= 5_000), `gaps ${gaps}`);
  assert.equal(report?.read, 3);
  assert.equal(posts.length, 1);
  assert.equal(posts[0]?.length, 3);
});

test('a refusal stops that store at once, is not retried, and keeps what was read before it', async () => {
  const { deps, loads, posts } = harness((url) => (url.endsWith('2') ? denied : aldiPage));
  const reports = await runAll([aldi(1), aldi(2), aldi(3), aldi(4), tesco(5555)], deps);

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
  const reports = await runAll([tesco(1111), tesco(2222), aldi(1)], deps);
  const tescoReport = reports.find((r) => r.store === 'tesco');
  assert.equal(tescoReport?.blocked?.notAsked, 1);
  assert.equal(tescoReport?.posted.batches, 0);
  assert.equal(posts.length, 1);
  assert.equal(posts[0]?.[0]?.store, 'aldi');
});

test('an unreadable or failed page is skipped and reported, the rest still read', async () => {
  const { deps, posts } = harness((url) => (url.endsWith('2') ? new Error('timeout') : url.endsWith('3') ? { status: 200, html: '<title>x</title>' } : tescoPage));
  const [report] = await runAll([tesco(1111), tesco(2), tesco(3), tesco(4444)], deps);
  assert.equal(report?.read, 2);
  assert.equal(report?.unreadable.length, 2);
  assert.match(report?.unreadable[0]?.why ?? '', /did not load: timeout/);
  assert.equal(posts[0]?.length, 2);
});

test('batches are cut at 200 readings and a refusing server ends that store without retry', async () => {
  const many = Array.from({ length: 450 }, (_, i) => aldi(i + 1));
  const { deps, posts } = harness(() => aldiPage);
  await runAll(many, deps, 0);
  assert.deepEqual(posts.map((p) => p.length), [200, 200, 50]);

  const refusing = harness(() => aldiPage, () => ({ status: 401, accepted: 0, rejected: [], error: 'unauthorized: A valid price token is required.' }));
  const [report] = await runAll(many, refusing.deps, 0);
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
  const reports = await runAll([aldi(1), tesco(5555)], deps, 0);
  assert.deepEqual(reports.map((r) => r.store), ['aldi', 'tesco']);
  assert.equal(loads.length, 2);
  assert.deepEqual(reports.map((r) => r.read), [1, 1]);
  assert.ok(reports.every((r) => /fetch failed/.test(r.posted.error ?? '')));
});

test('per-reading refusals are reported against their URL', async () => {
  const { deps } = harness(() => aldiPage, (readings) => ({ status: 200, accepted: readings.length - 1, rejected: [{ index: 1, accepted: false, reason: 'unknown_listing' }] }));
  const [report] = await runAll([aldi(1), aldi(2)], deps);
  assert.deepEqual(report?.posted.rejected, [{ url: aldi(2).url, reason: 'unknown_listing' }]);
});

test('dunnes: pages are 5 seconds apart, the reading has the same shape as the others, and a challenge stops the store at once', async () => {
  const ok = harness((url) => dunnesPageFor(url));
  const [report] = await runAll([dunnes(1), dunnes(2), dunnes(3)], ok.deps);
  const gaps = ok.loads.slice(1).map((l, i) => l.at - (ok.loads[i]?.at ?? 0));
  assert.ok(gaps.every((gap) => gap >= 5_000), `gaps ${gaps}`);
  assert.equal(report?.store, 'dunnes');
  assert.equal(report?.read, 3);
  assert.deepEqual(ok.posts[0]?.[0], { store: 'dunnes', url: dunnes(1).url, title: 'Dunnes Stores Gold Kiwi 4pk', price: 3, inStock: true, category: 'Fresh Fruit > Tropical & Exotic Fruit > Kiwi', imageUrl: 'https://images.cdn.dunnesstoresgrocery.com/cell/100836186_1.jpg' });

  const refused = harness((url) => (url.includes('item-1') ? dunnesPageFor(url) : challenged));
  const reports = await runAll([dunnes(1), dunnes(2), dunnes(3), tesco(5555)], refused.deps);
  const d = reports.find((r) => r.store === 'dunnes');
  assert.equal(refused.loads.filter((l) => l.url.includes('dunnes')).length, 2, 'the challenged URL is asked once, the one after it not at all');
  assert.equal(d?.blocked?.notAsked, 1);
  assert.match(d?.blocked?.why ?? '', /HTTP 403/);
  assert.equal(refused.posts[0]?.length, 1, 'the reading before the challenge is still posted');
  assert.ok(reports.some((r) => r.store === 'tesco'), 'another store is still tried');
});

test('tesco is paused by default: its entries are skipped with one log line, aldi and dunnes are still read', async () => {
  const { deps, loads, posts } = harness((url) => (url.includes('aldi') ? aldiPage : url.includes('dunnes') ? dunnesPageFor(url) : tescoPage));
  const lines: string[] = [];
  deps.log = (line) => lines.push(line);
  const reports = await run([aldi(1), tesco(1111), tesco(2222), dunnes(1), tesco(3333)], deps, 0);

  assert.deepEqual(reports.map((r) => r.store), ['aldi', 'dunnes']);
  assert.ok(reports.every((r) => r.read === 1));
  assert.equal(loads.filter((l) => l.url.includes('tesco')).length, 0, 'tesco is never loaded');
  assert.ok(posts.flat().every((r) => r.store !== 'tesco'));
  assert.equal(posts.flat().length, 2);
  const skipLines = lines.filter((line) => line.startsWith('tesco: paused'));
  assert.equal(skipLines.length, 1, 'one line for the run, not one per entry');
  assert.match(skipLines[0] ?? '', /skipping 3 pages/);
});
