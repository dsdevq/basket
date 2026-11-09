import type {Page} from '@playwright/test';

// The app as the screen specs see it: one API mock per signed-in or guest visitor, and the list of
// routed pages. The screen check, the baselines and the route scan all read it, so a new page is
// added here once and every gate covers it.

/** Pinned so a date on screen ("added today") cannot move a baseline. */
export const FROZEN_NOW = new Date('2026-11-06T10:00:00Z');

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
const TRIPS = {
  trips: [
    {
      id: 'trip-2',
      closedAt: '2026-11-05T12:00:00Z',
      items: [
        {text: 'Whole milk 2L', by: 'Anna'},
        {text: 'Sourdough bread', by: 'Anna'},
        {text: 'Bananas', by: 'Ben'},
      ],
    },
    {id: 'trip-1', closedAt: '2026-10-29T12:00:00Z', items: [{text: 'Rice', by: 'Ben'}]},
  ].map(trip => ({
    ...trip,
    items: trip.items.map((item, i) => ({
      id: `${trip.id}-item-${i}`,
      text: item.text,
      checkedAt: trip.closedAt,
      checkedBy: {id: item.by === 'Anna' ? 'u1' : 'u2', name: item.by},
    })),
  })),
  hasMore: false,
};
const PRODUCTS = {
  products: [
    {
      id: 'p1',
      name: 'Whole milk 2L',
      stores: [
        {
          store: 'lidl',
          name: 'Lidl',
          readBy: 'nightly',
          listing: {
            url: 'https://www.lidl.ie/p/milk/p11000',
            title: 'Milk',
            checkedAt: '2026-11-05T03:00:00Z',
            error: null,
          },
          price: {
            observedOn: '2026-11-05',
            state: 'current',
            price: 1.29,
            wasPrice: 1.49,
            loyaltyPrice: 0.99,
            loyaltyScheme: 'Lidl Plus',
            promoText: 'Lidl Plus deal',
            validFrom: null,
            validUntil: '2026-11-11',
            best: 0.99,
          },
          lastPaid: {state: 'not_connected', price: null, paidOn: null},
        },
        {store: 'aldi', name: 'Aldi', readBy: 'pc', listing: null, price: null, lastPaid: null},
        {store: 'tesco', name: 'Tesco', readBy: 'pc', listing: null, price: null, lastPaid: null},
      ],
    },
  ],
  stores: [
    {
      store: 'tesco',
      name: 'Tesco',
      lastRun: {
        finishedAt: '2026-11-05T03:00:00Z',
        outcome: 'blocked',
        listings: 1,
        read: 0,
        message: 'www.tesco.ie answered HTTP 403.',
      },
    },
  ],
};
const PROMOTIONS = {
  asOf: '2026-11-06',
  staleAfterDays: 3,
  products: [
    {
      id: 'p2',
      name: 'Chicken fillets',
      unit: 'kg',
      ranked: 3,
      offers: [
        {
          store: 'tesco',
          storeName: 'Tesco',
          kind: 'loyalty',
          isPromo: true,
          deal: 'Clubcard price',
          promoText: null,
          card: 'Clubcard',
          buyQuantity: 1,
          payTotal: 3.2,
          eachPrice: 3.2,
          shelfPrice: 4,
          referencePrice: 4,
          savingPercent: 20,
          packSize: '650 g',
          unit: 'kg',
          unitPrice: 4.92,
          referenceUnitPrice: 6.15,
          observedOn: '2026-11-06',
          validUntil: null,
          rank: 1,
          flags: [],
        },
        {
          store: 'lidl',
          storeName: 'Lidl',
          kind: 'multi_buy',
          isPromo: true,
          deal: '2 for €5',
          promoText: '2 for €5',
          card: null,
          buyQuantity: 2,
          payTotal: 5,
          eachPrice: 2.5,
          shelfPrice: 3,
          referencePrice: 3,
          savingPercent: 17,
          packSize: '500 g',
          unit: 'kg',
          unitPrice: 5,
          referenceUnitPrice: 6,
          observedOn: '2026-11-06',
          validUntil: '2026-11-11',
          rank: 2,
          flags: [],
        },
        {
          store: 'aldi',
          storeName: 'Aldi',
          kind: 'percentage',
          isPromo: true,
          deal: '25% off',
          promoText: null,
          card: null,
          buyQuantity: 1,
          payTotal: 6,
          eachPrice: 6,
          shelfPrice: 6,
          referencePrice: 8,
          savingPercent: 25,
          packSize: '1 kg',
          unit: 'kg',
          unitPrice: 6,
          referenceUnitPrice: 8,
          observedOn: '2026-11-06',
          validUntil: null,
          rank: 3,
          flags: [],
        },
        {
          store: 'dunnes',
          storeName: 'Dunnes',
          kind: 'percentage',
          isPromo: true,
          deal: '50% off',
          promoText: null,
          card: null,
          buyQuantity: 1,
          payTotal: 2,
          eachPrice: 2,
          shelfPrice: 2,
          referencePrice: 4,
          savingPercent: 50,
          packSize: '500 g',
          unit: 'kg',
          unitPrice: 4,
          referenceUnitPrice: 8,
          observedOn: '2026-11-01',
          validUntil: null,
          rank: null,
          flags: ['stale'],
        },
      ],
      gaps: [],
    },
  ],
};
const MEMBERS = [
  {id: 'u1', displayName: 'Anna', role: 'owner', createdAt: '2026-11-01T09:00:00Z'},
  {id: 'u2', displayName: 'Ben', role: 'member', createdAt: '2026-11-02T09:00:00Z'},
];
const INVITES = [
  {id: 'i1', label: 'Chris', createdAt: '2026-11-03T09:00:00Z', expiresAt: '2026-11-04T09:00:00Z'},
];

const PASSKEYS = [
  {id: 'k1', name: 'Phone', createdAt: '2026-11-01T09:00:00Z', backedUp: true},
  {id: 'k2', name: 'Laptop', createdAt: '2026-11-04T09:00:00Z', backedUp: false},
];

const INGEST_TOKENS = [{id: 't1', label: 'Home PC', createdAt: '2026-11-05T09:00:00Z'}];

export interface Route {
  /** Names the screen in a test title and in the baseline file (`<name>-<size>-<scheme>`). */
  readonly name: string;
  readonly path: string;
  /** Mocks the API as the person who reaches this route sees it. */
  readonly setup: (page: Page) => Promise<void>;
}

export async function signedIn(page: Page): Promise<void> {
  await page.route('**/api/session', r => r.fulfill({json: OWNER}));
  await page.route('**/api/list', r => r.fulfill({json: {items: ITEMS}}));
  await page.route('**/api/trips', r => r.fulfill({json: TRIPS}));
  await page.route('**/api/products', r => r.fulfill({json: PRODUCTS}));
  await page.route('**/api/promotions', r => r.fulfill({json: PROMOTIONS}));
  await page.route('**/api/members', r => r.fulfill({json: MEMBERS}));
  await page.route('**/api/invites', r => r.fulfill({json: INVITES}));
  await page.route('**/api/passkeys', r => r.fulfill({json: PASSKEYS}));
  await page.route('**/api/ingest-tokens', r => r.fulfill({json: INGEST_TOKENS}));
}

export async function guest(page: Page): Promise<void> {
  await page.route('**/api/session', r => r.fulfill({json: GUEST}));
  await page.route('**/api/auth/invite', r => r.fulfill({json: {role: 'member', label: null}}));
}

export const ROUTES: readonly Route[] = [
  {name: 'list', path: '/list', setup: signedIn},
  {name: 'history', path: '/history', setup: signedIn},
  {name: 'prices', path: '/prices', setup: signedIn},
  {name: 'promotions', path: '/promotions', setup: signedIn},
  {name: 'people', path: '/people', setup: signedIn},
  {name: 'settings', path: '/settings', setup: signedIn},
  {name: 'more', path: '/more', setup: signedIn},
  {
    // A device that last saw an older release: What's new wears its dot.
    name: 'more-whats-new',
    path: '/more',
    setup: async page => {
      await signedIn(page);
      await page.addInitScript(() => localStorage.setItem('basket.whatsNew.lastSeen', '0.0.1'));
    },
  },
  {name: 'sign-in', path: '/sign-in', setup: guest},
  {name: 'join', path: '/join#invite-token', setup: guest},
  {
    // The same link in a browser that cannot make passkeys: the open-in-browser notice.
    name: 'join-without-passkeys',
    path: '/join#invite-token',
    setup: async page => {
      await guest(page);
      await page.addInitScript(() => Reflect.deleteProperty(window, 'PublicKeyCredential'));
    },
  },
  {
    name: 'unreachable',
    path: '/unreachable',
    setup: page => page.route('**/api/session', r => r.abort('failed')),
  },
];
