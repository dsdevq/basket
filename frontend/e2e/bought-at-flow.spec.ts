import {expect, test} from '@playwright/test';

import {signedIn} from './support/screens';

// The owner's flow on a phone: a ticked item says where it was bought (the shop recorded at the tick), and the item sheet
// changes it when the plan's shop was not where it went. The list is a small stateful stand-in for the API.

test.use({viewport: {width: 390, height: 844}, hasTouch: true, isMobile: true});

test('a ticked item shows the shop it was bought at, and the item sheet changes it', async ({
  page,
}) => {
  await signedIn(page);
  let rice: Record<string, unknown> = {
    id: 'rice',
    text: 'Rice',
    checked: true,
    addedAt: '2026-11-06T10:00:00Z',
    addedBy: {id: 'u1', name: 'Anna'},
    checkedAt: '2026-11-06T10:05:00Z',
    checkedBy: {id: 'u1', name: 'Anna'},
    productId: null,
    store: null,
    imageUrl: null,
    boughtStore: 'lidl',
  };
  const patched: unknown[] = [];
  await page.route('**/api/list', route => route.fulfill({json: {items: [rice]}}));
  await page.route('**/api/list/items/rice/bought-at', async route => {
    const body = route.request().postDataJSON() as {store: string};
    patched.push(body);
    rice = {...rice, boughtStore: body.store};
    await route.fulfill({json: rice});
  });

  await page.goto('/list');
  const row = page.locator('cmn-list-item-row').filter({hasText: 'Rice'});
  await expect(row).toContainText('Lidl');

  await row.getByRole('button', {name: 'More for Rice'}).click();
  const rows = page.locator('bk-item-actions-sheet cmn-list-item-row');
  await expect(rows).toHaveCount(5);
  await expect(rows.nth(0)).toContainText('Bought here');
  await rows.nth(1).locator('button').click();

  await expect(page.locator('bk-item-actions-sheet')).toHaveCount(0);
  expect(patched).toEqual([{store: 'aldi'}]);
  await expect(row).toContainText('Aldi');
  await expect(row).not.toContainText('Lidl');
});
