import { MAX_BATCH, type PostOutcome } from './ingest.ts';
import { readPage } from './parse/index.ts';
import { ENABLED_STORES, STORES, type Page, type Reading, type StoreId, type WorkItem } from './types.ts';

/** Page loads on one site are at least this far apart (the same politeness as the server's nightly Lidl job). */
export const MIN_GAP_MS = 5_000;

export interface Deps {
  load(url: string): Promise<Page>;
  post(readings: Reading[]): Promise<PostOutcome>;
  sleep(ms: number): Promise<void>;
  now(): number;
  log(line: string): void;
}

export interface StoreReport {
  store: StoreId;
  planned: number;
  /** Pages that gave a reading. */
  read: number;
  /** Pages that loaded but could not be read, or did not load. Nothing is posted for them. */
  unreadable: { url: string; why: string }[];
  /** Set when the store refused or challenged this PC: that store was stopped for the run, nothing was retried. */
  blocked?: { url: string; why: string; notAsked: number };
  /** The server's answer to what was posted. */
  posted: { batches: number; accepted: number; rejected: { url: string; reason: string | null }[]; error?: string };
}

/**
 * One run over the work list. Sites are read one after the other, one page at a time, at least MIN_GAP_MS between loads
 * on the same site. A refusal ends that site's part of the run (the rest is not asked) and is never retried; readings
 * gathered before it are still posted. A site's readings are posted when its pages are done, so a later site cannot
 * lose them. Entries for a store that is not in `enabled` are skipped, with one log line for the run (not an error).
 */
export async function run(items: WorkItem[], deps: Deps, minGapMs = MIN_GAP_MS, enabled: readonly StoreId[] = ENABLED_STORES): Promise<StoreReport[]> {
  const reports: StoreReport[] = [];
  for (const store of STORES) {
    if (!enabled.includes(store)) {
      const skipped = items.filter((item) => item.store === store).length;
      if (skipped > 0) {
        deps.log(`${store}: paused, skipping ${skipped} page${skipped === 1 ? '' : 's'} (store not in ENABLED_STORES)`);
      }
      continue;
    }
    const mine = items.filter((item) => item.store === store);
    if (mine.length === 0) {
      continue;
    }
    reports.push(await runStore(store, mine, deps, minGapMs));
  }
  return reports;
}

async function runStore(store: StoreId, items: WorkItem[], deps: Deps, minGapMs: number): Promise<StoreReport> {
  const report: StoreReport = { store, planned: items.length, read: 0, unreadable: [], posted: { batches: 0, accepted: 0, rejected: [] } };
  const readings: Reading[] = [];
  let lastLoad: number | undefined;

  for (const [index, item] of items.entries()) {
    if (lastLoad !== undefined) {
      const wait = lastLoad + minGapMs - deps.now();
      if (wait > 0) {
        await deps.sleep(wait);
      }
    }
    lastLoad = deps.now();

    let result;
    try {
      result = readPage(store, item.url, await deps.load(item.url));
    } catch (error) {
      result = { kind: 'unreadable' as const, why: `did not load: ${error instanceof Error ? error.message : String(error)}` };
    }

    if (result.kind === 'blocked') {
      report.blocked = { url: item.url, why: result.why, notAsked: items.length - index - 1 };
      deps.log(`${store}: BLOCKED (${result.why}) at ${item.url}; stopping ${store} for this run`);
      break;
    }
    if (result.kind === 'unreadable') {
      report.unreadable.push({ url: item.url, why: result.why });
      deps.log(`${store}: unreadable (${result.why}) ${item.url}`);
      continue;
    }
    report.read++;
    readings.push(result.reading);
    deps.log(`${store}: read ${item.url}`);
  }

  for (let start = 0; start < readings.length; start += MAX_BATCH) {
    const batch = readings.slice(start, start + MAX_BATCH);
    let outcome: PostOutcome;
    try {
      outcome = await deps.post(batch);
    } catch (error) {
      report.posted.error = `did not post: ${error instanceof Error ? error.message : String(error)}`;
      deps.log(`${store}: ${report.posted.error}`);
      break;
    }
    report.posted.batches++;
    report.posted.accepted += outcome.accepted;
    for (const refused of outcome.rejected) {
      report.posted.rejected.push({ url: batch[refused.index]?.url ?? `#${refused.index}`, reason: refused.reason });
    }
    if (outcome.error) {
      report.posted.error = `HTTP ${outcome.status} ${outcome.error}`;
      deps.log(`${store}: the server refused the batch (${report.posted.error})`);
      break;
    }
  }
  return report;
}
