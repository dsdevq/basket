import assert from 'node:assert/strict';
import { test } from 'node:test';
import { fetchCatalogWorkList, resolveWorkList } from '../src/catalog.ts';
import { loadConfig, type Auth } from '../src/config.ts';
import { bearerSource, REFRESH_MARGIN_MS } from '../src/fleet.ts';
import type { WorkItem } from '../src/types.ts';

const fleetAuth: Auth = { kind: 'fleet', issuer: 'http://127.0.0.1:3001/oidc', clientId: 'cid', clientSecret: 's3cr3t/+', resource: 'https://fleet.lifekit.internal/api' };

function tokenStub(replies: Response[]) {
  const calls: { url: string; init: RequestInit }[] = [];
  const fetchImpl = (async (url: string, init: RequestInit) => {
    calls.push({ url: String(url), init });
    const reply = replies.shift();
    if (!reply) {
      throw new Error('unexpected call');
    }
    return reply;
  }) as unknown as typeof fetch;
  return { calls, fetchImpl };
}

const grant = (token: string, expiresIn = 3600) => new Response(JSON.stringify({ access_token: token, expires_in: expiresIn, token_type: 'Bearer' }), { status: 200 });

test('fleet token: client credentials POSTed to <issuer>/token with the secret only in the Basic header', async () => {
  const { calls, fetchImpl } = tokenStub([grant('jwt-1')]);
  const bearer = bearerSource(fleetAuth, { fetchImpl, now: () => 0 });
  assert.equal(await bearer(), 'jwt-1');
  assert.equal(calls[0]?.url, 'http://127.0.0.1:3001/oidc/token');
  const headers = calls[0]?.init.headers as Record<string, string>;
  assert.equal(headers['authorization'], `Basic ${Buffer.from('cid:s3cr3t%2F%2B').toString('base64')}`);
  const form = new URLSearchParams(String(calls[0]?.init.body));
  assert.equal(form.get('grant_type'), 'client_credentials');
  assert.equal(form.get('resource'), 'https://fleet.lifekit.internal/api');
  assert.deepEqual(form.get('scope')?.split(' '), ['basket:price-ingest', 'basket:catalog-read']);
  assert.ok(!String(calls[0]?.init.body).includes('s3cr3t'));
});

test('fleet token: reused while valid, fetched again once within the refresh margin of expiry', async () => {
  const { calls, fetchImpl } = tokenStub([grant('jwt-1', 3600), grant('jwt-2', 3600)]);
  let clock = 0;
  const bearer = bearerSource(fleetAuth, { fetchImpl, now: () => clock });
  assert.equal(await bearer(), 'jwt-1');
  clock = 3600_000 - REFRESH_MARGIN_MS - 1;
  assert.equal(await bearer(), 'jwt-1');
  assert.equal(calls.length, 1);
  clock = 3600_000 - REFRESH_MARGIN_MS;
  assert.equal(await bearer(), 'jwt-2');
  assert.equal(calls.length, 2);
  assert.equal(await bearer(), 'jwt-2');
  assert.equal(calls.length, 2);
});

test('fleet token: concurrent callers share one request', async () => {
  const { calls, fetchImpl } = tokenStub([grant('jwt-1')]);
  const bearer = bearerSource(fleetAuth, { fetchImpl, now: () => 0 });
  assert.deepEqual(await Promise.all([bearer(), bearer(), bearer()]), ['jwt-1', 'jwt-1', 'jwt-1']);
  assert.equal(calls.length, 1);
});

test('fleet token: a refusal or a malformed answer is an error that never holds the secret, and the next call tries again', async () => {
  const { fetchImpl } = tokenStub([
    new Response(JSON.stringify({ error: 'invalid_client', error_description: 'bad credentials' }), { status: 401 }),
    new Response('{}', { status: 200 }),
    grant('jwt-ok'),
  ]);
  const bearer = bearerSource(fleetAuth, { fetchImpl, now: () => 0 });
  await assert.rejects(bearer(), (e: Error) => /HTTP 401, invalid_client: bad credentials/.test(e.message) && !e.message.includes('s3cr3t'));
  await assert.rejects(bearer(), /without an access_token/);
  assert.equal(await bearer(), 'jwt-ok');

  const unreachable = bearerSource(fleetAuth, { fetchImpl: (async () => { throw new Error('connect ECONNREFUSED'); }) as typeof fetch });
  await assert.rejects(unreachable(), (e: Error) => /ECONNREFUSED/.test(e.message) && !e.message.includes('s3cr3t'));
});

test('the bsk_ token is the bearer as it is, with no identity call', async () => {
  const bearer = bearerSource({ kind: 'token', token: 'bsk_abc' }, { fetchImpl: (async () => { throw new Error('no call expected'); }) as typeof fetch });
  assert.equal(await bearer(), 'bsk_abc');
});

test('config: the fleet identity from the environment; a half-set identity names what is missing; fleet wins over a bsk_ token', () => {
  const none = () => { throw new Error('missing'); };
  const base = { BASKET_API_ORIGIN: 'https://basket.example.com', BASKET_FLEET_CLIENT_ID: 'cid', BASKET_FLEET_CLIENT_SECRET: 'sec', BASKET_FLEET_ISSUER: 'https://id.example.com/oidc/' };
  assert.deepEqual(loadConfig(base, none).auth, { kind: 'fleet', issuer: 'https://id.example.com/oidc', clientId: 'cid', clientSecret: 'sec', resource: 'https://fleet.lifekit.internal/api' });
  assert.equal((loadConfig({ ...base, BASKET_FLEET_RESOURCE: 'https://other/api', BASKET_INGEST_TOKEN: 'bsk_x' }, none).auth as { resource: string }).resource, 'https://other/api');
  assert.equal(loadConfig({ ...base, BASKET_INGEST_TOKEN: 'bsk_x' }, none).auth.kind, 'fleet');
  assert.throws(() => loadConfig({ BASKET_API_ORIGIN: base.BASKET_API_ORIGIN, BASKET_FLEET_CLIENT_ID: 'cid' }, none), /BASKET_FLEET_CLIENT_SECRET, BASKET_FLEET_ISSUER/);
  assert.throws(() => loadConfig({ ...base, BASKET_FLEET_ISSUER: 'http://id.example.com/oidc' }, none), /BASKET_FLEET_ISSUER must be https/);
  assert.equal(loadConfig({ ...base, BASKET_FLEET_ISSUER: 'http://127.0.0.1:3001/oidc' }, none).auth.kind, 'fleet');
  assert.throws(() => loadConfig({ ...base, BASKET_FLEET_CLIENT_SECRET: '' , BASKET_FLEET_CLIENT_ID: '', BASKET_FLEET_ISSUER: '' }, none), /BASKET_INGEST_TOKEN/);
});

const catalogBody = {
  products: [
    {
      id: 'p1',
      name: 'Milk',
      stores: [
        { store: 'aldi', readBy: 'pc', listing: { url: 'https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541?x=1' } },
        { store: 'tesco', readBy: 'pc', listing: null },
        { store: 'lidl', readBy: 'nightly', listing: { url: 'https://www.lidl.ie/p/milk/p1' } },
      ],
    },
    {
      id: 'p2',
      name: 'Swede',
      stores: [
        { store: 'dunnes', readBy: 'pc', listing: { url: 'https://www.dunnesstoresgrocery.com/product/dunnes-stores-swede-100317581' } },
        { store: 'tesco', readBy: 'pc', listing: { url: 'https://evil.example/groceries/en-IE/products/254317390' } },
        { store: 'aldi', readBy: 'pc', listing: { url: 'https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541' } },
      ],
    },
  ],
  stores: [],
};

test('catalog: GET /api/products with the fleet bearer; only PC-read, linked, well-formed shop pages, de-duplicated', async () => {
  const calls: { url: string; init: RequestInit }[] = [];
  const fetchImpl = (async (url: URL, init: RequestInit) => {
    calls.push({ url: String(url), init });
    return new Response(JSON.stringify(catalogBody), { status: 200 });
  }) as unknown as typeof fetch;
  const { items, skipped } = await fetchCatalogWorkList('https://basket.example.com', async () => 'jwt-1', fetchImpl);
  assert.equal(calls[0]?.url, 'https://basket.example.com/api/products');
  assert.equal((calls[0]?.init.headers as Record<string, string>)['authorization'], 'Bearer jwt-1');
  assert.deepEqual(items, [
    { store: 'aldi', url: 'https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541' },
    { store: 'dunnes', url: 'https://www.dunnesstoresgrocery.com/product/dunnes-stores-swede-100317581' },
  ]);
  assert.equal(skipped, 1);
});

test('catalog: a refusal, a bad body or a token failure is an error', async () => {
  const answer = (status: number, body: string) => (async () => new Response(body, { status })) as unknown as typeof fetch;
  await assert.rejects(fetchCatalogWorkList('https://b.example', async () => 't', answer(403, '{}')), /HTTP 403/);
  await assert.rejects(fetchCatalogWorkList('https://b.example', async () => 't', answer(200, '{"nope":1}')), /without a product list/);
  await assert.rejects(fetchCatalogWorkList('https://b.example', async () => { throw new Error('identity down'); }, answer(200, '{}')), /identity down/);
});

const fromFile: WorkItem[] = [{ store: 'tesco', url: 'https://www.tesco.ie/groceries/en-IE/products/254317390' }];
const fromCatalog: WorkItem[] = [{ store: 'aldi', url: 'https://www.aldi.ie/product/x-000000000000422541' }];

test('work list: the catalog wins and the file is not read; an empty catalog is an answer, not a failure', async () => {
  const logs: string[] = [];
  const file = async () => { throw new Error('the file must not be read'); };
  assert.deepEqual(await resolveWorkList({ catalog: async () => ({ items: fromCatalog, skipped: 0 }), file, log: (l: string) => logs.push(l) }), fromCatalog);
  assert.deepEqual(await resolveWorkList({ catalog: async () => ({ items: [], skipped: 0 }), file, log: (l: string) => logs.push(l) }), []);
});

test('work list: the file only when the catalog is not configured or the call fails', async () => {
  const logs: string[] = [];
  const file = async () => fromFile;
  assert.deepEqual(await resolveWorkList({ file, log: (l: string) => logs.push(l) }), fromFile);
  assert.equal(logs.length, 0);
  const failed = await resolveWorkList({ catalog: async () => { throw new Error('basket answered the catalog read with HTTP 502.'); }, file, log: (l: string) => logs.push(l) });
  assert.deepEqual(failed, fromFile);
  assert.match(logs[0] ?? '', /HTTP 502.*falling back to the URL file/);
  await assert.rejects(
    resolveWorkList({ catalog: async () => { throw new Error('HTTP 502'); }, file: async () => { throw new Error('Cannot read the URL file x'); }, log: () => undefined }),
    /catalog call failed \(HTTP 502\) and Cannot read the URL file x/,
  );
});
