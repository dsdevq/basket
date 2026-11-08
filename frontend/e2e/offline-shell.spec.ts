import {cpSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync} from 'node:fs';
import {createServer, type Server} from 'node:http';
import type {AddressInfo} from 'node:net';
import {tmpdir} from 'node:os';
import {extname, join, normalize} from 'node:path';

import {expect, type Page, test} from '@playwright/test';

// The service worker is the one thing every other spec blocks (`serviceWorkers: 'block'`, so page.route can answer the API). Here
// it runs for real, against a copy of the production build served by a server of its own that answers `/api`, because a
// page controlled by the worker no longer goes through page.route.
test.use({serviceWorkers: 'allow'});

const DIST = join(__dirname, '..', 'dist', 'basket', 'browser');
const SETTINGS = join(__dirname, '..', '..', 'backend', 'src', 'Basket.Api', 'appsettings.json');
const CSP = JSON.parse(readFileSync(SETTINGS, 'utf8')).Security.ContentSecurityPolicy as string;
const TYPES: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.webmanifest': 'application/manifest+json',
  '.png': 'image/png',
  '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon',
  '.woff2': 'font/woff2',
};
const OWNER = {authenticated: true, user: {id: 'u1', displayName: 'Anna', role: 'owner'}};
const GUEST = {authenticated: false, user: null};
const ITEM = {
  id: 'item-1',
  text: 'Milk',
  checked: false,
  addedAt: '2026-11-03T09:00:00Z',
  addedBy: {id: 'u1', name: 'Anna'},
  checkedAt: null,
  checkedBy: null,
};

let root: string;
let server: Server;
let origin: string;
let signedIn = false;
const apiHits: string[] = [];

function serve(pathname: string): {file: string; status: number} {
  const file = join(root, normalize(decodeURIComponent(pathname)));
  try {
    if (file.startsWith(root) && statSync(file).isFile()) {
      return {file, status: 200};
    }
  } catch {
    // Not a file: an app route, answered with the shell.
  }
  return {file: join(root, 'index.html'), status: 200};
}

test.beforeAll(async () => {
  root = mkdtempSync(join(tmpdir(), 'basket-sw-'));
  cpSync(DIST, root, {recursive: true});
  server = createServer((req, res) => {
    const {pathname} = new URL(req.url ?? '/', 'http://x');
    if (pathname.startsWith('/api/')) {
      apiHits.push(pathname);
      res.writeHead(200, {'content-type': 'application/json', 'cache-control': 'no-store'});
      res.end(
        JSON.stringify(
          pathname === '/api/session' ? (signedIn ? OWNER : GUEST) : {items: [ITEM], members: []}
        )
      );
      return;
    }
    const {file, status} = serve(pathname);
    res.writeHead(status, {
      'content-type': TYPES[extname(file)] ?? 'application/octet-stream',
      'content-security-policy': CSP,
      // As the API serves them: hashed bundles are immutable, the shell and the worker are re-checked.
      'cache-control': /-[A-Za-z0-9]{8}\.(js|css|woff2)$/.test(file)
        ? 'public, max-age=31536000, immutable'
        : 'no-cache',
    });
    res.end(readFileSync(file));
  });
  await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve));
  origin = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
});

test.afterAll(async () => {
  await new Promise(resolve => server.close(resolve));
  rmSync(root, {recursive: true, force: true});
});

test.beforeEach(() => {
  signedIn = false;
  apiHits.length = 0;
});

/**
 * Resolves once the worker controls the page and has downloaded the shell. The worker only starts on the first
 * request it sees, so the page is loaded a second time (as a second visit is) before the cache is waited for.
 */
async function shellCached(page: Page): Promise<void> {
  await page.waitForFunction(
    async () => (await navigator.serviceWorker.getRegistration())?.active?.state === 'activated',
    undefined,
    {timeout: 45_000}
  );
  await page.reload();
  await page.waitForFunction(
    async () => {
      for (const name of await caches.keys()) {
        if (
          name.endsWith(':assets:app:cache') &&
          (await (await caches.open(name)).match('/index.html'))
        ) {
          return true;
        }
      }
      return false;
    },
    undefined,
    {timeout: 30_000}
  );
}

test('serves the app shell with no network, and never keeps an API answer', async ({
  page,
  context,
}) => {
  await page.goto(`${origin}/sign-in`);
  await expect(page.locator('cmn-page')).toBeVisible();
  await shellCached(page);

  await context.setOffline(true);
  await page.reload();

  // The shell came from the worker's cache; the session call had nowhere to go, so the app says so
  // instead of showing a stale session.
  await expect(page.getByRole('heading', {name: 'Cannot reach basket'})).toBeVisible();
  await expect(page.locator('lk-offline-banner')).toHaveCount(0);

  const cached = await page.evaluate(async () => {
    const urls: string[] = [];
    for (const name of await caches.keys()) {
      for (const request of await (await caches.open(name)).keys()) {
        urls.push(new URL(request.url).pathname);
      }
    }
    return urls;
  });
  expect(cached).toContain('/index.html');
  expect(cached.filter(url => url.startsWith('/api'))).toEqual([]);
});

test('offers the new version once it is downloaded, and runs it on Reload', async ({page}) => {
  await page.goto(`${origin}/sign-in`);
  await shellCached(page);

  // A release is a new ngsw.json; changing its timestamp changes the manifest hash the worker compares.
  const manifestFile = join(root, 'ngsw.json');
  const manifest = JSON.parse(readFileSync(manifestFile, 'utf8')) as {timestamp: number};
  writeFileSync(manifestFile, JSON.stringify({...manifest, timestamp: manifest.timestamp + 1}));

  signedIn = true;
  await page.goto(`${origin}/list`);
  await expect(page.locator('cmn-page').first()).toBeVisible();
  // The worker also checks on navigation, after an idle delay; coming back to the app is the quicker trigger.
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
  const prompt = page.locator('lk-update-prompt');
  await expect(prompt.getByRole('button', {name: /reload/i})).toBeVisible({timeout: 30_000});

  await Promise.all([
    page.waitForNavigation(),
    prompt.getByRole('button', {name: /reload/i}).click(),
  ]);
  await expect(page.locator('cmn-page').first()).toBeVisible();
  await expect(prompt.getByRole('button', {name: /reload/i})).toHaveCount(0);
});
