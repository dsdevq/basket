import {expect, type Page, test} from '@playwright/test';

// iPhone 17 (CSS px) and its Dynamic Island / home-indicator insets: the installed-PWA shape, drawn
// under the status bar and the home indicator. Chromium can simulate the insets through CDP.
const PHONE = {width: 402, height: 874};
const SMALL_PHONE = {width: 375, height: 667};
const INSET_TOP = 62;
const INSET_BOTTOM = 34;
const ITEM_COUNT = 40;
const SCROLL_PROBE = 500;
// The shell leaves 8px of breathing room between the content and the tab bar.
const BREATHING = 8;

const SESSION = {authenticated: true, user: {id: 'u1', displayName: 'Denys', role: 'owner'}};
const ITEMS = Array.from({length: ITEM_COUNT}, (_, i) => ({
  id: `item-${i}`,
  text: `Item ${i}`,
  checked: false,
  addedAt: '2026-11-03T09:00:00Z',
  addedBy: {id: 'u1', name: 'Denys'},
  checkedAt: null,
  checkedBy: null,
}));

async function open(page: Page, route: string, insets: boolean): Promise<void> {
  await page.route('**/api/session', r => r.fulfill({json: SESSION}));
  await page.route('**/api/list', r => r.fulfill({json: {items: ITEMS}}));
  await page.route('**/api/members', r => r.fulfill({json: []}));
  await page.route('**/api/invites', r => r.fulfill({json: []}));
  await page.goto(route);
  if (insets) {
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Emulation.setSafeAreaInsetsOverride', {
      insets: {top: INSET_TOP, bottom: INSET_BOTTOM, left: 0, right: 0},
    });
    await page.reload();
  }
  // The shell is drawn before the session answers; the routed shell replaces it once the first
  // navigation ends, so measure only after that swap.
  await expect(page.locator('html')).toHaveAttribute('data-bk-mounted', '');
  await expect(page.getByRole('status', {name: 'Loading'})).toHaveCount(0);
  await expect(page.locator('cmn-bottom-tab-bar nav')).toBeVisible();
}

for (const [name, viewport, insets] of [
  ['phone with safe-area insets', PHONE, true],
  ['small phone', SMALL_PHONE, false],
] as const) {
  test.describe(name, () => {
    test.use({viewport, hasTouch: true, isMobile: true});

    for (const route of ['/list', '/more']) {
      test(`${route}: the tab bar is pinned to the bottom of the screen`, async ({page}) => {
        await open(page, route, insets);

        const nav = (await page.locator('cmn-bottom-tab-bar nav').boundingBox())!;
        expect(nav.x).toBe(0);
        expect(nav.width).toBe(viewport.width);
        expect(nav.y + nav.height).toBe(viewport.height);
        expect(nav.y).toBeGreaterThan(viewport.height / 2);
        if (insets) {
          // The bar reaches the screen's edge and pads its labels above the home indicator.
          expect(nav.height).toBeGreaterThan(INSET_BOTTOM);
        }

        // The page itself never scrolls: a scroll attempt leaves the window at 0.
        const scroll = await page.evaluate(probe => {
          window.scrollTo(0, probe);
          const root = document.documentElement;
          return {overflow: root.scrollHeight - root.clientHeight, scrollY: window.scrollY};
        }, SCROLL_PROBE);
        expect(scroll).toEqual({overflow: 0, scrollY: 0});
      });
    }

    for (const route of ['/list', '/prices']) {
      test(`${route}: the add bar rests in the thumb zone, just above the tab bar`, async ({
        page,
      }) => {
        await page.route('**/api/products', r => r.fulfill({json: {products: [], stores: []}}));
        await open(page, route, insets);

        const bar = (await page.locator('bk-composer-bar').boundingBox())!;
        const nav = (await page.locator('cmn-bottom-tab-bar nav').boundingBox())!;
        expect(bar.y).toBeGreaterThan(viewport.height / 2);
        expect(nav.y - (bar.y + bar.height)).toBeGreaterThanOrEqual(0);
        expect(nav.y - (bar.y + bar.height)).toBeLessThanOrEqual(BREATHING);
      });
    }

    test('/list: the add bar stays above the tab bar while a long list scrolls', async ({page}) => {
      await open(page, '/list', insets);
      await expect(page.locator('cmn-list-item-row')).toHaveCount(ITEM_COUNT);
      await page.evaluate(() => (document.querySelector('main') as HTMLElement).scrollTo(0, 0));

      const gap = await page.evaluate(() => {
        const bar = document.querySelector('bk-composer-bar') as HTMLElement;
        const nav = document.querySelector('cmn-bottom-tab-bar nav') as HTMLElement;
        return nav.getBoundingClientRect().top - bar.getBoundingClientRect().bottom;
      });
      expect(gap).toBeGreaterThanOrEqual(0);
      expect(gap).toBeLessThanOrEqual(BREATHING);
    });

    test('/list: the last item scrolls clear of the tab bar', async ({page}) => {
      await open(page, '/list', insets);
      await expect(page.locator('cmn-list-item-row')).toHaveCount(ITEM_COUNT);

      const gap = await page.evaluate(() => {
        const main = document.querySelector('main') as HTMLElement;
        main.scrollTop = main.scrollHeight;
        const bar = document.querySelector('cmn-bottom-tab-bar nav') as HTMLElement;
        const rows = document.querySelectorAll('cmn-list-item-row');
        return (
          bar.getBoundingClientRect().top - rows[rows.length - 1].getBoundingClientRect().bottom
        );
      });
      expect(gap).toBeGreaterThanOrEqual(0);
    });
  });
}
