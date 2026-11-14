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
    await cards.nth(i).locator('cmn-selectable-card button').click();
    await expect(cards.nth(i)).toContainText('On the list');
  }
  await cards.first().locator('cmn-selectable-card button').click({force: true});
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

// The partner forms the list and compares: the scale button on a tile opens the product at all four shops, a tap on a shop
// puts the line there (its chip), and "Leave it to the plan" puts it on with no shop. Prices are dated 5 Nov in the mock, so
// the clock sits on 6 Nov: one day old, fresh.

test('the compare sheet shows the four shops with price, day and flags, and a tap on a shop sets its chip', async ({
  page,
}) => {
  await page.clock.setFixedTime(new Date('2026-11-06T10:00:00Z'));
  await signedIn(page);
  const items: Record<string, unknown>[] = [];
  const posted: Posted[] = [];
  const deleted: string[] = [];
  await page.route('**/api/list', route => route.fulfill({json: {items}}));
  await page.route('**/api/list/items/*', async route => {
    const id = route.request().url().split('/').pop() ?? '';
    deleted.push(id);
    items.splice(
      items.findIndex(item => item['id'] === id),
      1
    );
    await route.fulfill({status: 204});
  });
  await page.route('**/api/list/items', async route => {
    const body = route.request().postDataJSON() as Posted;
    posted.push(body);
    const item = {
      id: `new-${posted.length}`,
      text: 'Whole milk 2L',
      checked: false,
      addedAt: '2026-11-06T10:00:00Z',
      addedBy: {id: 'u1', name: 'Anna'},
      checkedAt: null,
      checkedBy: null,
      productId: body.productId,
      store: body.store,
    };
    items.push(item);
    await route.fulfill({status: 201, json: item});
  });

  await page.goto('/shelf');
  const milk = page.locator('bk-shelf-card').filter({hasText: 'Whole milk 2L'});
  await milk.getByRole('button', {name: /^Compare Whole milk 2L/}).click();

  const sheet = page.locator('bk-compare-sheet');
  await expect(sheet).toBeVisible();
  const rows = sheet.locator('cmn-list-item-row');
  await expect(rows).toHaveCount(5);
  await expect(rows.nth(0)).toContainText('Leave it to the plan');
  await expect(rows.nth(1)).toContainText('Lidl');
  await expect(rows.nth(1)).toContainText('€0.99');
  await expect(rows.nth(1)).toContainText('Seen 5 Nov');
  await expect(rows.nth(1)).not.toContainText('Old price');
  await expect(rows.nth(2)).toContainText('Aldi');
  await expect(rows.nth(2)).toContainText('No page there');
  await expect(rows.nth(3)).toContainText('€1.89');
  await expect(rows.nth(4)).toContainText('No page there');

  await rows.nth(3).locator('button').click();
  await expect(sheet).toHaveCount(0);
  expect(posted).toEqual([{productId: 'p1', store: 'tesco'}]);
  await expect(milk).toContainText('On the list');

  // Changing its mind moves the line: Lidl is added and the Tesco line goes.
  await milk.getByRole('button', {name: /^Compare Whole milk 2L/}).click();
  await expect(page.locator('bk-compare-sheet cmn-list-item-row').nth(3)).toContainText(
    'On the list'
  );
  await page.locator('bk-compare-sheet cmn-list-item-row').nth(1).locator('button').click();
  await expect(page.locator('bk-compare-sheet')).toHaveCount(0);
  expect(posted).toEqual([
    {productId: 'p1', store: 'tesco'},
    {productId: 'p1', store: 'lidl'},
  ]);
  expect(deleted).toEqual(['new-1']);
});

test('the compare sheet flags a price older than three days and leaving it to the plan sets no shop', async ({
  page,
}) => {
  await page.clock.setFixedTime(new Date('2026-11-12T10:00:00Z'));
  await signedIn(page);
  const posted: Posted[] = [];
  await page.route('**/api/list', route => route.fulfill({json: {items: []}}));
  await page.route('**/api/list/items', async route => {
    const body = route.request().postDataJSON() as Posted;
    posted.push(body);
    await route.fulfill({
      status: 201,
      json: {
        id: 'new-1',
        text: 'Whole milk 2L',
        checked: false,
        addedAt: '2026-11-12T10:00:00Z',
        addedBy: {id: 'u1', name: 'Anna'},
        checkedAt: null,
        checkedBy: null,
        productId: body.productId,
        store: body.store,
      },
    });
  });

  await page.goto('/shelf');
  await page
    .locator('bk-shelf-card')
    .filter({hasText: 'Whole milk 2L'})
    .getByRole('button', {name: /^Compare Whole milk 2L/})
    .click();

  const rows = page.locator('bk-compare-sheet cmn-list-item-row');
  await expect(rows.nth(1)).toContainText('Old price');
  await expect(rows.nth(1)).toContainText('Seen 5 Nov');
  await expect(rows.nth(3)).toContainText('Old price');

  await rows.nth(0).locator('button').click();
  await expect(page.locator('bk-compare-sheet')).toHaveCount(0);
  expect(posted).toEqual([{productId: 'p1', store: null}]);
});
