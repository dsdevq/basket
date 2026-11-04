import {expect, type Page, test} from '@playwright/test';
import {checkScreen, formatScreen, SCREEN_VIEWPORTS} from '@lifekit-hq/tokens/screen';

// The screen-composition check (rules C0 to C10) over every routed page, at the phone and the desktop
// size. Any finding fails the build; there are no opt-outs (`ignore`).

const OWNER = {authenticated: true, user: {id: 'u1', displayName: 'Anna', role: 'owner'}};
const GUEST = {authenticated: false, user: null};
const ITEMS = [
  {text: 'Milk', checked: false},
  {text: 'Bread', checked: false},
  {text: 'Apples', checked: false},
  {text: 'Rice', checked: true},
].map((item, i) => ({
  id: `item-${i}`,
  ...item,
  addedAt: '2026-11-03T09:00:00Z',
  addedBy: {id: 'u1', name: 'Anna'},
  checkedAt: null,
  checkedBy: null,
}));
const MEMBERS = [
  {id: 'u1', displayName: 'Anna', role: 'owner', createdAt: '2026-11-01T09:00:00Z'},
  {id: 'u2', displayName: 'Ben', role: 'member', createdAt: '2026-11-02T09:00:00Z'},
];
const INVITES = [
  {id: 'i1', label: 'Chris', createdAt: '2026-11-03T09:00:00Z', expiresAt: '2026-11-04T09:00:00Z'},
];

interface Route {
  readonly path: string;
  /** Mocks the API as the person who reaches this route sees it. */
  readonly setup: (page: Page) => Promise<void>;
}

async function signedIn(page: Page): Promise<void> {
  await page.route('**/api/session', r => r.fulfill({json: OWNER}));
  await page.route('**/api/list', r => r.fulfill({json: {items: ITEMS}}));
  await page.route('**/api/members', r => r.fulfill({json: MEMBERS}));
  await page.route('**/api/invites', r => r.fulfill({json: INVITES}));
}

async function guest(page: Page): Promise<void> {
  await page.route('**/api/session', r => r.fulfill({json: GUEST}));
  await page.route('**/api/auth/invite', r => r.fulfill({json: {role: 'member', label: null}}));
}

const ROUTES: readonly Route[] = [
  {path: '/list', setup: signedIn},
  {path: '/people', setup: signedIn},
  {path: '/settings', setup: signedIn},
  {path: '/more', setup: signedIn},
  {path: '/sign-in', setup: guest},
  {path: '/join#invite-token', setup: guest},
  {
    path: '/unreachable',
    setup: page => page.route('**/api/session', r => r.abort('failed')),
  },
];

for (const [size, use] of Object.entries(SCREEN_VIEWPORTS)) {
  test.describe(`screen check at ${size}`, () => {
    test.use(use);

    for (const route of ROUTES) {
      test(`${route.path} is composed on its page template`, async ({page}) => {
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
