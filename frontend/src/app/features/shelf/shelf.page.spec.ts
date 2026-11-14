import {type ComponentFixture, TestBed} from '@angular/core/testing';
import {ToastService} from '@lifekit-hq/ui';

import {SessionStore} from '../../core/auth/session.store';
import {HabitsApi} from '../habits/habits.api';
import type {Habit} from '../habits/habits.model';
import {ListApi} from '../list/list.api';
import type {ListItem} from '../list/list.model';
import {PricesApi} from '../prices/prices.api';
import type {Price, Product, ProductStore, StoreId} from '../prices/prices.model';
import {ShelfPage} from './shelf.page';

const PRICE: Price = {
  observedOn: '2026-11-05',
  state: 'current',
  price: 2.15,
  wasPrice: 2.35,
  loyaltyPrice: null,
  loyaltyScheme: null,
  promoText: 'Clubcard Price',
  validFrom: null,
  validUntil: null,
  best: 2.15,
  inStock: true,
};

function entry(store: StoreId, linked: boolean, imageUrl: string | null = null): ProductStore {
  return {
    store,
    name: store[0]?.toUpperCase() + store.slice(1),
    readBy: 'pc',
    listing: linked
      ? {url: `https://example.test/${store}`, title: null, checkedAt: null, error: null, imageUrl}
      : null,
    price: linked ? PRICE : null,
    lastPaid: null,
  };
}

function product(
  id: string,
  name: string,
  shops: StoreId[],
  imageUrl: string | null = null
): Product {
  const all: StoreId[] = ['lidl', 'aldi', 'tesco', 'dunnes'];
  return {id, name, stores: all.map(shop => entry(shop, shops.includes(shop), imageUrl))};
}

const PRODUCTS = [
  product('milk', 'Whole milk 2L', ['lidl', 'tesco']),
  product('bread', 'Sliced pan', ['tesco']),
  product('butter', 'Butter', ['tesco']),
  product('eggs', 'Eggs', ['aldi']),
];

function line(id: string, productId: string, store: StoreId | null): ListItem {
  return {
    id,
    text: productId,
    checked: false,
    addedAt: '2026-11-03T10:00:00Z',
    addedBy: {id: 'u1', name: 'Anna'},
    checkedAt: null,
    checkedBy: null,
    productId,
    store,
  };
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, 0));
  fixture.detectChanges();
  await Promise.resolve();
  fixture.detectChanges();
}

function habit(productId: string, boughtIn: number, usual: boolean): Habit {
  return {productId, name: productId, boughtIn, lastBoughtAt: '2026-11-05T12:00:00Z', usual};
}

const HABITS: Habit[] = [habit('eggs', 5, true), habit('milk', 2, true), habit('butter', 1, false)];

async function render(
  items: ListItem[] = [],
  products: Product[] = PRODUCTS,
  loads = true,
  habits: Habit[] = HABITS
) {
  const listApi = {
    items: vi.fn().mockResolvedValue(items),
    addProduct: vi
      .fn()
      .mockImplementation((productId: string, store: StoreId | null) =>
        Promise.resolve(line(`new-${productId}-${store}`, productId, store))
      ),
  };
  const pricesApi = {
    all: loads
      ? vi.fn().mockResolvedValue({products, stores: []})
      : vi.fn().mockRejectedValue(new Error('offline')),
  };
  TestBed.configureTestingModule({
    providers: [
      {provide: ListApi, useValue: listApi},
      {provide: PricesApi, useValue: pricesApi},
      {provide: HabitsApi, useValue: {all: vi.fn().mockResolvedValue(habits)}},
      {provide: ToastService, useValue: {error: vi.fn(), success: vi.fn()}},
      {
        provide: SessionStore,
        useValue: {user: () => ({id: 'u1', displayName: 'Anna', role: 'owner'})},
      },
    ],
  });
  const fixture = TestBed.createComponent(ShelfPage);
  fixture.detectChanges();
  await settle(fixture);
  const host = fixture.nativeElement as HTMLElement;
  return {
    fixture,
    host,
    listApi,
    pricesApi,
    products: () =>
      Array.from(host.querySelectorAll('bk-shelf-card .line-clamp-2')).map(
        el => el.textContent?.trim().split(' ')[0]
      ),
    names: () =>
      Array.from(host.querySelectorAll('bk-shelf-card')).map(card =>
        (card.textContent ?? '').trim().replace(/\s+/g, ' ')
      ),
    choose: async (label: string) => {
      const cell = Array.from(host.querySelectorAll('[role="radio"], [role="tab"], button')).find(
        el => el.textContent?.trim() === label
      ) as HTMLElement;
      cell.click();
      await settle(fixture);
    },
    tap: async (index: number) => {
      (host.querySelectorAll('bk-shelf-card button')[index] as HTMLButtonElement).click();
      await settle(fixture);
    },
  };
}

describe('ShelfPage', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('puts the most bought products first and says how many trips bought each', async () => {
    const {names, products, choose} = await render();

    await choose('Most bought');

    expect(products()).toEqual(['Eggs', 'Whole', 'Butter', 'Sliced']);
    expect(names()[0]).toContain('Bought on 5 trips');
    expect(names()[2]).toContain('Bought on 1 trip');
    expect(names()[3]).not.toContain('Bought on');
  });

  it('keeps only the usually bought products, most bought first', async () => {
    const {products, choose} = await render();

    await choose('Usually bought');

    expect(products()).toEqual(['Eggs', 'Whole']);
  });

  it('combines with a shop: the usual products that shop has a page for', async () => {
    const {products, choose} = await render();

    await choose('Usually bought');
    await choose('Aldi');

    expect(products()).toEqual(['Eggs']);
  });

  it('says so when nothing is usual yet', async () => {
    const {host, choose} = await render([], PRODUCTS, true, []);

    await choose('Usually bought');

    expect(host.textContent).toContain('Nothing usual yet');
  });

  it('opens on All with every product as a card', async () => {
    const {names} = await render();

    expect(names()).toHaveLength(4);
  });

  it('shows only the products a shop has a page for when that shop is picked', async () => {
    const {names, choose} = await render();

    await choose('Tesco');

    const cards = names();
    expect(cards).toHaveLength(3);
    expect(cards.join(' ')).toContain('Whole milk 2L');
    expect(cards.join(' ')).not.toContain('Eggs');
  });

  it('draws a photo where one is known and the initial letter where it is not', async () => {
    const {host} = await render(
      [],
      [
        product('milk', 'Whole milk 2L', ['tesco'], 'https://img.test/milk.jpg'),
        product('bread', 'Sliced pan', ['tesco']),
      ]
    );

    const cards = Array.from(host.querySelectorAll('bk-shelf-card'));
    expect(cards[0]?.querySelector('img')?.getAttribute('src')).toBe('https://img.test/milk.jpg');
    expect(cards[1]?.querySelector('img')).toBeNull();
    expect(cards[1]?.textContent).toContain('S');
  });

  it('puts a tapped product on the list with the shop it was picked from, and a second tap adds nothing', async () => {
    const {fixture, listApi, choose, tap, host} = await render();
    await choose('Tesco');

    await tap(0);
    await tap(0);

    expect(listApi.addProduct).toHaveBeenCalledTimes(1);
    expect(listApi.addProduct).toHaveBeenCalledWith('milk', 'tesco');
    fixture.detectChanges();
    expect(host.querySelectorAll('bk-shelf-card')[0]?.textContent).toContain('On the list');
    expect(host.querySelectorAll('bk-shelf-card')[1]?.textContent).not.toContain('On the list');
  });

  it('adds three products from one shop in three taps, without any typing', async () => {
    const {listApi, choose, tap, host} = await render();
    await choose('Tesco');

    await tap(0);
    await tap(1);
    await tap(2);

    expect(listApi.addProduct.mock.calls).toEqual([
      ['milk', 'tesco'],
      ['bread', 'tesco'],
      ['butter', 'tesco'],
    ]);
    expect(host.querySelector('input')).toBeNull();
  });

  it('records no shop when the pick is made on All, and shows a product already on the list as picked', async () => {
    const {listApi, tap, host} = await render([line('x', 'bread', null)]);

    await tap(0);

    expect(listApi.addProduct).toHaveBeenCalledWith('milk', null);
    expect(host.querySelectorAll('bk-shelf-card')[1]?.textContent).toContain('On the list');
  });

  it('treats a line from another shop as a different pick', async () => {
    const {listApi, choose, tap} = await render([line('x', 'milk', 'lidl')]);
    await choose('Tesco');

    await tap(0);

    expect(listApi.addProduct).toHaveBeenCalledWith('milk', 'tesco');
  });

  it('says what is missing for a shop with no products, and for an empty shelf', async () => {
    const empty = await render([], []);
    expect(empty.host.textContent).toContain('The shelf is empty');
    TestBed.resetTestingModule();

    const noDunnes = await render();
    await noDunnes.choose('Dunnes');
    expect(noDunnes.host.textContent).toContain('Nothing at Dunnes yet');
  });

  it('shows an error with a retry when the products do not load', async () => {
    const {host, pricesApi} = await render([], [], false);

    expect(host.textContent).toContain('The shelf did not load');
    expect(pricesApi.all).toHaveBeenCalledTimes(1);
  });
});
