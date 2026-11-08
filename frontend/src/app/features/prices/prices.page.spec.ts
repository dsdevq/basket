import {type ComponentFixture, TestBed} from '@angular/core/testing';
import {CmnDrawerService, ToastService} from '@lifekit-hq/ui';
import {of} from 'rxjs';

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
        },
      },
      {
        ...ALDI,
        listing: {
          url: 'https://www.aldi.ie/product/milk-000000000000423109',
          title: null,
          checkedAt: null,
          error: null,
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
        },
      },
      TESCO,
    ],
  };
}

async function render(response: PricesResponse) {
  const api = {
    all: vi.fn().mockResolvedValue(response),
    add: vi.fn(),
    remove: vi.fn().mockResolvedValue(undefined),
    link: vi.fn(),
    unlink: vi.fn(),
  };
  const drawer = {open: vi.fn().mockReturnValue({afterClosed: () => of(undefined)})};
  TestBed.configureTestingModule({
    providers: [
      {provide: PricesApi, useValue: api},
      {provide: CmnDrawerService, useValue: drawer},
      {provide: ToastService, useValue: {error: vi.fn(), success: vi.fn()}},
    ],
  });
  const fixture = TestBed.createComponent(PricesPage);
  fixture.detectChanges();
  await settle(fixture);
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
    expect(text).toContain('Waiting for the next PC read');
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

  it('opens the link sheet for the shop that was tapped', async () => {
    const {host, drawer} = await render({products: [milk()], stores: []});

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

  it('shows an error state with a retry when the prices do not load', async () => {
    TestBed.configureTestingModule({
      providers: [
        {provide: PricesApi, useValue: {all: vi.fn().mockRejectedValue(new Error('offline'))}},
        {provide: CmnDrawerService, useValue: {open: vi.fn()}},
        {provide: ToastService, useValue: {error: vi.fn()}},
      ],
    });
    const fixture = TestBed.createComponent(PricesPage);
    fixture.detectChanges();
    await settle(fixture);

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('The prices did not load');
  });
});
