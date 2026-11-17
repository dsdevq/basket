import {expect, type Page, test} from '@playwright/test';

const SESSION = {authenticated: true, user: {id: 'u1', displayName: 'Denys', role: 'owner'}};

// A keyboard or screen reader user has no pull to refresh: the account menu and the command palette
// each carry a Refresh that reloads the tab root on screen (what a pull does).
async function open(page: Page): Promise<() => number> {
  let listCalls = 0;
  await page.route('**/api/session', r => r.fulfill({json: SESSION}));
  await page.route('**/api/list', r => {
    listCalls++;
    return r.fulfill({json: {items: []}});
  });
  await page.route('**/api/members', r => r.fulfill({json: []}));
  await page.route('**/api/invites', r => r.fulfill({json: []}));
  await page.goto('/list');
  await expect(page.locator('html')).toHaveAttribute('data-bk-mounted', '');
  await expect.poll(() => listCalls).toBe(1);
  return () => listCalls;
}

test('the account menu Refresh reloads the page on screen', async ({page}) => {
  const listCalls = await open(page);

  await page.getByRole('button', {name: 'Account menu'}).click();
  await page.getByRole('menuitem', {name: 'Refresh'}).click();

  await expect.poll(listCalls).toBe(2);
  expect(new URL(page.url()).pathname).toBe('/list');
});

test('the command palette Refresh reloads the page on screen instead of navigating', async ({
  page,
}) => {
  const listCalls = await open(page);

  await page.keyboard.press('Control+k');
  // The palette focuses its search box after a short delay: wait for it, or the typing is lost.
  const search = page.getByPlaceholder('Search pages, actions…');
  await expect(search).toBeFocused();
  await search.fill('Refresh');
  await page.keyboard.press('Enter');

  await expect.poll(listCalls).toBe(2);
  expect(new URL(page.url()).pathname).toBe('/list');
});
