import {type ComponentFixture, TestBed} from '@angular/core/testing';
import {ToastService} from '@lifekit-hq/ui';

import {ByShopApi} from './by-shop.api';
import type {ByShopResponse} from './by-shop.model';
import {ByShopPage} from './by-shop.page';
import {basket, bought, shop} from './by-shop-fixtures';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, 0));
  fixture.detectChanges();
  await Promise.resolve();
  fixture.detectChanges();
}

const EGGS = {itemId: 'e', text: 'Free-range eggs, a dozen', reason: 'not_linked'} as const;
const BREAD = {itemId: 'b', text: 'Brennans sliced pan 800g', reason: 'stale'} as const;

const RESPONSE: ByShopResponse = basket({
  lineCount: 4,
  stores: [
    shop({
      store: 'lidl',
      storeName: 'Lidl',
      priced: 2,
      total: 2.8,
      oldestSeenOn: '2026-11-05',
      cards: ['Lidl Plus'],
      missing: [EGGS, BREAD],
    }),
    shop({
      store: 'aldi',
      storeName: 'Aldi',
      priced: 3,
      total: 5.1,
      oldestSeenOn: '2026-11-06',
      missing: [EGGS],
    }),
    shop({store: 'tesco', storeName: 'Tesco', missing: [EGGS, BREAD]}),
    shop({store: 'dunnes', storeName: 'Dunnes'}),
  ],
  split: {
    total: 3.9,
    complete: false,
    versusCheapest: null,
    groups: [
      {
        store: 'lidl',
        storeName: 'Lidl',
        total: 0.99,
        lines: [
          bought({
            price: 0.99,
            deal: 'Lidl Plus price',
            isPromo: true,
            card: 'Lidl Plus',
            unitPrice: 0.5,
          }),
        ],
      },
      {
        store: 'aldi',
        storeName: 'Aldi',
        total: 2.91,
        lines: [
          bought({
            itemId: 'k',
            text: 'Kerrygold butter 227g',
            price: 2.91,
            observedOn: '2026-11-06',
            unit: 'kg',
            unitPrice: 12.82,
          }),
        ],
      },
    ],
    unplaced: [{itemId: 'e', text: 'Free-range eggs, a dozen', reason: 'no_price'}],
  },
});

async function render(response: ByShopResponse | Error) {
  const api = {
    get: vi.fn(() =>
      response instanceof Error ? Promise.reject(response) : Promise.resolve(response)
    ),
  };
  const toast = {error: vi.fn(), success: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      {provide: ByShopApi, useValue: api},
      {provide: ToastService, useValue: toast},
    ],
  });
  const fixture = TestBed.createComponent(ByShopPage);
  fixture.detectChanges();
  await settle(fixture);
  return {fixture, api, toast, host: fixture.nativeElement as HTMLElement};
}

const squash = (value: string | null | undefined): string =>
  (value ?? '').replace(/\s+/g, ' ').trim();

const section = (host: HTMLElement, heading: string): HTMLElement | undefined =>
  [...host.querySelectorAll<HTMLElement>('cmn-list-section')].find(s =>
    squash(s.textContent).startsWith(heading)
  );

const rows = (card: HTMLElement | undefined): string[] =>
  [...(card?.querySelectorAll('cmn-list-item-row') ?? [])].map(r => squash(r.textContent));

describe('ByShopPage', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('says on top that no shop prices the whole list and which is closest', async () => {
    const {host} = await render(RESPONSE);

    const answer = squash(host.querySelector('[data-testid="verdict"]')?.textContent);
    expect(answer).toContain('No shop prices the whole list');
    expect(answer).toContain('Closest: Aldi');
    expect(answer).toContain('Aldi has a fresh price for 3 of 4 lines, €5.10 so far.');
  });

  it('lists every shop with its total, the lines it prices and the day of its oldest price, fullest first', async () => {
    const {host} = await render(RESPONSE);

    const list = rows(section(host, 'The list at each shop'));
    expect(list).toHaveLength(4);
    expect(list[0]).toContain('Aldi');
    expect(list[0]).toContain('3 of 4 lines · oldest price 6 Nov');
    expect(list[0]).toContain('1 missing');
    expect(list[0]).toContain('€5.10');
    expect(list[1]).toContain('Lidl');
    expect(list[1]).toContain('Lidl Plus price included');
    expect(list[1]).toContain('2 missing');
    expect(list[2]).toContain('Tesco');
    expect(list[2]).toContain('0 of 4 lines');
    expect(list[2]).toContain('Nothing priced');
    expect(list[2]).toContain('—');
  });

  it('lists the lines a shop lacks with the reason, never a guessed price', async () => {
    const {host} = await render(RESPONSE);

    const lidl = squash(section(host, 'Not priced at Lidl')?.textContent);
    expect(lidl).toContain('Free-range eggs, a dozen No page there');
    expect(lidl).toContain('Brennans sliced pan 800g Old price');
    expect(squash(section(host, 'Not priced at Aldi')?.textContent)).toContain('No page there');
    expect(section(host, 'Not priced at Tesco')).toBeUndefined();
  });

  it('shows the split shop by shop with dated prices and the line it cannot place', async () => {
    const {host} = await render(RESPONSE);

    const lidl = rows(section(host, 'Split: buy at Lidl · €0.99'));
    expect(lidl[0]).toContain('Seen 5 Nov · Lidl Plus price · €0.50/l');
    expect(lidl[0]).toContain('€0.99');
    expect(rows(section(host, 'Split: buy at Aldi · €2.91'))[0]).toContain('Seen 6 Nov');
    const unplaced = squash(section(host, 'Split: no best shop')?.textContent);
    expect(unplaced).toContain('Free-range eggs, a dozen');
    expect(unplaced).toContain('No price');
  });

  it('marks the cheapest complete shop', async () => {
    const complete = basket({
      lineCount: 1,
      stores: [
        shop({store: 'lidl', storeName: 'Lidl', priced: 1, total: 1.2, complete: true}),
        shop({
          store: 'aldi',
          storeName: 'Aldi',
          priced: 1,
          total: 1.1,
          complete: true,
          cheapest: true,
        }),
      ],
    });
    const {host} = await render(complete);

    expect(squash(host.querySelector('[data-testid="verdict"]')?.textContent)).toContain(
      'Aldi is cheapest'
    );
    const list = rows(section(host, 'The list at each shop'));
    expect(list[0]).toContain('Aldi');
    expect(list[0]).toContain('Cheapest');
    expect(host.textContent).not.toContain('Not priced at');
  });

  it('states the rule with the date of the prices and the limit the server uses', async () => {
    const {host} = await render(RESPONSE);

    const rule = squash(host.querySelector('[data-testid="rule"]')?.textContent);
    expect(rule).toContain('Prices as of 6 Nov');
    expect(rule).toContain('last 3 days');
    expect(rule).toContain('lowest price per kilo, litre or piece');
  });

  it('says there is nothing to total for an empty list', async () => {
    const {host} = await render(basket({lineCount: 0}));

    expect(host.querySelector('cmn-empty-state')?.textContent).toContain('Nothing to total');
    expect(host.querySelector('[data-testid="verdict"]')).toBeNull();
  });

  it('shows a retry and a toast when it does not load, and loads again on retry', async () => {
    const {fixture, api, toast, host} = await render(new Error('offline'));

    expect(host.querySelector('cmn-empty-state')?.textContent).toContain('did not load');
    expect(toast.error).toHaveBeenCalledOnce();

    api.get.mockResolvedValueOnce(RESPONSE);
    (host.querySelector('cmn-empty-state button') as HTMLButtonElement).click();
    await settle(fixture);

    expect(host.querySelector('[data-testid="verdict"]')).not.toBeNull();
  });

  it('reads again when the page comes back into view', async () => {
    const {api} = await render(RESPONSE);
    expect(api.get).toHaveBeenCalledTimes(1);

    document.dispatchEvent(new Event('visibilitychange'));

    expect(api.get).toHaveBeenCalledTimes(2);
  });
});
