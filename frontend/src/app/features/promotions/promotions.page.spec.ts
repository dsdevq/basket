import {type ComponentFixture, TestBed} from '@angular/core/testing';
import {ToastService} from '@lifekit-hq/ui';

import {PromotionsApi} from './promotions.api';
import type {PromotionsResponse, UsualOffersResponse} from './promotions.model';
import {PromotionsPage} from './promotions.page';
import {gap, offer, product, usualOffer, usualProduct} from './promotions-fixtures';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, 0));
  fixture.detectChanges();
  await Promise.resolve();
  fixture.detectChanges();
}

const RESPONSE: PromotionsResponse = {
  asOf: '2026-11-10',
  staleAfterDays: 3,
  products: [
    product({
      ranked: 3,
      offers: [
        offer({
          store: 'tesco',
          storeName: 'Tesco',
          kind: 'loyalty',
          deal: 'Clubcard price',
          card: 'Clubcard',
          buyQuantity: 1,
          payTotal: 3.2,
          eachPrice: 3.2,
          unitPrice: 4.92,
          savingPercent: 20,
          rank: 1,
        }),
        offer({store: 'lidl', storeName: 'Lidl', unitPrice: 5, rank: 2, lowestIn90Days: true}),
        offer({
          store: 'aldi',
          storeName: 'Aldi',
          kind: 'percentage',
          deal: '25% off',
          buyQuantity: 1,
          payTotal: 6,
          eachPrice: 6,
          unitPrice: 6,
          savingPercent: 25,
          rank: 3,
        }),
        offer({store: 'dunnes', storeName: 'Dunnes', unitPrice: 4, rank: null, flags: ['stale']}),
      ],
      gaps: [gap({store: 'dunnes', storeName: 'Dunnes', reason: 'ended', date: '2026-11-05'})],
    }),
  ],
};

const NO_USUAL: UsualOffersResponse = {asOf: '2026-11-10', staleAfterDays: 3, products: []};

async function render(response: PromotionsResponse, usual: UsualOffersResponse = NO_USUAL) {
  const api = {all: vi.fn().mockResolvedValue(response), usual: vi.fn().mockResolvedValue(usual)};
  TestBed.configureTestingModule({
    providers: [
      {provide: PromotionsApi, useValue: api},
      {provide: ToastService, useValue: {error: vi.fn(), success: vi.fn()}},
    ],
  });
  const fixture = TestBed.createComponent(PromotionsPage);
  fixture.detectChanges();
  await settle(fixture);
  return {fixture, api, host: fixture.nativeElement as HTMLElement};
}

describe('PromotionsPage', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('shows the ranking rule on the page, with the staleness limit the server uses', async () => {
    const {host} = await render(RESPONSE);

    const rule = host.querySelector('[data-testid="rule"]')?.textContent ?? '';
    expect(rule).toContain('one kilo, litre or piece');
    expect(rule).toContain('2 for €4 is €2 each');
    expect(rule).toContain('over 3 days old');
  });

  it('lists each product with its offers cheapest per unit first, and the best one marked', async () => {
    const {host} = await render(RESPONSE);

    const text = host.textContent ?? '';
    expect(text).toContain('Chicken fillets, per kg');
    expect(text.indexOf('€4.92/kg')).toBeLessThan(text.indexOf('€5.00/kg'));
    expect(text.indexOf('€5.00/kg')).toBeLessThan(text.indexOf('€6.00/kg'));
    expect(text).toContain('Best of 3');
    expect(text).toContain('#2 of 3');
    expect(text).toContain('Clubcard price · needs Clubcard · saves 20%');
    expect(text).toContain('2 for €4');
    expect(text).toContain('25% off');
  });

  it('marks the offer that is the lowest in 90 days, and only that one', async () => {
    const {host} = await render(RESPONSE);

    const text = host.textContent ?? '';
    expect(text.split('Lowest in 90 days').length - 1).toBe(1);
  });

  it('shows an old reading with its date and the reason, not as a ranked price', async () => {
    const {host} = await render(RESPONSE);

    const text = host.textContent ?? '';
    expect(text).toContain('Dunnes');
    expect(text).toContain('Old reading, not ranked');
    expect(text).toContain('Offer ended 5 Nov');
  });

  it('switches to the shop by shop list', async () => {
    const {fixture, host} = await render(RESPONSE);

    const cells = host.querySelectorAll<HTMLElement>('cmn-segmented button');
    const byStore = [...cells].find(cell => cell.textContent?.includes('By store'));
    byStore?.click();
    await settle(fixture);

    const headings = [...host.querySelectorAll('cmn-list-section')].map(
      section => section.textContent ?? ''
    );
    expect(headings.some(text => text.includes('Tesco') && text.includes('Chicken fillets'))).toBe(
      true
    );
    expect(headings.some(text => text.includes('Lidl') && text.includes('Chicken fillets'))).toBe(
      true
    );
  });

  it('opens on the usual products on offer, found in a shop with no linked page, and sends nothing', async () => {
    const {host} = await render(RESPONSE, {
      ...NO_USUAL,
      products: [
        usualProduct({name: 'Whole milk 2L', offers: [usualOffer({title: 'Milbona whole milk'})]}),
      ],
    });

    const text = host.textContent ?? '';
    expect(text).toContain('Your usual products on offer now');
    expect(text).toContain('Whole milk 2L');
    expect(text).toContain('Milbona whole milk');
    expect(text).toContain('Lidl');
    expect(text).toContain('€1.49 a pack');
    expect(host.querySelector('[data-testid="usual-rule"]')?.textContent).toContain(
      'Nothing is sent'
    );
    expect(text).not.toContain('No promotions right now');
  });

  it('opens on the best buys when no usual product is on offer, and the usual view says so', async () => {
    const {fixture, host} = await render(RESPONSE);

    expect(host.textContent).toContain('Chicken fillets, per kg');
    const usualTab = [...host.querySelectorAll<HTMLElement>('cmn-segmented button')].find(cell =>
      cell.textContent?.includes('Your usual')
    );
    usualTab?.click();
    await settle(fixture);

    expect(host.textContent).toContain('Nothing on offer from your usual products');
  });

  it('keeps the promotions when only the usual products fail to load', async () => {
    const api = {
      all: vi.fn().mockResolvedValue(RESPONSE),
      usual: vi.fn().mockRejectedValue(new Error('offline')),
    };
    TestBed.configureTestingModule({
      providers: [
        {provide: PromotionsApi, useValue: api},
        {provide: ToastService, useValue: {error: vi.fn(), success: vi.fn()}},
      ],
    });
    const fixture = TestBed.createComponent(PromotionsPage);
    fixture.detectChanges();
    await settle(fixture);

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Chicken fillets');
  });

  it('says so when nothing is on offer', async () => {
    const {host} = await render({asOf: '2026-11-10', staleAfterDays: 3, products: []});

    expect(host.textContent).toContain('No promotions right now');
  });

  it('offers a retry when the promotions did not load', async () => {
    const api = {all: vi.fn().mockRejectedValue(new Error('offline')), usual: vi.fn()};
    TestBed.configureTestingModule({
      providers: [
        {provide: PromotionsApi, useValue: api},
        {provide: ToastService, useValue: {error: vi.fn(), success: vi.fn()}},
      ],
    });
    const fixture = TestBed.createComponent(PromotionsPage);
    fixture.detectChanges();
    await settle(fixture);

    expect((fixture.nativeElement as HTMLElement).textContent).toContain(
      'The promotions did not load'
    );
  });
});
