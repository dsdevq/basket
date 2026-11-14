import assert from 'node:assert/strict';
import { mkdtemp, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { parseProbeArgs } from '../src/probe.ts';
import { findBarcode, findProductRecord, readRobots, readSitemap, trimRecord } from '../src/probe/analyse.ts';
import { jsonEndpoints, pagingParameters } from '../src/probe/discover.ts';
import { writeReport } from '../src/probe/report.ts';
import { probe, type Inspected, type ProbeDeps } from '../src/probe/run.ts';

// Everything here is invented: no page, record or barcode below was captured from a shop.
const ROBOTS = 'User-agent: *\nDisallow: /basket\nDisallow: /account/\nCrawl-delay: 2\nSitemap: https://www.dunnesstoresgrocery.com/sitemap.xml\n';
const SITEMAP = '<?xml version="1.0"?><sitemapindex><sitemap><loc>https://x.test/a.xml</loc></sitemap><sitemap><loc>https://x.test/b.xml</loc></sitemap></sitemapindex>';
const HOME = '<html><head><title>Shop</title></head><body><a href="/product/skipped-100000001">p</a><a href="/category/fruit">Fruit</a><a href="https://other.test/category/no">x</a></body></html>';
const CATEGORY = '<html><head><title>Fruit</title></head><body><a href="/product/test-apple-100000002">Apple</a><a href="/category/fruit?page=2">next</a>' +
  '<script>window.__PRELOADED_STATE__ = {"listing":{"total":120,"pageSize":24,"page":1}};</script></body></html>';
const PRODUCT = '<html><head><title>Apple</title></head><body><script id="data" type="application/json">' +
  JSON.stringify({ product: { sku: 100000002, name: 'Test Apple', price: 1.5, gtin13: '5000000000017', images: ['a', 'b', 'c'], description: 'x'.repeat(200) } }) +
  '</script></body></html>';

function page(body: string, extra: Partial<Inspected> = {}): Inspected {
  return { status: 200, contentType: 'text/html', body, requests: [], json: [], ...extra };
}

function deps(pages: Record<string, Inspected>): ProbeDeps & { asked: string[]; slept: number[] } {
  const asked: string[] = [];
  const slept: number[] = [];
  let clock = 0;
  return {
    asked,
    slept,
    async inspect(url) {
      asked.push(url);
      const found = pages[new URL(url).pathname];
      if (!found) {
        throw new Error(`unexpected ${url}`);
      }
      return found;
    },
    sleep: async (ms) => {
      slept.push(ms);
      clock += ms;
    },
    now: () => clock,
    log: () => undefined,
  };
}

const ALL: Record<string, Inspected> = {
  '/robots.txt': page(ROBOTS, { contentType: 'text/plain' }),
  '/sitemap.xml': page(SITEMAP, { contentType: 'application/xml' }),
  '/': page(HOME, { requests: [{ url: 'https://x.test/api/nav?lang=en', method: 'GET', resourceType: 'fetch', status: 200, contentType: 'application/json' }] }),
  '/category/fruit': page(CATEGORY, {
    requests: [{ url: 'https://x.test/api/list?page=1&pageSize=24', method: 'GET', resourceType: 'xhr', status: 200, contentType: 'application/json' }],
  }),
  '/product/test-apple-100000002': page(PRODUCT),
};

test('the whole probe: five addresses in order, 5 s apart, one at a time', async () => {
  const d = deps(ALL);
  const report = await probe('dunnes', d);
  assert.deepEqual(d.asked, [
    'https://www.dunnesstoresgrocery.com/robots.txt',
    'https://www.dunnesstoresgrocery.com/sitemap.xml',
    'https://www.dunnesstoresgrocery.com/',
    'https://www.dunnesstoresgrocery.com/category/fruit',
    'https://www.dunnesstoresgrocery.com/product/test-apple-100000002',
  ]);
  assert.deepEqual(d.slept, [5000, 5000, 5000, 5000]);
  assert.equal(report.blocked, undefined);
  assert.deepEqual(report.robots, { disallow: ['/basket', '/account/'], sitemaps: ['https://www.dunnesstoresgrocery.com/sitemap.xml'], crawlDelay: 2 });
  assert.equal(report.sitemap?.kind, 'index');
  assert.equal(report.sitemap?.entries, 2);
  assert.equal(report.endpoints.home?.[0]?.url, 'https://x.test/api/nav?lang=en');
  assert.deepEqual(report.pagination?.parameters, ['page', 'pageSize']);
  assert.ok(report.pagination?.totals.some((t) => t.value === 120));
  assert.ok(report.pagination?.totals.some((t) => t.value === 24));
  assert.equal(report.product?.barcode.present, true);
  assert.equal(report.product?.barcode.example, '5000000000017');
  assert.ok(report.product?.fields.includes('gtin13'));
});

test('the first refusal ends the probe: nothing after it is asked, nothing is retried', async () => {
  const d = deps({ ...ALL, '/': page('<html><head><title>Just a moment...</title></head></html>', { status: 403 }) });
  const report = await probe('dunnes', d);
  assert.equal(d.asked.length, 3);
  assert.equal(report.blocked?.step, 'home');
  assert.equal(report.blocked?.why, 'HTTP 403');
  assert.deepEqual(report.blocked?.notAsked, ['category', 'product']);
  assert.equal(report.product, undefined);
});

test('a refused robots.txt stops before the sitemap', async () => {
  const d = deps({ ...ALL, '/robots.txt': page('Access Denied', { status: 403 }) });
  const report = await probe('tesco', d);
  assert.deepEqual(d.asked, ['https://www.tesco.ie/robots.txt']);
  assert.equal(report.blocked?.step, 'robots');
});

test('a missing sitemap (404) is a fact, not a refusal', async () => {
  const d = deps({ ...ALL, '/sitemap.xml': page('not found', { status: 404 }) });
  const report = await probe('dunnes', d);
  assert.equal(report.blocked, undefined);
  assert.equal(report.steps[1]?.status, 404);
  assert.equal(report.steps.length, 5);
});

test('no category link on the home page is noted and the later steps carry on without it', async () => {
  const d = deps({ ...ALL, '/': page('<html><head><title>Shop</title></head><body></body></html>') });
  const report = await probe('dunnes', d);
  assert.equal(report.steps[3]?.note, 'no category address found on the previous page');
  assert.equal(report.steps[4]?.note, 'no product address found on the previous page');
});

test('a page that does not load is recorded and the probe goes on', async () => {
  const report = await probe('dunnes', deps({ '/robots.txt': ALL['/robots.txt']! }));
  assert.match(report.steps[1]?.note ?? '', /did not load: unexpected/);
  assert.equal(report.steps.length, 5);
});

test('the Tesco shapes are picked from links', async () => {
  const home = '<title>t</title><a href="/groceries/en-IE/shop/fresh-food/all">Fresh</a>';
  const cat = '<title>c</title><a href="/groceries/en-IE/products/111222333">Milk</a>';
  const d = deps({
    '/robots.txt': page(''),
    '/sitemap.xml': page(''),
    '/': page(home),
    '/groceries/en-IE/shop/fresh-food/all': page(cat),
    '/groceries/en-IE/products/111222333': page('<title>p</title>'),
  });
  await probe('tesco', d);
  assert.equal(d.asked.at(-1), 'https://www.tesco.ie/groceries/en-IE/products/111222333');
});

test('discovery keeps the page\'s own JSON/XHR calls and drops assets and the document', () => {
  const found = jsonEndpoints([
    { url: 'https://x.test/', method: 'GET', resourceType: 'document', status: 200, contentType: 'text/html' },
    { url: 'https://x.test/app.js', method: 'GET', resourceType: 'script', status: 200, contentType: 'application/javascript' },
    { url: 'https://x.test/api/a?page=2', method: 'GET', resourceType: 'fetch', status: 200, contentType: 'text/plain' },
    { url: 'https://x.test/api/b', method: 'POST', resourceType: 'xhr', status: 200, contentType: 'application/json; charset=utf-8' },
    { url: 'https://x.test/api/b', method: 'POST', resourceType: 'xhr', status: 200, contentType: 'application/json' },
  ]);
  assert.deepEqual(found.map((e) => `${e.method} ${e.url}`), ['GET https://x.test/api/a?page=2', 'POST https://x.test/api/b']);
  assert.deepEqual(pagingParameters(['https://x.test/api/a?page=2&q=milk&sort=asc', 'not a url']), ['page', 'sort']);
});

test('robots and sitemap readers', () => {
  assert.deepEqual(readRobots('disallow: \nDisallow: /x\nCrawl-delay: soon'), { disallow: ['/x'], sitemaps: [] });
  assert.deepEqual(readSitemap('<urlset><url><loc> https://x.test/1 </loc></url></urlset>'), { kind: 'urlset', entries: 1, sample: ['https://x.test/1'] });
  assert.equal(readSitemap('<html></html>').kind, 'unknown');
});

test('the example record is trimmed and the barcode is found by key and shape only', () => {
  const doc = { a: [{ nothing: 1 }], p: { title: 'T', price: { amount: 2 }, notes: 'y'.repeat(300), tags: [1, 2, 3, 4], ean: '12345678', deep: { a: { b: { c: { d: 1 } } } } } };
  const hit = findProductRecord(doc);
  assert.equal(hit?.path, '$.p');
  const trimmed = trimRecord(hit?.record) as Record<string, unknown>;
  assert.equal((trimmed['notes'] as string).length, 83);
  assert.deepEqual(trimmed['tags'], [1, 2, '... 2 more']);
  assert.equal(findBarcode([{ source: 's', value: doc }]).present, true);
  assert.equal(findBarcode([{ source: 's', value: { gtin: 'abc', barcode: '' } }]).present, false);
});

test('the report goes to stdout, or to a file with its folders made', async () => {
  let out = '';
  await writeReport({ ok: true }, undefined, (text) => (out += text));
  assert.equal(out, '{\n  "ok": true\n}\n');
  const dir = await mkdtemp(join(tmpdir(), 'probe-'));
  await writeReport({ ok: 1 }, join(dir, 'sub', 'r.json'));
  assert.deepEqual(JSON.parse(await readFile(join(dir, 'sub', 'r.json'), 'utf8')), { ok: 1 });
});

test('the arguments: a known store, an optional file, nothing else', () => {
  assert.deepEqual(parseProbeArgs(['--store', 'tesco']), { store: 'tesco' });
  assert.equal(parseProbeArgs(['--store=dunnes', '--out', 'r.json']).store, 'dunnes');
  assert.throws(() => parseProbeArgs([]), /Name the store/);
  assert.throws(() => parseProbeArgs(['--store', 'aldi']), /Name the store/);
  assert.throws(() => parseProbeArgs(['--store', 'tesco', '--post']), /Unknown argument/);
});
