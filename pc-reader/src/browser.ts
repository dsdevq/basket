import { mkdir } from 'node:fs/promises';
import { chromium, type BrowserContext, type Response } from 'playwright-core';
import type { Inspected } from './probe/run.ts';
import type { Page } from './types.ts';

const MAX_JSON_BODIES = 12;
const MAX_JSON_BYTES = 750_000;

export interface BrowserOptions {
  /** A profile directory made for the reader. Never the owner's own browser profile. */
  profileDir: string;
  /** The installed browser to drive: "chrome" (default), "msedge", ... */
  channel: string;
  headless: boolean;
}

export interface Browser {
  load(url: string): Promise<Page>;
  /** One page load plus the network calls the page made itself (for `npm run probe`). Reads only. */
  inspect(url: string): Promise<Inspected>;
  close(): Promise<void>;
}

/**
 * An ordinary installed Chrome with its own clean profile, on this PC's own connection. Nothing is done to look like
 * anything else: no stealth plugin, no user-agent or fingerprint changes, no proxy, no extra headers. It is driven by
 * Playwright, and a site is free to notice that and refuse; the run then stops that site (see run.ts).
 */
export async function openBrowser(options: BrowserOptions): Promise<Browser> {
  await mkdir(options.profileDir, { recursive: true });
  const context: BrowserContext = await chromium.launchPersistentContext(options.profileDir, {
    channel: options.channel,
    headless: options.headless,
  });
  const tab = context.pages()[0] ?? (await context.newPage());
  return {
    async load(url) {
      const response = await tab.goto(url, { waitUntil: 'domcontentloaded', timeout: 45_000 });
      // Let the page's own scripts settle briefly; a page that never goes quiet is read as it stands.
      await tab.waitForLoadState('load', { timeout: 15_000 }).catch(() => undefined);
      return { status: response?.status() ?? 0, html: await tab.content() };
    },
    async inspect(url) {
      const seen: Inspected['requests'] = [];
      const bodies: Promise<{ url: string; value: unknown } | undefined>[] = [];
      const onResponse = (response: Response): void => {
        const contentType = response.headers()['content-type'] ?? '';
        const resourceType = response.request().resourceType();
        seen.push({ url: response.url(), method: response.request().method(), resourceType, status: response.status(), contentType });
        if ((resourceType === 'xhr' || resourceType === 'fetch') && /json/i.test(contentType) && bodies.length < MAX_JSON_BODIES) {
          bodies.push(
            response
              .text()
              .then((text) => (text.length <= MAX_JSON_BYTES ? { url: response.url(), value: JSON.parse(text) as unknown } : undefined))
              .catch(() => undefined),
          );
        }
      };
      tab.on('response', onResponse);
      try {
        const response = await tab.goto(url, { waitUntil: 'domcontentloaded', timeout: 45_000 });
        await tab.waitForLoadState('load', { timeout: 15_000 }).catch(() => undefined);
        const contentType = response?.headers()['content-type'] ?? '';
        const body = /html/i.test(contentType) ? await tab.content() : ((await response?.text().catch(() => '')) ?? '');
        const json = (await Promise.all(bodies)).filter((b) => b !== undefined);
        return { status: response?.status() ?? 0, contentType, body, requests: seen, json };
      } finally {
        tab.off('response', onResponse);
      }
    },
    close: () => context.close(),
  };
}
