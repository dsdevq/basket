import {expect, test} from '@playwright/test';

import {signedIn} from './support/screens';

// The owner on a phone: Plan is one of the four tabs, Prices moved behind More, and the Plan tab answers whether one
// shop covers the list and which lines stand in the way.

test.use({viewport: {width: 390, height: 844}, hasTouch: true, isMobile: true});

test('the Plan tab is in the tab bar, Prices is behind More, and the plan says what blocks one shop', async ({
  page,
}) => {
  await signedIn(page);
  const tabs = page.getByRole('navigation', {name: 'Primary'});

  await page.goto('/list');
  for (const name of ['List', 'Shelf', 'Plan', 'Cards', 'More']) {
    await expect(tabs.getByRole('button', {name})).toBeVisible();
  }
  await expect(tabs.getByRole('button', {name: 'Prices'})).toHaveCount(0);

  await tabs.getByRole('button', {name: 'Plan'}).click();
  await expect(page).toHaveURL(/\/plan$/);
  const answer = page.getByTestId('coverage');
  await expect(answer).toContainText('No one shop covers it');
  await expect(answer).toContainText('Closest: Lidl');
  await expect(page.getByText('Not priced at Lidl')).toBeVisible();
  await expect(page.getByText('Seen 6 Nov').first()).toBeVisible();

  await tabs.getByRole('button', {name: 'More'}).click();
  await expect(page.getByRole('link', {name: 'Prices'})).toBeVisible();
});
