import { openBrowser } from './browser.ts';
import { fetchCatalogWorkList, resolveWorkList } from './catalog.ts';
import { loadConfig } from './config.ts';
import { bearerSource } from './fleet.ts';
import { postBatch } from './ingest.ts';
import { run } from './run.ts';
import { loadWorkList, parseStoreArg } from './worklist.ts';

/**
 * Exit code: 0 every page read and accepted, 1 the run could not start or a post failed outright, 2 the run finished
 * but a store was blocked or some page was unreadable or refused.
 */
async function main(): Promise<number> {
  const config = loadConfig();
  const only = parseStoreArg(process.argv.slice(2));
  const bearer = bearerSource(config.auth);
  const listed = await resolveWorkList({
    catalog: config.auth.kind === 'fleet' ? () => fetchCatalogWorkList(config.origin, bearer) : undefined,
    file: () => loadWorkList(config.urlsFile),
    log: (line) => console.error(line),
  });
  const items = only ? listed.filter((item) => item.store === only) : listed;
  if (items.length === 0) {
    console.error(`No ${only ? `${only} ` : ''}pages to read (the page list is empty).`);
    return only ? 1 : 0;
  }

  const browser = await openBrowser({ profileDir: config.profileDir, channel: config.channel, headless: config.headless });
  try {
    const reports = await run(items, {
      load: (url) => browser.load(url),
      post: async (readings) => postBatch(config.origin, await bearer(), readings),
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
