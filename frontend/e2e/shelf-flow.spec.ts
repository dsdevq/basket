import {expect, test} from '@playwright/test';

import {PRODUCTS, signedIn} from './support/screens';

// The owner's flow on a phone: open the Shelf, pick a shop, tap the products, find them on the List with the shop on
// each. The list is a small stateful stand-in for the API, so what the page posts is what comes back.

test.use({viewport: {width: 390, height: 844}, hasTouch: true, isMobile: true});

interface Posted {
  readonly productId?: string;
  readonly store?: string | null;
}

test('pick Tesco, tap three products, and they are on the list with the Tesco chip', async ({
  page,
}) => {
  await signedIn(page);
  const items: Record<string, unknown>[] = [];
  const posted: Posted[] = [];
  await page.route('**/api/list', route => route.fulfill({json: {items}}));
  await page.route('**/api/list/items', async route => {
    const body = route.request().postDataJSON() as Posted;
    posted.push(body);
    const product = PRODUCTS.products.find(candidate => candidate.id === body.productId);
    const open = items.find(i => i['productId'] === body.productId && i['store'] === body.store);
    const item = open ?? {
      id: `new-${items.length}`,
      text: product?.name,
      checked: false,
      addedAt: '2026-11-06T10:00:00Z',
      addedBy: {id: 'u1', name: 'Anna'},
      checkedAt: null,
      checkedBy: null,
      productId: body.productId,
      store: body.store,
    };
    if (!open) {
      items.push(item);
    }
    await route.fulfill({status: open ? 200 : 201, json: item});
  });

  const tabs = page.getByRole('navigation', {name: 'Primary'});
  await page.goto('/list');
  await tabs.getByRole('button', {name: 'Shelf'}).click();
  await expect(page).toHaveURL(/\/shelf$/);
  await page.getByRole('radio', {name: 'Tesco'}).click();

  const cards = page.locator('bk-shelf-card');
  await expect(cards).toHaveCount(3);
  await expect(cards.filter({hasText: 'Free-range eggs'})).toHaveCount(0);
  await expect(cards.first()).toContainText('€1.89');
  await expect(cards.first()).toContainText('€2.35');
  await expect(cards.first()).toContainText('Clubcard Price');
  await expect(cards.nth(2)).toContainText('Out of stock');

  for (let i = 0; i < 3; i++) {
    await cards.nth(i).locator('button').click();
    await expect(cards.nth(i)).toContainText('On the list');
  }
  await cards.first().locator('button').click({force: true});
  expect(posted).toEqual([
    {productId: 'p1', store: 'tesco'},
    {productId: 'p2', store: 'tesco'},
    {productId: 'p3', store: 'tesco'},
  ]);

  await tabs.getByRole('button', {name: 'List', exact: true}).click();
  const rows = page.locator('cmn-list-item-row');
  await expect(rows).toHaveCount(3);
  for (const row of await rows.all()) {
    await expect(row).toContainText('Tesco');
  }
  await expect(page.getByText('Whole milk 2L')).toBeVisible();
});
