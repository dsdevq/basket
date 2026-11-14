import {expect, test} from '@playwright/test';

import {signedIn} from './support/screens';

// Appearance is an account preference (APP-SHELL-STANDARD section 2): the server copy wins once it
// has loaded, and a change in Settings is written back to `/api/me/preferences`. The design preset
// stays per device and is not part of it.
const html = 'html';

interface Appearance {
  theme: string;
  seed?: string;
  intensity?: number;
}

test.describe('appearance saved per account', () => {
  test.beforeEach(async ({page}) => {
    await signedIn(page);
  });

  test('the theme saved on the account is applied after sign-in, and is the mode Settings shows', async ({
    page,
  }) => {
    await page.route('**/api/me/preferences', r =>
      r.fulfill({json: {appearance: {theme: 'dark', seed: '#aa3300'}}})
    );

    await page.goto('/settings');

    await expect(page.locator(html)).toHaveAttribute('data-theme', 'dark');
    await expect(
      page.getByRole('radiogroup', {name: 'Theme'}).getByRole('radio', {name: 'Dark'})
    ).toBeChecked();
    expect(await page.evaluate(() => localStorage.getItem('cmn-theme'))).toBe('dark');
  });

  test('choosing a mode in Settings is written to the account', async ({page}) => {
    const saved: Appearance[] = [];
    await page.route('**/api/me/preferences', async route => {
      const request = route.request();
      if (request.method() === 'PATCH') {
        saved.push((request.postDataJSON() as {appearance: Appearance}).appearance);
        await route.fulfill({json: {appearance: saved.at(-1)}});
      } else {
        await route.fulfill({json: {appearance: {theme: 'system'}}});
      }
    });
    await page.goto('/settings');

    await page.getByRole('radiogroup', {name: 'Theme'}).getByRole('radio', {name: 'Dark'}).click();

    await expect.poll(() => saved.at(-1)?.theme).toBe('dark');
    await expect(page.locator(html)).toHaveAttribute('data-theme', 'dark');
  });
});
