import {expect, test} from '@playwright/test';

import {signedIn} from './support/screens';

// The phone UX audit's `basket-boot` probe: a cold start with the session call held for 900 ms. The
// shell draws at once, so the page that arrives moves nothing (Apple HIG, Loading and Launching).
const SESSION_DELAY_MS = 900;
const MAX_SHIFT = 0.1;
const PAGES = [
  '/list',
  '/history',
  '/shelf',
  '/plan',
  '/plan/by-shop',
  '/prices',
  '/promotions',
  '/cards',
  '/cards/tesco',
  '/more',
  '/people',
  '/settings',
];

for (const [name, viewport] of [
  ['phone', {width: 390, height: 844}],
  ['desktop', {width: 1280, height: 800}],
] as const) {
  test.describe(name, () => {
    test.use({viewport});

    for (const path of PAGES) {
      test(`${path}: a slow session call shifts the layout by less than ${MAX_SHIFT}`, async ({
        page,
      }) => {
        await signedIn(page);
        await page.route('**/api/session', async route => {
          await new Promise(resolve => setTimeout(resolve, SESSION_DELAY_MS));
          await route.fulfill({
            json: {authenticated: true, user: {id: 'u1', displayName: 'Denys', role: 'owner'}},
          });
        });
        await page.addInitScript(() => {
          const w = window as unknown as {__shift: number};
          w.__shift = 0;
          new PerformanceObserver(list => {
            for (const entry of list.getEntries() as unknown as {
              value: number;
              hadRecentInput: boolean;
            }[]) {
              if (!entry.hadRecentInput) {
                w.__shift += entry.value;
              }
            }
          }).observe({type: 'layout-shift', buffered: true});
        });

        await page.goto(path);
        // The shell is up before the session answers, holding the page's place.
        await expect(page.getByRole('status', {name: 'Loading'})).toBeVisible();
        await expect(page.locator('main')).toBeVisible();
        await page.waitForTimeout(SESSION_DELAY_MS + 600);
        await expect(page.getByRole('status', {name: 'Loading'})).toHaveCount(0);

        const shift = await page.evaluate(() => (window as unknown as {__shift: number}).__shift);
        expect(shift).toBeLessThan(MAX_SHIFT);
      });
    }
  });
}
