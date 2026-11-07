import { mkdir } from 'node:fs/promises';
import { chromium, type BrowserContext } from 'playwright-core';
import type { Page } from './types.ts';

export interface BrowserOptions {
  /** A profile directory made for the reader. Never the owner's own browser profile. */
  profileDir: string;
  /** The installed browser to drive: "chrome" (default), "msedge", ... */
  channel: string;
  headless: boolean;
}

export interface Browser {
  load(url: string): Promise<Page>;
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
    close: () => context.close(),
  };
}
