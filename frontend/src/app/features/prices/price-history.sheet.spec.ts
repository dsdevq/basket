import {type ComponentFixture, TestBed} from '@angular/core/testing';
import {CMN_DRAWER_DATA} from '@lifekit-hq/ui';

import type {PriceHistory} from './price-history.model';
import {PriceHistorySheetComponent} from './price-history.sheet';
import {PricesApi} from './prices.api';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, 0));
  fixture.detectChanges();
}

const HISTORY: PriceHistory = {
  productId: 'p1',
  store: 'lidl',
  storeName: 'Lidl',
  days: 90,
  from: '2026-08-13',
  to: '2026-11-10',
  points: [
    {
      observedOn: '2026-10-01',
      effective: 2.5,
      price: 2.5,
      wasPrice: null,
      loyaltyPrice: null,
      loyaltyScheme: null,
      promoText: null,
      inStock: true,
    },
    {
      observedOn: '2026-11-10',
      effective: 1.8,
      price: 1.8,
      wasPrice: 2.5,
      loyaltyPrice: null,
      loyaltyScheme: null,
      promoText: null,
      inStock: true,
    },
  ],
  lowest: {price: 1.8, on: '2026-11-10'},
  highest: {price: 2.5, on: '2026-10-01'},
  current: 1.8,
  lowestIn90Days: true,
};

async function render(history: Promise<PriceHistory>) {
  const api = {history: vi.fn().mockReturnValue(history)};
  TestBed.configureTestingModule({
    providers: [
      {provide: PricesApi, useValue: api},
      {
        provide: CMN_DRAWER_DATA,
        useValue: {productId: 'p1', productName: 'Butter', store: 'lidl', storeName: 'Lidl'},
      },
    ],
  });
  const fixture = TestBed.createComponent(PriceHistorySheetComponent);
  fixture.detectChanges();
  await settle(fixture);
  return {api, host: fixture.nativeElement as HTMLElement};
}

describe('PriceHistorySheetComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('reads the product and shop it was opened for and shows the line, the summary and the changes', async () => {
    const {api, host} = await render(Promise.resolve(HISTORY));

    expect(api.history).toHaveBeenCalledWith('p1', 'lidl');
    expect(host.querySelector('svg path')).not.toBeNull();
    expect(host.querySelector('[data-testid="history-summary"]')?.textContent).toContain(
      'lowest in 90 days'
    );
    expect(host.textContent).toContain('1 Oct');
    expect(host.textContent).toContain('€2.50');
  });

  it('draws nothing when no price was read', async () => {
    const {host} = await render(
      Promise.resolve({
        ...HISTORY,
        points: [],
        lowest: null,
        highest: null,
        current: null,
        lowestIn90Days: false,
      })
    );

    expect(host.querySelector('svg')).toBeNull();
    expect(host.textContent).toContain('No price has been read');
  });

  it('says when the history did not load', async () => {
    const {host} = await render(Promise.reject(new Error('offline')));

    expect(host.querySelector('[data-testid="history-error"]')).not.toBeNull();
  });
});
