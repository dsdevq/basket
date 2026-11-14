import { openBrowser } from './browser.ts';
import { loadProbeSettings } from './config.ts';
import { PROBE_STORES, probe, type ProbeStore } from './probe/run.ts';
import { writeReport } from './probe/report.ts';
import { resolve } from 'node:path';

/** `--store tesco|dunnes` (or `--store=...`) and `--out <file>`. Anything else is a mistake worth stopping on. */
export function parseProbeArgs(args: readonly string[]): { store: ProbeStore; out?: string } {
  let store: string | undefined;
  let out: string | undefined;
  for (let i = 0; i < args.length; i++) {
    const arg = args[i] ?? '';
    const [flag, inline] = arg.split(/=(.*)/s);
    if (flag === '--store' || flag === '--out') {
      const value = inline ?? args[++i];
      if (flag === '--store') {
        store = value;
      } else {
        out = value;
      }
    } else {
      throw new Error(`Unknown argument "${arg}" (usage: npm run probe -- --store ${PROBE_STORES.join('|')} [--out file.json]).`);
    }
  }
  if (!store || !(PROBE_STORES as readonly string[]).includes(store)) {
    throw new Error(`Name the store: npm run probe -- --store ${PROBE_STORES.join('|')} [--out file.json].`);
  }
  return { store: store as ProbeStore, ...(out ? { out: resolve(out) } : {}) };
}

/**
 * `npm run probe -- --store tesco|dunnes`: the catalog probe. Opens the reader's own browser profile (as `npm run
 * profile` leaves it, the same one the normal reader uses) and reads five addresses of one store, 5 s apart, stopping
 * at the first refusal. Needs no token and posts nothing anywhere. Exit 0 when it finished, 2 when the store refused.
 */
async function main(): Promise<number> {
  const { store, out } = parseProbeArgs(process.argv.slice(2));
  const { profileDir, channel, headless } = loadProbeSettings();
  const browser = await openBrowser({ profileDir, channel, headless });
  try {
    const report = await probe(store, {
      inspect: (url) => browser.inspect(url),
      sleep: (ms) => new Promise((done) => setTimeout(done, ms)),
      now: () => Date.now(),
      log: (line) => console.error(line),
    });
    await writeReport({ ...report, headless }, out);
    return report.blocked ? 2 : 0;
  } finally {
    await browser.close();
  }
}

if (import.meta.main) {
  main().then(
    (code) => {
      process.exitCode = code;
    },
    (error: unknown) => {
      console.error(error instanceof Error ? error.message : String(error));
      process.exitCode = 1;
    },
  );
}
