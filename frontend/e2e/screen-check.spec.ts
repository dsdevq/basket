import {expect, test} from '@playwright/test';
import {checkScreen, formatScreen, SCREEN_VIEWPORTS} from '@lifekit-hq/tokens/screen';

import {ROUTES} from './support/screens';

// The screen-composition check (rules C0 to C10) over every routed page, at the phone and the desktop
// size. Any finding fails the build; there are no opt-outs (`ignore`).

for (const [size, use] of Object.entries(SCREEN_VIEWPORTS)) {
  test.describe(`screen check at ${size}`, () => {
    test.use(use);

    for (const route of ROUTES) {
      test(`${route.name} is composed on its page template`, async ({page}) => {
        await route.setup(page);
        await page.goto(route.path);
        await page.locator('cmn-page').first().waitFor();

        const result = await checkScreen(page);

        expect(result.template).toBe(true);
        expect(result.findings, formatScreen(result)).toEqual([]);
      });
    }
  });
}
