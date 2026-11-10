import {type ComponentFixture, TestBed} from '@angular/core/testing';
import {ToastService} from '@lifekit-hq/ui';

import {PlanApi} from './plan.api';
import type {PlanCoverage, PlanResponse} from './plan.model';
import {PlanPage} from './plan.page';
import {entry, line, offer} from './plan-fixtures';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, 0));
  fixture.detectChanges();
  await Promise.resolve();
  fixture.detectChanges();
}

const SPLIT: PlanCoverage = {
  singleStore: false,
  store: null,
  coveredBy: [],
  closestStore: 'lidl',
  blockers: [
    {itemId: 'b', text: 'Brennans sliced pan 800g', reason: 'stale'},
    {itemId: 'c', text: 'Bananas', reason: 'unmatched'},
  ],
};

const RESPONSE: PlanResponse = {
  asOf: '2026-11-06',
  staleAfterDays: 3,
  coverage: SPLIT,
  items: [
    line({
      id: 'a',
      text: 'Whole milk 2L',
      bestStore: 'lidl',
      stores: [
        entry({
          best: true,
          wasPrice: 1.49,
          observedOn: '2026-11-05',
          offer: offer({
            kind: 'loyalty',
            isPromo: true,
            deal: 'Lidl Plus price',
            card: 'Lidl Plus',
            eachPrice: 0.99,
            unitPrice: 0.5,
          }),
        }),
        entry({
          store: 'tesco',
          storeName: 'Tesco',
          state: 'out_of_stock',
          observedOn: '2026-11-06',
          offer: offer({store: 'tesco', storeName: 'Tesco', eachPrice: 1.89, unitPrice: 0.95}),
        }),
      ],
    }),
    line({
      id: 'b',
      text: 'Brennans sliced pan 800g',
      flag: 'stale',
      stores: [
        entry({
          store: 'tesco',
          storeName: 'Tesco',
          state: 'stale',
          observedOn: '2026-11-01',
          offer: offer({eachPrice: 1.85, unit: null, unitPrice: null}),
        }),
      ],
    }),
    line({id: 'c', text: 'Bananas', productId: null, productName: null, flag: 'unmatched'}),
  ],
};

async function render(response: PlanResponse | Error) {
  const api = {
    get: vi.fn(() =>
      response instanceof Error ? Promise.reject(response) : Promise.resolve(response)
    ),
  };
  const toast = {error: vi.fn(), success: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      {provide: PlanApi, useValue: api},
      {provide: ToastService, useValue: toast},
    ],
  });
  const fixture = TestBed.createComponent(PlanPage);
  fixture.detectChanges();
  await settle(fixture);
  return {fixture, api, toast, host: fixture.nativeElement as HTMLElement};
}

const squash = (value: string | null | undefined): string =>
  (value ?? '').replace(/\s+/g, ' ').trim();

/** The card of one list line (or the card with the given heading), found by the words it starts with. */
const section = (host: HTMLElement, heading: string): HTMLElement | undefined =>
  [...host.querySelectorAll<HTMLElement>('cmn-list-section')].find(s =>
    squash(s.textContent).startsWith(heading)
  );

const text = (host: HTMLElement, selector: string): string =>
  (host.querySelector(selector)?.textContent ?? '').replace(/\s+/g, ' ').trim();

describe('PlanPage', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('says on top that no one shop covers the list, which shop is closest and what blocks it', async () => {
    const {host} = await render(RESPONSE);

    const answer = text(host, '[data-testid="coverage"]');
    expect(answer).toContain('No one shop covers it');
    expect(answer).toContain('Closest: Lidl');
    expect(answer).toContain('2 lines block it');
    expect(answer).toContain('Lidl has a fresh price for 1 of 3 lines.');
    const blocking = squash(section(host, 'Not priced at Lidl')?.textContent);
    expect(blocking).toContain('Brennans sliced pan 800g Old prices');
    expect(blocking).toContain('Bananas Not matched');
  });

  it('shows the best shop for a line with its price, the day it was read, the deal and the struck old price', async () => {
    const {host} = await render(RESPONSE);

    const rows = [
      ...(section(host, 'Whole milk 2L')?.querySelectorAll('cmn-list-item-row') ?? []),
    ].map(r => squash(r.textContent));
    expect(rows).toHaveLength(2);
    expect(rows[0]).toContain('Lidl');
    expect(rows[0]).toContain('Best');
    expect(rows[0]).toContain('Seen 5 Nov · Lidl Plus price · €0.50/l');
    expect(rows[0]).toContain('€0.99');
    expect(rows[0]).toContain('was €1.49');
    expect(rows[1]).toContain('Tesco');
    expect(rows[1]).toContain('Out of stock');
    expect(rows[1]).toContain('Seen 6 Nov');
  });

  it('flags a stale line and an unmatched one with plain labels and no best shop', async () => {
    const {host} = await render(RESPONSE);

    const bread = squash(section(host, 'Brennans sliced pan 800g')?.textContent);
    expect(bread).toContain('Old prices');
    expect(bread).toContain('Every price is over 3 days old.');
    expect(bread).toContain('Seen 1 Nov');
    expect(bread).not.toContain('Best');

    const bananas = squash(section(host, 'Bananas')?.textContent);
    expect(bananas).toContain('Not matched');
    expect(bananas).toContain('Typed in by hand');
  });

  it('names the shop that covers the list and shows nothing blocking it', async () => {
    const covered: PlanResponse = {
      ...RESPONSE,
      items: [RESPONSE.items[0]],
      coverage: {
        singleStore: true,
        store: 'lidl',
        coveredBy: ['lidl'],
        closestStore: null,
        blockers: [],
      },
    };
    const {host} = await render(covered);

    const answer = text(host, '[data-testid="coverage"]');
    expect(answer).toContain('One shop covers it');
    expect(answer).toContain('Lidl has a fresh price for every line.');
    expect(host.textContent).not.toContain('Not priced at');
  });

  it('states the rule with the date of the prices and the limit the server uses', async () => {
    const {host} = await render(RESPONSE);

    const rule = text(host, '[data-testid="rule"]');
    expect(rule).toContain('Prices as of 6 Nov');
    expect(rule).toContain('lowest price per kilo, litre or piece');
    expect(rule).toContain('last 3 days');
  });

  it('says there is nothing to plan for an empty list', async () => {
    const empty: PlanResponse = {
      ...RESPONSE,
      items: [],
      coverage: {singleStore: false, store: null, coveredBy: [], closestStore: null, blockers: []},
    };
    const {host} = await render(empty);

    expect(host.querySelector('cmn-empty-state')?.textContent).toContain('Nothing to plan');
    expect(host.querySelector('[data-testid="coverage"]')).toBeNull();
  });

  it('shows a retry and a toast when the plan does not load, and loads again on retry', async () => {
    const {fixture, api, toast, host} = await render(new Error('offline'));

    expect(host.querySelector('cmn-empty-state')?.textContent).toContain('The plan did not load');
    expect(toast.error).toHaveBeenCalledOnce();

    api.get.mockResolvedValueOnce(RESPONSE);
    (host.querySelector('cmn-empty-state button') as HTMLButtonElement).click();
    await settle(fixture);

    expect(host.querySelector('[data-testid="coverage"]')).not.toBeNull();
  });

  it('reads the plan again when the page comes back into view', async () => {
    const {api} = await render(RESPONSE);
    expect(api.get).toHaveBeenCalledTimes(1);

    document.dispatchEvent(new Event('visibilitychange'));

    expect(api.get).toHaveBeenCalledTimes(2);
  });
});
