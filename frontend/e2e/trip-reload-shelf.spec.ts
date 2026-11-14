import {expect, test} from '@playwright/test';

import {signedIn} from './support/screens';

// Same as last time, then the Shelf: a trip that held a Shelf pick comes back on the list as that product at that shop,
// so the Shelf tile says "On the list" and a tap adds nothing. The API is a small stand-in whose reload answers the way
// the real one does: the line carries its productId and store.

test.use({viewport: {width: 390, height: 844}, hasTouch: true, isMobile: true});

test('a reloaded Shelf line is marked on the list and tapping its tile adds no second line', async ({
  page,
}) => {
  await signedIn(page);
  const items: Record<string, unknown>[] = [];
  let posts = 0;
  await page.route('**/api/list', route => route.fulfill({json: {items}}));
  // No closed-trip habits: the list's "Usually bought" suggestions are rows too, and this spec counts the list's own lines.
  await page.route('**/api/trips/habits', route => route.fulfill({json: {products: []}}));
  await page.route('**/api/list/items', route => {
    posts++;
    return route.fulfill({status: 500, json: {error: 'unexpected'}});
  });
  await page.route('**/api/trips/*/reload', async route => {
    items.push({
      id: 'reloaded-0',
      text: 'Whole milk 2L',
      checked: false,
      addedAt: '2026-11-06T10:00:00Z',
      addedBy: {id: 'u1', name: 'Anna'},
      checkedAt: null,
      checkedBy: null,
      productId: 'p1',
      store: 'tesco',
    });
    await route.fulfill({json: {added: 1, alreadyOnList: 0}});
  });

  await page.goto('/history');
  await page.getByRole('button', {name: 'Same as last time'}).first().click();
  await expect(page).toHaveURL(/\/list$/);
  await expect(page.locator('cmn-list-item-row')).toHaveCount(1);

  await page
    .getByRole('navigation', {name: 'Primary'})
    .getByRole('button', {name: 'Shelf'})
    .click();
  await page.getByRole('radio', {name: 'Tesco'}).click();
  const milk = page.locator('bk-shelf-card').filter({hasText: 'Whole milk 2L'});
  await expect(milk).toContainText('On the list');

  await milk.locator('cmn-selectable-card button').click({force: true});
  expect(posts).toBe(0);
});
