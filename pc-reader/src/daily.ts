import { spawn } from 'node:child_process';
import { appendFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { loadConfig, READER_DIR } from './config.ts';
import { checkIdentity } from './identity-check.ts';
import type { StoreReport } from './run.ts';
import { runDaily, type ReaderOutcome } from './schedule.ts';

/** `npm run daily`: the scheduled entry point (see "Running it every day" in the README). */
const stateDir = join(process.env['XDG_STATE_HOME'] ?? join(homedir(), '.local', 'state'), 'basket-pc-reader');
const stampFile = join(stateDir, 'last-read');
const logFile = join(stateDir, 'daily.log');

/** `main.ts` as a child process: its progress goes straight to our stderr, its JSON summary is captured for the log line. */
function readAll(): Promise<ReaderOutcome> {
  return new Promise((resolve, reject) => {
    // A scheduled run has nobody at the screen: windowless unless the unit says otherwise.
    const env = { ...process.env, BASKET_READER_HEADLESS: process.env['BASKET_READER_HEADLESS'] ?? '1' };
    const child = spawn(process.execPath, [join(READER_DIR, 'src', 'main.ts')], { env, stdio: ['ignore', 'pipe', 'inherit'] });
    let out = '';
    child.stdout.setEncoding('utf8').on('data', (chunk: string) => {
      out += chunk;
    });
    child.on('error', reject);
    child.on('close', (code) => {
      let reports: StoreReport[] | undefined;
      try {
        reports = (JSON.parse(out) as { stores: StoreReport[] }).stores;
      } catch {
        // The reader died before it printed its summary; the exit code and its stderr say why.
      }
      resolve({ code: code ?? 1, reports });
    });
  });
}

mkdirSync(stateDir, { recursive: true });
runDaily({
  now: () => new Date(),
  readStamp: () => {
    try {
      return readFileSync(stampFile, 'utf8').trim();
    } catch {
      return undefined;
    }
  },
  writeStamp: (day) => writeFileSync(stampFile, `${day}\n`),
  log: (line) => {
    const stamped = `${new Date().toISOString()} ${line}`;
    console.log(stamped);
    appendFileSync(logFile, `${stamped}\n`);
  },
  loadConfig,
  check: checkIdentity,
  read: readAll,
}).then(
  (code) => {
    process.exitCode = code;
  },
  (error: unknown) => {
    const line = `${new Date().toISOString()} failed: ${error instanceof Error ? error.message : String(error)}`;
    console.error(line);
    appendFileSync(logFile, `${line}\n`);
    process.exitCode = 1;
  },
);
