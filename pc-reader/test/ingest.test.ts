import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { postBatch } from '../src/ingest.ts';
import { loadConfig, parseEnvFile } from '../src/config.ts';
import type { Reading } from '../src/types.ts';

const reading: Reading = { store: 'aldi', url: 'https://www.aldi.ie/product/x-000000000000422541', price: 1.49, inStock: true };

function stub(status: number, body: unknown) {
  const calls: { url: string; init: RequestInit }[] = [];
  const fetchImpl = (async (url: URL, init: RequestInit) => {
    calls.push({ url: String(url), init });
    return new Response(JSON.stringify(body), { status });
  }) as unknown as typeof fetch;
  return { calls, fetchImpl };
}

test('the batch is POSTed as {"prices": [...]} to /api/ingest/prices with the bearer token', async () => {
  const { calls, fetchImpl } = stub(200, { accepted: 1, rejected: 0, results: [{ index: 0, accepted: true, reason: null }] });
  const outcome = await postBatch('https://basket.example.com', 'bsk_secret', [reading], fetchImpl);
  assert.equal(calls[0]?.url, 'https://basket.example.com/api/ingest/prices');
  const headers = calls[0]?.init.headers as Record<string, string>;
  assert.equal(headers['authorization'], 'Bearer bsk_secret');
  assert.deepEqual(JSON.parse(String(calls[0]?.init.body)), { prices: [reading] });
  assert.deepEqual(outcome, { status: 200, accepted: 1, rejected: [] });
});

test('the shape the reader posts matches the contract fixture the backend tests use', () => {
  const contract = JSON.parse(readFileSync(new URL('../../backend/tests/Basket.Tests/Fixtures/ingest-batch.json', import.meta.url), 'utf8')) as { prices: Record<string, unknown>[] };
  const allowed = new Set(['store', 'url', 'title', 'price', 'wasPrice', 'loyaltyPrice', 'loyaltyScheme', 'promoText', 'validFrom', 'validUntil', 'inStock']);
  for (const price of contract.prices) {
    assert.ok(Object.keys(price).every((key) => allowed.has(key)), JSON.stringify(price));
  }
  assert.deepEqual(new Set(Object.keys(contract.prices[0] ?? {})), allowed);
});

test('422 (nothing usable) still carries per-reading reasons; 401 and 429 are whole-batch refusals', async () => {
  const unusable = await postBatch('https://b.example', 'bsk_x', [reading], stub(422, { accepted: 0, rejected: 1, results: [{ index: 0, accepted: false, reason: 'unknown_listing' }] }).fetchImpl);
  assert.equal(unusable.error, undefined);
  assert.equal(unusable.rejected[0]?.reason, 'unknown_listing');

  const unauthorized = await postBatch('https://b.example', 'bsk_x', [reading], stub(401, { error: 'unauthorized', message: 'A valid price token is required.' }).fetchImpl);
  assert.equal(unauthorized.error, 'unauthorized: A valid price token is required.');
  const limited = await postBatch('https://b.example', 'bsk_x', [reading], stub(429, {}).fetchImpl);
  assert.equal(limited.error, 'HTTP 429');
});

test('config: environment wins over the local file; secrets are required, https is required off localhost', () => {
  assert.deepEqual(parseEnvFile('# c\nBASKET_API_ORIGIN="https://a.example"\nexport BASKET_INGEST_TOKEN=bsk_abc\n'), { BASKET_API_ORIGIN: 'https://a.example', BASKET_INGEST_TOKEN: 'bsk_abc' });
  const file = () => 'BASKET_API_ORIGIN=https://file.example\nBASKET_INGEST_TOKEN=bsk_file\n';
  const fromFile = loadConfig({}, file);
  assert.equal(fromFile.origin, 'https://file.example');
  assert.equal(loadConfig({ BASKET_API_ORIGIN: 'https://env.example/', BASKET_INGEST_TOKEN: 'bsk_env' }, file).token, 'bsk_env');
  assert.throws(() => loadConfig({}, () => { throw new Error('missing'); }), /BASKET_API_ORIGIN and BASKET_INGEST_TOKEN/);
  assert.throws(() => loadConfig({ BASKET_API_ORIGIN: 'http://basket.example', BASKET_INGEST_TOKEN: 'bsk_x' }, file), /https/);
  assert.equal(loadConfig({ BASKET_API_ORIGIN: 'http://localhost:3310', BASKET_INGEST_TOKEN: 'bsk_x' }, file).origin, 'http://localhost:3310');
  assert.throws(() => loadConfig({ BASKET_API_ORIGIN: 'https://a.example', BASKET_INGEST_TOKEN: 'nope' }, file), /bsk_/);
});
