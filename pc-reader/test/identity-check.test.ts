import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { Config } from '../src/config.ts';
import { checkIdentity } from '../src/identity-check.ts';

const ISSUER = 'https://identity.example.ts.net:3001/oidc';
const RESOURCE = 'https://fleet.lifekit.internal/api';

function config(issuer = ISSUER): Config {
  return {
    origin: 'https://basket.example.ts.net:10000',
    auth: { kind: 'fleet', issuer, clientId: 'cid', clientSecret: 's3cr3t', resource: RESOURCE },
    urlsFile: 'urls.local.json',
    profileDir: 'profile',
    channel: 'chrome',
    headless: true,
  };
}

function jwt(claims: Record<string, unknown>): string {
  const part = (value: unknown) => Buffer.from(JSON.stringify(value)).toString('base64url');
  return `${part({ alg: 'ES384' })}.${part(claims)}.signature`;
}

const goodClaims = { iss: ISSUER, aud: RESOURCE, scope: 'basket:price-ingest basket:catalog-read' };

/** Answers by URL: discovery, token and the catalog read; each can be overridden. */
function world(overrides: { discovery?: () => Response; claims?: Record<string, unknown>; basket?: number } = {}) {
  const calls: string[] = [];
  const fetchImpl = (async (url: string | URL) => {
    const target = String(url);
    calls.push(target);
    if (target === `${ISSUER}/.well-known/openid-configuration`) {
      return overrides.discovery?.() ?? new Response(JSON.stringify({ issuer: ISSUER }), { status: 200 });
    }
    if (target === `${ISSUER}/token`) {
      return new Response(JSON.stringify({ access_token: jwt(overrides.claims ?? goodClaims), expires_in: 3600 }), { status: 200 });
    }
    if (target === 'https://basket.example.ts.net:10000/api/products') {
      return new Response('{}', { status: overrides.basket ?? 200 });
    }
    throw new Error(`unexpected ${target}`);
  }) as unknown as typeof fetch;
  return { calls, fetchImpl };
}

test('check: discovery, token and basket all pass over an https issuer', async () => {
  const { fetchImpl } = world();
  const steps = await checkIdentity(config(), { fetchImpl });
  assert.deepEqual(steps.map((s) => [s.name, s.ok]), [['issuer address', true], ['discovery', true], ['token', true], ['basket', true]]);
});

test('check: a loopback issuer is reported as tunnel-dependent and nothing is requested', async () => {
  const { calls, fetchImpl } = world();
  const steps = await checkIdentity(config('http://127.0.0.1:3001/oidc'), { fetchImpl });
  assert.equal(steps.length, 1);
  assert.equal(steps[0]?.ok, false);
  assert.match(steps[0]?.detail ?? '', /ssh tunnel/);
  assert.deepEqual(calls, []);
});

test('check: an issuer that differs from the one the identity service reports fails at discovery', async () => {
  const { calls, fetchImpl } = world({ discovery: () => new Response(JSON.stringify({ issuer: 'https://other.example/oidc' }), { status: 200 }) });
  const steps = await checkIdentity(config(), { fetchImpl });
  assert.equal(steps.at(-1)?.name, 'discovery');
  assert.match(steps.at(-1)?.detail ?? '', /reports its issuer as https:\/\/other\.example\/oidc/);
  assert.ok(!calls.some((c) => c.endsWith('/token')));
});

test('check: an unreachable issuer fails at discovery with the reason', async () => {
  const fetchImpl = (async () => {
    throw new TypeError('fetch failed');
  }) as unknown as typeof fetch;
  const steps = await checkIdentity(config(), { fetchImpl });
  assert.equal(steps.at(-1)?.ok, false);
  assert.match(steps.at(-1)?.detail ?? '', /could not reach .*fetch failed/);
});

test('check: a token whose iss, audience or scope is wrong fails at the token step', async () => {
  const { fetchImpl } = world({ claims: { iss: 'http://logto:3001/oidc', aud: 'https://elsewhere', scope: 'basket:price-ingest' } });
  const steps = await checkIdentity(config(), { fetchImpl });
  const token = steps.at(-1);
  assert.equal(token?.name, 'token');
  assert.equal(token?.ok, false);
  assert.match(token?.detail ?? '', /iss is http:\/\/logto:3001\/oidc/);
  assert.match(token?.detail ?? '', /audience is https:\/\/elsewhere/);
  assert.match(token?.detail ?? '', /scope lacks basket:catalog-read/);
});

test('check: basket refusing the token fails at the last step, naming the authority', async () => {
  const { fetchImpl } = world({ basket: 401 });
  const steps = await checkIdentity(config(), { fetchImpl });
  assert.equal(steps.at(-1)?.name, 'basket');
  assert.equal(steps.at(-1)?.ok, false);
  assert.match(steps.at(-1)?.detail ?? '', /HTTP 401.*FLEET_AGENT_AUTHORITY/);
});

test('check: prints no token or secret in any step', async () => {
  const { fetchImpl } = world();
  const steps = await checkIdentity(config(), { fetchImpl });
  const text = JSON.stringify(steps);
  assert.ok(!text.includes('s3cr3t'));
  assert.ok(!text.includes('signature'));
});

test('check: the bsk_ token has no issuer to check', async () => {
  const steps = await checkIdentity({ ...config(), auth: { kind: 'token', token: 'bsk_x' } });
  assert.equal(steps[0]?.ok, false);
});
