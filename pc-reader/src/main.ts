import { openBrowser } from './browser.ts';
import { loadConfig } from './config.ts';
import { postBatch } from './ingest.ts';
import { run } from './run.ts';
import { loadWorkList } from './worklist.ts';

/**
 * Exit code: 0 every page read and accepted, 1 the run could not start or a post failed outright, 2 the run finished
 * but a store was blocked or some page was unreadable or refused.
 */
async function main(): Promise<number> {
  const config = loadConfig();
  const items = await loadWorkList(config.urlsFile);
  if (items.length === 0) {
    console.error(`${config.urlsFile} lists no pages; nothing to read.`);
    return 0;
  }

  const browser = await openBrowser({ profileDir: config.profileDir, channel: config.channel, headless: config.headless });
  try {
    const reports = await run(items, {
      load: (url) => browser.load(url),
      post: (readings) => postBatch(config.origin, config.token, readings),
      sleep: (ms) => new Promise((done) => setTimeout(done, ms)),
      now: () => Date.now(),
      log: (line) => console.error(line),
    });
    console.log(JSON.stringify({ finishedAt: new Date().toISOString(), stores: reports }, null, 2));

    if (reports.some((r) => r.posted.error)) {
      return 1;
    }
    const clean = reports.every((r) => !r.blocked && r.unreadable.length === 0 && r.posted.rejected.length === 0);
    return clean ? 0 : 2;
  } finally {
    await browser.close();
  }
}

main().then(
  (code) => {
    process.exitCode = code;
  },
  (error: unknown) => {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  },
);
