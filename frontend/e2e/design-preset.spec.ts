import {expect, test} from '@playwright/test';

import {signedIn} from './support/screens';

// The design selection in Settings > Appearance (the library's own `PresetService` and Design
// control, offered through `provideDesignPresets`). Lifekit is the default; the choice is kept on
// the device under `cmn-preset`.
const html = 'html';

test.describe('design preset', () => {
  test.beforeEach(async ({page}) => {
    await signedIn(page);
  });

  test('Lifekit is the default and the Design control shows it chosen', async ({page}) => {
    await page.goto('/settings');
    await expect(page.locator(html)).toHaveAttribute('data-preset', 'lifekit');
    const design = page.getByRole('radiogroup', {name: 'Design'});
    await expect(design.getByRole('radio', {name: 'Lifekit'})).toBeChecked();
    await expect(design.getByRole('radio', {name: 'Carbon'})).not.toBeChecked();
  });

  test('switching to Carbon restyles the app at once, and back to Lifekit is kept across a reload', async ({
    page,
  }) => {
    await page.goto('/settings');
    const design = page.getByRole('radiogroup', {name: 'Design'});

    await design.getByRole('radio', {name: 'Carbon'}).click();
    await expect(page.locator(html)).not.toHaveAttribute('data-preset', /.*/);
    await expect(design.getByRole('radio', {name: 'Carbon'})).toBeChecked();

    await design.getByRole('radio', {name: 'Lifekit'}).click();
    await expect(page.locator(html)).toHaveAttribute('data-preset', 'lifekit');
    expect(await page.evaluate(() => localStorage.getItem('cmn-preset'))).toBe('lifekit');

    await page.reload();
    await expect(page.locator(html)).toHaveAttribute('data-preset', 'lifekit');
    await expect(
      page.getByRole('radiogroup', {name: 'Design'}).getByRole('radio', {name: 'Lifekit'})
    ).toBeChecked();
  });
});
