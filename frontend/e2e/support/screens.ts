import type {Page} from '@playwright/test';

// The app as the screen specs see it: one API mock per signed-in or guest visitor, and the list of
// routed pages. The screen check, the baselines and the route scan all read it, so a new page is
// added here once and every gate covers it.

/** Pinned so a date on screen ("added today") cannot move a baseline. */
export const FROZEN_NOW = new Date('2026-11-06T10:00:00Z');

const OWNER = {authenticated: true, user: {id: 'u1', displayName: 'Anna', role: 'owner'}};
const GUEST = {authenticated: false, user: null};
const ITEMS: {text: string; checked: boolean; productId?: string; store?: string}[] = [
  {text: 'Milk', checked: false},
  {text: 'Bread', checked: false},
  {text: 'Apples', checked: false},
  {text: 'Rice', checked: true},
  {text: 'Whole milk 2L', checked: false, productId: 'p1', store: 'tesco'},
].map((item, i) => ({
  id: `item-${i}`,
  ...item,
  productId: item.productId ?? null,
  store: item.store ?? null,
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
/** A flat tinted square as a product photo, so the baselines never wait on a shop's image server. */
const PHOTO = (colour: string): string =>
  `data:image/svg+xml;utf8,${encodeURIComponent(
    `<svg xmlns="http://www.w3.org/2000/svg" width="112" height="112"><rect width="112" height="112" rx="12" fill="${colour}"/><rect x="34" y="22" width="44" height="68" rx="8" fill="white" fill-opacity="0.7"/></svg>`
  )}`;

type MockShop = 'lidl' | 'aldi' | 'tesco' | 'dunnes';
const SHOPS: Record<MockShop, {name: string; readBy: string}> = {
  lidl: {name: 'Lidl', readBy: 'nightly'},
  aldi: {name: 'Aldi', readBy: 'pc'},
  tesco: {name: 'Tesco', readBy: 'pc'},
  dunnes: {name: 'Dunnes', readBy: 'pc'},
};

function entry(shop: MockShop, listing: {imageUrl?: string | null; price?: object} | null) {
  return {
    store: shop,
    ...SHOPS[shop],
    listing: listing && {
      url: `https://example.test/${shop}`,
      title: null,
      checkedAt: '2026-11-05T03:00:00Z',
      error: null,
      imageUrl: listing.imageUrl ?? null,
    },
    price: listing?.price ?? null,
    lastPaid: shop === 'lidl' ? {state: 'not_connected', price: null, paidOn: null} : null,
  };
}

function price(over: object) {
  return {
    observedOn: '2026-11-05',
    state: 'current',
    price: null,
    wasPrice: null,
    loyaltyPrice: null,
    loyaltyScheme: null,
    promoText: null,
    validFrom: null,
    validUntil: null,
    best: null,
    inStock: true,
    ...over,
  };
}

export const PRODUCTS = {
  products: [
    {
      id: 'p1',
      name: 'Whole milk 2L',
      stores: [
        entry('lidl', {
          imageUrl: PHOTO('#cfe3ff'),
          price: price({
            price: 1.29,
            wasPrice: 1.49,
            loyaltyPrice: 0.99,
            loyaltyScheme: 'Lidl Plus',
            promoText: 'Lidl Plus deal',
            validUntil: '2026-11-11',
            best: 0.99,
          }),
        }),
        entry('aldi', null),
        entry('tesco', {
          imageUrl: PHOTO('#ffd9d9'),
          price: price({
            price: 2.15,
            wasPrice: 2.35,
            loyaltyPrice: 1.89,
            loyaltyScheme: 'Clubcard',
            promoText: 'Clubcard Price',
            validUntil: '2026-11-15',
            best: 1.89,
          }),
        }),
        entry('dunnes', null),
      ],
    },
    {
      id: 'p2',
      name: 'Brennans sliced pan 800g',
      stores: [
        entry('lidl', null),
        entry('aldi', null),
        entry('tesco', {price: price({price: 1.85, best: 1.85})}),
        entry('dunnes', null),
      ],
    },
    {
      id: 'p3',
      name: 'Kerrygold butter 227g',
      stores: [
        entry('lidl', null),
        entry('aldi', {imageUrl: PHOTO('#fff1c2'), price: price({price: 2.99, best: 2.99})}),
        entry('tesco', {
          imageUrl: PHOTO('#fff1c2'),
          price: price({state: 'unavailable', inStock: false}),
        }),
        entry('dunnes', null),
      ],
    },
    {
      id: 'p4',
      name: 'Free-range eggs, a dozen, large, from a farm with a very long name',
      stores: [
        entry('lidl', null),
        entry('aldi', {price: price({state: 'upcoming', price: 2.49, validFrom: '2026-11-12'})}),
        entry('tesco', null),
        entry('dunnes', null),
      ],
    },
  ],
  stores: [
    {
      store: 'tesco',
      name: 'Tesco',
      readBy: 'pc',
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
type PlanShop = 'lidl' | 'aldi' | 'tesco' | 'dunnes';
const PLAN_SHOP_NAMES: Record<PlanShop, string> = {
  lidl: 'Lidl',
  aldi: 'Aldi',
  tesco: 'Tesco',
  dunnes: 'Dunnes',
};

/** One shop's reading of a line, as GET /api/plan sends it: the reading itself and the normalised offer (null when nothing usable). */
function planShop(
  store: PlanShop,
  state: string,
  reading: {observedOn?: string; price: number | null; wasPrice?: number; promoText?: string},
  offer: object | null,
  best = false
) {
  return {
    store,
    storeName: PLAN_SHOP_NAMES[store],
    state,
    observedOn: reading.observedOn ?? '2026-11-06',
    price: reading.price,
    wasPrice: reading.wasPrice ?? null,
    promoText: reading.promoText ?? null,
    offer: offer && {
      kind: 'regular',
      isPromo: false,
      deal: 'Shelf price',
      promoText: null,
      card: null,
      buyQuantity: 1,
      payTotal: reading.price,
      eachPrice: reading.price,
      shelfPrice: reading.price,
      referencePrice: null,
      savingPercent: null,
      packSize: null,
      unit: null,
      unitPrice: null,
      referenceUnitPrice: null,
      observedOn: reading.observedOn ?? '2026-11-06',
      validUntil: null,
      rank: null,
      flags: [],
      store,
      storeName: PLAN_SHOP_NAMES[store],
      ...offer,
    },
    best,
  };
}

/** What a receipt says was paid at a shop, as GET /api/plan sends it. */
function planPaid(store: PlanShop, price: number, paidOn: string) {
  return {store, storeName: PLAN_SHOP_NAMES[store], price, paidOn};
}

function planLine(
  text: string,
  over: {
    productId?: string;
    flag?: string;
    bestStore?: string;
    stores?: ReturnType<typeof planShop>[];
    lastPaid?: ReturnType<typeof planPaid>[];
  } = {}
) {
  return {
    id: `plan-${text}`,
    text,
    productId: over.productId ?? null,
    productName: over.productId ? text : null,
    pickedStore: null,
    flag: over.flag ?? null,
    bestStore: over.bestStore ?? null,
    stores: over.stores ?? [],
    lastPaid: over.lastPaid ?? [],
  };
}

const MILK_LIDL = planShop(
  'lidl',
  'priced',
  {price: 1.29, wasPrice: 1.49, promoText: 'Lidl Plus deal'},
  {
    kind: 'loyalty',
    isPromo: true,
    deal: 'Lidl Plus price',
    card: 'Lidl Plus',
    eachPrice: 0.99,
    savingPercent: 33,
    packSize: '2 L',
    unit: 'l',
    unitPrice: 0.5,
    rank: 1,
  },
  true
);
const MILK_LIDL_PAID = planPaid('lidl', 1.19, '2026-11-03');
const MILK_TESCO = planShop(
  'tesco',
  'priced',
  {price: 2.15, wasPrice: 2.35, promoText: 'Clubcard Price'},
  {
    kind: 'loyalty',
    isPromo: true,
    deal: 'Clubcard price',
    card: 'Clubcard',
    eachPrice: 1.89,
    savingPercent: 20,
    packSize: '2 L',
    unit: 'l',
    unitPrice: 0.95,
    rank: 2,
  }
);
const BUTTER_ALDI = planShop(
  'aldi',
  'priced',
  {price: 2.99},
  {eachPrice: 2.99, packSize: '227 g', unit: 'kg', unitPrice: 13.17, rank: 1},
  true
);
const BUTTER_TESCO_OUT = planShop(
  'tesco',
  'out_of_stock',
  {price: 3.19},
  {
    eachPrice: 3.19,
    packSize: '227 g',
    unit: 'kg',
    unitPrice: 14.05,
    rank: null,
    flags: ['out_of_stock'],
  }
);

/** A list nobody can shop in one place: one best shop for the lines that have one, and a line of each flag that explains why not. */
const PLAN = {
  asOf: '2026-11-06',
  staleAfterDays: 3,
  items: [
    planLine('Whole milk 2L', {
      productId: 'p1',
      bestStore: 'lidl',
      stores: [MILK_LIDL, MILK_TESCO],
      lastPaid: [MILK_LIDL_PAID],
    }),
    planLine('Kerrygold butter 227g', {
      productId: 'p3',
      bestStore: 'aldi',
      stores: [BUTTER_ALDI, BUTTER_TESCO_OUT],
    }),
    planLine('Brennans sliced pan 800g', {
      productId: 'p2',
      flag: 'stale',
      stores: [
        planShop(
          'tesco',
          'stale',
          {observedOn: '2026-11-01', price: 1.85},
          {observedOn: '2026-11-01', eachPrice: 1.85, rank: null, flags: ['stale']}
        ),
      ],
    }),
    planLine('Free-range eggs, a dozen', {
      productId: 'p4',
      flag: 'no_price',
      stores: [planShop('aldi', 'upcoming', {price: 2.49}, null)],
    }),
    planLine('Bananas', {flag: 'unmatched'}),
    // Came in on a Lidl Plus receipt: bought at Lidl, but no Lidl page to read a price from.
    planLine('Chicken breast fillets', {
      productId: 'p5',
      flag: 'no_price',
      lastPaid: [planPaid('lidl', 5.49, '2026-11-02')],
    }),
  ],
  coverage: {
    singleStore: false,
    store: null,
    coveredBy: [],
    closestStore: 'lidl',
    blockers: [
      {itemId: 'plan-Kerrygold butter 227g', text: 'Kerrygold butter 227g', reason: 'not_linked'},
      {itemId: 'plan-Brennans sliced pan 800g', text: 'Brennans sliced pan 800g', reason: 'stale'},
      {
        itemId: 'plan-Free-range eggs, a dozen',
        text: 'Free-range eggs, a dozen',
        reason: 'no_price',
      },
      {itemId: 'plan-Bananas', text: 'Bananas', reason: 'unmatched'},
      {
        itemId: 'plan-Chicken breast fillets',
        text: 'Chicken breast fillets',
        reason: 'no_price',
      },
    ],
  },
};

/** The happy case: two shops price both lines, so the list can be done in one. */
const PLAN_COVERED = {
  asOf: '2026-11-06',
  staleAfterDays: 3,
  items: [
    planLine('Whole milk 2L', {
      productId: 'p1',
      bestStore: 'lidl',
      stores: [MILK_LIDL, MILK_TESCO],
      lastPaid: [MILK_LIDL_PAID],
    }),
    planLine('Brennans sliced pan 800g', {
      productId: 'p2',
      bestStore: 'tesco',
      stores: [
        planShop(
          'lidl',
          'priced',
          {price: 1.99},
          {eachPrice: 1.99, packSize: '800 g', unit: 'kg', unitPrice: 2.49, rank: 2}
        ),
        planShop(
          'tesco',
          'priced',
          {price: 1.85},
          {eachPrice: 1.85, packSize: '800 g', unit: 'kg', unitPrice: 2.31, rank: 1},
          true
        ),
      ],
    }),
  ],
  coverage: {
    singleStore: true,
    store: 'lidl',
    coveredBy: ['lidl', 'tesco'],
    closestStore: null,
    blockers: [],
  },
};
const PLAN_EMPTY = {
  asOf: '2026-11-06',
  staleAfterDays: 3,
  items: [],
  coverage: {singleStore: false, store: null, coveredBy: [], closestStore: null, blockers: []},
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

const CARDS = [
  {
    store: 'lidl',
    name: 'Lidl',
    format: 'qr',
    masked: '•••• 4821',
    readable: true,
    updatedAt: '2026-11-05T09:00:00Z',
  },
  {
    store: 'tesco',
    name: 'Tesco',
    format: 'code128',
    masked: '•••• 8923',
    readable: true,
    updatedAt: '2026-11-05T09:00:00Z',
  },
];
const TESCO_CARD = {
  store: 'tesco',
  name: 'Tesco',
  format: 'code128',
  number: '634004012345678923',
  updatedAt: '2026-11-05T09:00:00Z',
};

const INGEST_TOKENS = [{id: 't1', label: 'Home PC', createdAt: '2026-11-05T09:00:00Z'}];

const LIDL_PLUS = {
  state: 'connected',
  connectedAt: '2026-11-05T09:00:00Z',
  verifiedAt: '2026-11-09T18:30:00Z',
};

export interface Route {
  /** Names the screen in a test title and in the baseline file (`<name>-<size>-<scheme>`). */
  readonly name: string;
  readonly path: string;
  /** Mocks the API as the person who reaches this route sees it. */
  readonly setup: (page: Page) => Promise<void>;
  /** Brings up what the route shows only after a tap (a full-screen dialog), once the page has loaded. */
  readonly reveal?: (page: Page) => Promise<void>;
}

export async function signedIn(page: Page): Promise<void> {
  await page.route('**/api/session', r => r.fulfill({json: OWNER}));
  await page.route('**/api/list', r => r.fulfill({json: {items: ITEMS}}));
  await page.route('**/api/trips', r => r.fulfill({json: TRIPS}));
  await page.route('**/api/products', r => r.fulfill({json: PRODUCTS}));
  await page.route('**/api/promotions', r => r.fulfill({json: PROMOTIONS}));
  await page.route('**/api/plan', r => r.fulfill({json: PLAN}));
  await page.route('**/api/cards', r => r.fulfill({json: {cards: CARDS}}));
  await page.route('**/api/cards/tesco', r => r.fulfill({json: TESCO_CARD}));
  await page.route('**/api/members', r => r.fulfill({json: MEMBERS}));
  await page.route('**/api/invites', r => r.fulfill({json: INVITES}));
  await page.route('**/api/passkeys', r => r.fulfill({json: PASSKEYS}));
  await page.route('**/api/ingest-tokens', r => r.fulfill({json: INGEST_TOKENS}));
  await page.route('**/api/lidl-plus/connection', r => r.fulfill({json: LIDL_PLUS}));
}

export async function guest(page: Page): Promise<void> {
  await page.route('**/api/session', r => r.fulfill({json: GUEST}));
  await page.route('**/api/auth/invite', r => r.fulfill({json: {role: 'member', label: null}}));
}

export const ROUTES: readonly Route[] = [
  {name: 'list', path: '/list', setup: signedIn},
  {name: 'history', path: '/history', setup: signedIn},
  {name: 'shelf', path: '/shelf', setup: signedIn},
  {name: 'plan', path: '/plan', setup: signedIn},
  {
    name: 'plan-covered',
    path: '/plan',
    setup: async page => {
      await signedIn(page);
      await page.route('**/api/plan', r => r.fulfill({json: PLAN_COVERED}));
    },
  },
  {
    name: 'plan-empty',
    path: '/plan',
    setup: async page => {
      await signedIn(page);
      await page.route('**/api/plan', r => r.fulfill({json: PLAN_EMPTY}));
    },
  },
  {name: 'prices', path: '/prices', setup: signedIn},
  {name: 'promotions', path: '/promotions', setup: signedIn},
  {name: 'cards', path: '/cards', setup: signedIn},
  {name: 'card', path: '/cards/tesco', setup: signedIn},
  {
    name: 'card-till',
    path: '/cards/tesco',
    setup: signedIn,
    reveal: async page => {
      await page.getByRole('button', {name: 'Show at till'}).click();
      await page.locator('[role=dialog] canvas[role=img]').waitFor();
    },
  },
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
