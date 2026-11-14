import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { Config } from '../src/config.ts';
import type { CheckStep } from '../src/identity-check.ts';
import type { StoreReport } from '../src/run.ts';
import { localDay, runDaily, summarize, type DailyDeps } from '../src/schedule.ts';

const CONFIG = { origin: 'https://basket.example', auth: { kind: 'token', token: 'bsk_x' } } as unknown as Config;
const PASSING: CheckStep[] = [{ name: 'basket', ok: true, detail: 'accepts the token' }];

const aldi: StoreReport = { store: 'aldi', planned: 4, read: 4, unreadable: [], posted: { batches: 1, accepted: 4, rejected: [] } };
const dunnes: StoreReport = {
  store: 'dunnes',
  planned: 3,
  read: 0,
  unreadable: [],
  blocked: { url: 'https://www.dunnesstoresgrocery.com/product/x-1', why: 'challenge page "Just a moment..."', notAsked: 2 },
  posted: { batches: 0, accepted: 0, rejected: [] },
};

/** A day with nothing stamped yet, passing checks and a clean read; each can be overridden. */
function day(overrides: Partial<DailyDeps> = {}) {
  const lines: string[] = [];
  const stamps: string[] = [];
  const calls: string[] = [];
  const deps: DailyDeps = {
    now: () => new Date(2026, 10, 14, 12, 0, 0),
    readStamp: () => undefined,
    writeStamp: (d) => stamps.push(d),
    log: (line) => lines.push(line),
    loadConfig: () => CONFIG,
    check: async () => {
      calls.push('check');
      return PASSING;
    },
    read: async () => {
      calls.push('read');
      return { code: 0, reports: [aldi] };
    },
    ...overrides,
  };
  return { deps, lines, stamps, calls };
}

test('a clean day: checks, reads, stamps the day, one line, exit 0', async () => {
  const { deps, lines, stamps, calls } = day();
  assert.equal(await runDaily(deps), 0);
  assert.deepEqual(calls, ['check', 'read']);
  assert.deepEqual(stamps, ['2026-11-14']);
  assert.deepEqual(lines, ['read: aldi 4/4 read, 4 accepted (reader exit 0)']);
});

test('already read today: nothing is checked or read, one line, exit 0', async () => {
  const { deps, lines, stamps, calls } = day({ readStamp: () => '2026-11-14' });
  assert.equal(await runDaily(deps), 0);
  assert.deepEqual(calls, []);
  assert.deepEqual(stamps, []);
  assert.deepEqual(lines, ['skipped: already read today (2026-11-14)']);
});

test('a stamp from yesterday does not block today (a missed day is simply caught up)', async () => {
  const { deps, calls } = day({ readStamp: () => '2026-11-13' });
  assert.equal(await runDaily(deps), 0);
  assert.deepEqual(calls, ['check', 'read']);
});

test('basket or the issuer unreachable: skipped quietly, exit 0, not stamped, no browser', async () => {
  const unreachable: CheckStep[] = [
    { name: 'discovery', ok: false, detail: 'could not reach https://vps.ts.net:3001/oidc: fetch failed', unreachable: true },
  ];
  const { deps, lines, stamps, calls } = day({ check: async () => unreachable });
  assert.equal(await runDaily(deps), 0);
  assert.deepEqual(calls, []);
  assert.deepEqual(stamps, []);
  assert.equal(lines.length, 1);
  assert.match(lines[0] ?? '', /^skipped: basket or the identity service is unreachable.*discovery: could not reach/);
});

test('a check that fails for another reason is a failure (exit 1), not a quiet skip', async () => {
  const refused: CheckStep[] = [{ name: 'basket', ok: false, detail: 'answered GET /api/products with HTTP 401' }];
  const { deps, lines, stamps, calls } = day({ check: async () => refused });
  assert.equal(await runDaily(deps), 1);
  assert.deepEqual(calls, []);
  assert.deepEqual(stamps, []);
  assert.match(lines[0] ?? '', /^failed: check basket: .*HTTP 401/);
});

test('a missing setting is a failure with the reason', async () => {
  const { deps, lines } = day({
    loadConfig: () => {
      throw new Error('Set BASKET_API_ORIGIN in the environment');
    },
  });
  assert.equal(await runDaily(deps), 1);
  assert.deepEqual(lines, ['failed: Set BASKET_API_ORIGIN in the environment']);
});

test('a blocked store is a normal outcome: exit 0, stamped, the block is in the line', async () => {
  const { deps, lines, stamps } = day({ read: async () => ({ code: 2, reports: [aldi, dunnes] }) });
  assert.equal(await runDaily(deps), 0);
  assert.deepEqual(stamps, ['2026-11-14']);
  assert.equal(lines.length, 1);
  assert.match(lines[0] ?? '', /aldi 4\/4 read, 4 accepted; dunnes blocked \(challenge page "Just a moment\.\.\."; 2 not asked\), 0\/3 read, 0 accepted \(reader exit 2\)/);
});

test('the reader failing outright (exit 1) is a failure and leaves the day open for a retry', async () => {
  const { deps, lines, stamps } = day({ read: async () => ({ code: 1, reports: undefined }) });
  assert.equal(await runDaily(deps), 1);
  assert.deepEqual(stamps, []);
  assert.deepEqual(lines, ['failed: the reader exited 1: no summary printed']);
});

test('summarize: unreadable, rejected and failed posts are all named', () => {
  const text = summarize([
    { ...aldi, read: 3, unreadable: [{ url: 'u', why: 'w' }], posted: { batches: 1, accepted: 2, rejected: [{ url: 'u', reason: null }], error: 'HTTP 429' } },
  ]);
  assert.equal(text, 'aldi 3/4 read, 2 accepted, 1 unreadable, 1 rejected, post failed: HTTP 429');
  assert.equal(summarize([]), 'nothing to read');
});

test('localDay is the local calendar day, zero-padded', () => {
  assert.equal(localDay(new Date(2026, 0, 5, 23, 59)), '2026-01-05');
});
