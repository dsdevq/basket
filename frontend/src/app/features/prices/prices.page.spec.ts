import {type ComponentFixture, TestBed} from '@angular/core/testing';
import {ActivatedRoute, convertToParamMap} from '@angular/router';
import {CmnDrawerService, ToastService} from '@lifekit-hq/ui';
import {of} from 'rxjs';

import {HabitsApi} from '../habits/habits.api';
import {health, neverSwept} from './catalog-health-fixtures';
import {MatchSheetComponent} from './match.sheet';
import {PricesApi} from './prices.api';
import type {PricesResponse, Product, ProductStore} from './prices.model';
import {PricesPage} from './prices.page';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, 0));
  fixture.detectChanges();
  await Promise.resolve();
  fixture.detectChanges();
}

const LIDL: ProductStore = {
  store: 'lidl',
  name: 'Lidl',
  readBy: 'nightly',
  listing: null,
  price: null,
  lastPaid: {state: 'not_connected', price: null, paidOn: null},
};
const ALDI: ProductStore = {
  store: 'aldi',
  name: 'Aldi',
  readBy: 'pc',
  listing: null,
  price: null,
  lastPaid: null,
};
const TESCO: ProductStore = {
  store: 'tesco',
  name: 'Tesco',
  readBy: 'pc',
  listing: null,
  price: null,
  lastPaid: null,
};

function milk(): Product {
  return {
    id: 'p1',
    name: 'Whole milk 2L',
    stores: [
      {
        ...LIDL,
        listing: {
          url: 'https://www.lidl.ie/p/milk/p11000',
          title: 'Milk',
          checkedAt: null,
          error: null,
          imageUrl: null,
        },
        price: {
          observedOn: '2026-11-05',
          state: 'current',
          price: 1.29,
          wasPrice: null,
          loyaltyPrice: 0.99,
          loyaltyScheme: 'Lidl Plus',
          promoText: null,
          validFrom: null,
          validUntil: '2026-11-11',
          best: 0.99,
          inStock: true,
        },
      },
      {
        ...ALDI,
        listing: {
          url: 'https://www.aldi.ie/product/milk-000000000000423109',
          title: null,
          checkedAt: null,
          error: null,
          imageUrl: null,
        },
        price: {
          observedOn: '2026-11-04',
          state: 'current',
          price: 1.19,
          wasPrice: null,
          loyaltyPrice: null,
          loyaltyScheme: null,
          promoText: null,
          validFrom: null,
          validUntil: null,
          best: 1.19,
          inStock: true,
        },
      },
      TESCO,
    ],
  };
}

/** `cmn-async-state` keeps a shown skeleton up for a moment, so wait until the loading notice is gone. */
async function loaded(fixture: ComponentFixture<unknown>): Promise<void> {
  await vi.waitFor(() => {
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('Loading');
  });
}

async function render(
  response: PricesResponse,
  query: Record<string, string> = {},
  habits: {
    productId: string;
    name: string;
    boughtIn: number;
    lastBoughtAt: string;
    usual: boolean;
  }[] = []
) {
  const api = {
    all: vi.fn().mockResolvedValue(response),
    add: vi.fn(),
    remove: vi.fn().mockResolvedValue(undefined),
    link: vi.fn(),
    unlink: vi.fn(),
    candidates: vi.fn(),
    confirm: vi.fn(),
    reject: vi.fn(),
  };
  const drawer = {open: vi.fn().mockReturnValue({afterClosed: () => of(undefined)})};
  TestBed.configureTestingModule({
    providers: [
      {provide: PricesApi, useValue: api},
      {provide: CmnDrawerService, useValue: drawer},
      {provide: HabitsApi, useValue: {all: vi.fn().mockResolvedValue(habits)}},
      {provide: ActivatedRoute, useValue: {snapshot: {queryParamMap: convertToParamMap(query)}}},
      {provide: ToastService, useValue: {error: vi.fn(), success: vi.fn()}},
    ],
  });
  const fixture = TestBed.createComponent(PricesPage);
  fixture.detectChanges();
  await settle(fixture);
  await loaded(fixture);
  return {fixture, api, drawer, host: fixture.nativeElement as HTMLElement};
}

describe('PricesPage', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('prompts for the first product when there are none', async () => {
    const {host} = await render({products: [], stores: []});

    expect(host.textContent).toContain('No products yet');
  });

  it('shows every product with its three shops, the best price and the day it was seen', async () => {
    const {host} = await render({products: [milk()], stores: []});

    const text = host.textContent ?? '';
    expect(text).toContain('Whole milk 2L');
    expect(text).toContain('€0.99');
    expect(text).toContain('Lidl Plus €0.99, €1.29 without');
    expect(text).toContain('Seen 5 Nov');
    expect(text).toContain('€1.19');
    expect(text).toContain('Seen 4 Nov');
    expect(text).not.toContain('No page linked yet');
    expect(text).toContain('1 shop with no page linked yet');
    expect(text).not.toContain('Waiting for the next PC read');
    expect(text).not.toContain('unavailable');
    expect(text).toContain('Last paid: not yet connected');
  });

  it('warns about a shop that blocks the nightly pull and says nothing about one that works', async () => {
    const {host} = await render({
      products: [milk()],
      stores: [
        {
          store: 'lidl',
          name: 'Lidl',
          readBy: 'nightly',
          catalog: health(),
          lastRun: {
            finishedAt: '2026-11-05T03:00:00Z',
            outcome: 'blocked',
            listings: 1,
            read: 0,
            message: 'www.lidl.ie answered HTTP 403.',
          },
        },
        {
          store: 'aldi',
          name: 'Aldi',
          readBy: 'pc',
          catalog: health(),
          lastRun: {
            finishedAt: '2026-11-05T03:00:00Z',
            outcome: 'ok',
            listings: 1,
            read: 1,
            message: null,
          },
        },
      ],
    });

    const alerts = host.querySelectorAll('cmn-alert');
    expect(alerts).toHaveLength(1);
    expect(alerts[0]?.textContent).toContain("Lidl blocks basket's automated requests");
    expect(alerts[0]?.textContent).toContain('HTTP 403');
  });

  it('shows what each shop catalog is doing: last sweep, counts, discontinued and a tag', async () => {
    vi.useFakeTimers({toFake: ['Date'], now: new Date('2026-11-06T10:00:00Z')});
    try {
      const {host} = await render({
        products: [milk()],
        stores: [
          {
            store: 'lidl',
            name: 'Lidl',
            readBy: 'nightly',
            catalog: health({live: 3554, discontinued: 12, discontinuedOnShelf: 1}),
            lastRun: null,
          },
          {store: 'tesco', name: 'Tesco', readBy: 'pc', catalog: neverSwept(), lastRun: null},
        ],
      });

      const card = host.querySelector('[data-testid="catalog-health"]');
      const text = card?.textContent ?? '';
      expect(text).toContain('Shop catalogs');
      expect(text).toContain('Swept yesterday');
      expect(text).toContain('690 products listed');
      expect(text).toContain('3,554 products live');
      expect(text).toContain('12 discontinued');
      expect(text).toContain('1 on your shelf');
      expect(text).toContain('Up to date');
      expect(text).toContain('No catalog sweep recorded yet');
      expect(text).toContain('No sweep yet');
      expect(host.querySelectorAll('cmn-alert')).toHaveLength(0);
    } finally {
      vi.useRealTimers();
    }
  });

  it('warns about a stale catalog and about a sweep that saw under 90% of the one before', async () => {
    vi.useFakeTimers({toFake: ['Date'], now: new Date('2026-11-12T10:00:00Z')});
    try {
      const {host} = await render({
        products: [],
        stores: [
          {
            store: 'lidl',
            name: 'Lidl',
            readBy: 'nightly',
            catalog: health({state: 'stale'}),
            lastRun: null,
          },
          {
            store: 'aldi',
            name: 'Aldi',
            readBy: 'nightly',
            catalog: health({
              state: 'fresh',
              lastSweep: {
                finishedAt: '2026-11-12T03:30:00Z',
                outcome: 'partial',
                seen: 1200,
                live: 3554,
                message: null,
              },
              lastOkAt: '2026-11-11T03:30:00Z',
              shrunk: {seen: 1200, previous: 3554},
            }),
            lastRun: null,
          },
        ],
      });

      const alerts = Array.from(host.querySelectorAll('cmn-alert')).map(a => a.textContent ?? '');
      expect(alerts).toHaveLength(2);
      expect(alerts[0]).toContain("Lidl's catalog is stale");
      expect(alerts[0]).toContain('more than 2 days');
      expect(alerts[1]).toContain("Aldi's last sweep listed 1,200 products");
      expect(alerts[1]).toContain('under 90% of the 3,554');
      const card = host.querySelector('[data-testid="catalog-health"]')?.textContent ?? '';
      expect(card).toContain('Stale');
      expect(card).toContain('Shrunk');
    } finally {
      vi.useRealTimers();
    }
  });

  it('opens the link sheet for the shop that was tapped', async () => {
    const {fixture, host, drawer} = await render({products: [milk()], stores: []});

    host
      .querySelector<HTMLElement>(
        '[data-testid="unlinked-toggle"] button, [data-testid="unlinked-toggle"] [role="button"]'
      )
      ?.click();
    fixture.detectChanges();
    const rows = host.querySelectorAll<HTMLElement>(
      'cmn-list-item-row button, cmn-list-item-row [role="button"]'
    );
    rows[2]?.click();

    expect(drawer.open).toHaveBeenCalledTimes(1);
    expect(drawer.open.mock.calls[0]?.[1]).toMatchObject({
      title: 'Whole milk 2L at Tesco',
      mode: 'sheet',
    });
  });

  it('opens the match sheet, not the paste sheet, for a shop with suggested pages, and says how many', async () => {
    const product = milk();
    const response = {
      products: [
        {
          ...product,
          stores: product.stores.map(entry =>
            entry.store === 'tesco' ? {...entry, candidates: 2} : entry
          ),
        },
      ],
      stores: [],
    };
    const {host, drawer} = await render(response);

    expect(host.textContent).toContain('2 suggested pages to check');
    host
      .querySelectorAll<HTMLElement>(
        '[data-testid="to-check"] cmn-list-item-row button, [data-testid="to-check"] cmn-list-item-row [role="button"]'
      )[0]
      ?.click();

    expect(drawer.open).toHaveBeenCalledTimes(1);
    expect(drawer.open.mock.calls[0]?.[0]).toBe(MatchSheetComponent);
    expect(drawer.open.mock.calls[0]?.[1]).toMatchObject({title: 'Whole milk 2L at Tesco'});
  });

  it('lists suggestions in a To check section, most-bought product first, one row each', async () => {
    const withCandidates = (id: string, name: string, count: number): Product => ({
      id,
      name,
      stores: [{...ALDI, candidates: count}, TESCO],
    });
    const {host} = await render(
      {
        products: [
          withCandidates('p1', 'Pistachios', 1),
          withCandidates('p2', 'Carrots', 1),
          withCandidates('p3', 'Soy sauce', 3),
        ],
        stores: [],
      },
      {},
      [
        {productId: 'p3', name: 'Soy sauce', boughtIn: 4, lastBoughtAt: '2026-11-10', usual: true},
        {productId: 'p2', name: 'Carrots', boughtIn: 1, lastBoughtAt: '2026-11-01', usual: false},
      ]
    );

    const section = host.querySelector('[data-testid="to-check"]');
    expect(section?.textContent).toContain('To check (3)');
    const rows = Array.from(section?.querySelectorAll('cmn-list-item-row') ?? []).map(
      row => row.textContent ?? ''
    );
    expect(rows).toHaveLength(3);
    expect(rows[0]).toContain('Soy sauce');
    expect(rows[0]).toContain('3 suggested pages to check');
    expect(rows[1]).toContain('Carrots');
    expect(rows[2]).toContain('Pistachios');
  });

  it('shows no To check section when nothing is waiting', async () => {
    const {host} = await render({products: [milk()], stores: []});

    expect(host.querySelector('[data-testid="to-check"]')).toBeNull();
  });

  it('fills the add box with the typed item that History sent, and adds nothing until Add is tapped', async () => {
    const {host, api} = await render({products: [], stores: []}, {add: ' Oat milk '});

    expect(host.querySelector('input')?.value).toBe('Oat milk');
    expect(api.add).not.toHaveBeenCalled();
  });

  it('shows an error state with a retry when the prices do not load', async () => {
    TestBed.configureTestingModule({
      providers: [
        {provide: PricesApi, useValue: {all: vi.fn().mockRejectedValue(new Error('offline'))}},
        {provide: CmnDrawerService, useValue: {open: vi.fn()}},
        {provide: HabitsApi, useValue: {all: vi.fn().mockResolvedValue([])}},
        {provide: ActivatedRoute, useValue: {snapshot: {queryParamMap: convertToParamMap({})}}},
        {provide: ToastService, useValue: {error: vi.fn()}},
      ],
    });
    const fixture = TestBed.createComponent(PricesPage);
    fixture.detectChanges();
    await settle(fixture);

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('The prices did not load');
  });
});
