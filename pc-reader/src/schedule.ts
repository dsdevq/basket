import type { Config } from './config.ts';
import type { CheckStep } from './identity-check.ts';
import type { StoreReport } from './run.ts';

/** What a finished `main.ts` run came to: its exit code (see main.ts) and the JSON summary it printed, if it printed one. */
export interface ReaderOutcome {
  code: number;
  reports: StoreReport[] | undefined;
}

export interface DailyDeps {
  now: () => Date;
  /** The local day (YYYY-MM-DD) of the last run that finished, if any. */
  readStamp: () => string | undefined;
  writeStamp: (day: string) => void;
  /** The one line this attempt came to: printed, and appended to the log file. */
  log: (line: string) => void;
  loadConfig: () => Config;
  check: (config: Config) => Promise<CheckStep[]>;
  read: () => Promise<ReaderOutcome>;
}

/** The local calendar day, the unit "once a day" is counted in. */
export function localDay(date: Date): string {
  const two = (n: number): string => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${two(date.getMonth() + 1)}-${two(date.getDate())}`;
}

/** Each store's outcome in a few words: "aldi 4/4 read, 4 accepted", "dunnes blocked (challenge page; 2 not asked)". */
export function summarize(reports: StoreReport[]): string {
  if (reports.length === 0) {
    return 'nothing to read';
  }
  return reports
    .map((r) => {
      const parts = [`${r.read}/${r.planned} read`, `${r.posted.accepted} accepted`];
      if (r.blocked) {
        parts.unshift(`blocked (${r.blocked.why}; ${r.blocked.notAsked} not asked)`);
      }
      if (r.unreadable.length > 0) {
        parts.push(`${r.unreadable.length} unreadable`);
      }
      if (r.posted.rejected.length > 0) {
        parts.push(`${r.posted.rejected.length} rejected`);
      }
      if (r.posted.error) {
        parts.push(`post failed: ${r.posted.error}`);
      }
      return `${r.store} ${parts.join(', ')}`;
    })
    .join('; ');
}

/**
 * One scheduled attempt, at most once per local day. It ends in exactly one log line and returns the process exit code:
 *
 * - already read today: skipped, exit 0 (this is what stops a second trigger from reading twice);
 * - basket or the identity service gave no answer (Tailscale down, VPS off): skipped, exit 0, and no stamp, so the next
 *   trigger tries again;
 * - the check failed for any other reason (a refused token, a wrong issuer, no fleet identity configured): failed, exit 1,
 *   because that will not fix itself and should show as a failed unit;
 * - otherwise the reader runs. Exit 0 or 2 from it (a blocked store or an unreadable page is a normal outcome, not a
 *   failure) is "read", exit 0 and the day is stamped. Exit 1 from it (could not start, basket refused a batch) is
 *   "failed", exit 1, no stamp.
 */
export async function runDaily(deps: DailyDeps): Promise<number> {
  const day = localDay(deps.now());
  if (deps.readStamp() === day) {
    deps.log(`skipped: already read today (${day})`);
    return 0;
  }

  let config: Config;
  try {
    config = deps.loadConfig();
  } catch (error) {
    deps.log(`failed: ${error instanceof Error ? error.message : String(error)}`);
    return 1;
  }

  const failed = (await deps.check(config)).find((step) => !step.ok);
  if (failed?.unreachable) {
    deps.log(`skipped: basket or the identity service is unreachable, will try at the next trigger (${failed.name}: ${failed.detail})`);
    return 0;
  }
  if (failed) {
    deps.log(`failed: check ${failed.name}: ${failed.detail}`);
    return 1;
  }

  const outcome = await deps.read();
  const summary = outcome.reports ? summarize(outcome.reports) : 'no summary printed';
  if (outcome.code === 0 || outcome.code === 2) {
    deps.writeStamp(day);
    deps.log(`read: ${summary} (reader exit ${outcome.code})`);
    return 0;
  }
  deps.log(`failed: the reader exited ${outcome.code}: ${summary}`);
  return 1;
}
