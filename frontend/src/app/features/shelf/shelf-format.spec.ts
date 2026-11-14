import type {Habit} from '../habits/habits.model';
import type {Price, Product, ProductStore, StoreId} from '../prices/prices.model';
import {cardPrice, pickStore, SHELF_OPTIONS, SHELF_VIEWS, shelfTiles} from './shelf-format';

function price(over: Partial<Price> = {}): Price {
  return {
    observedOn: '2026-11-05',
    state: 'current',
    price: 2.15,
    wasPrice: null,
    loyaltyPrice: null,
    loyaltyScheme: null,
    promoText: null,
    validFrom: null,
    validUntil: null,
    best: 2.15,
    inStock: true,
    ...over,
  };
}

function entry(
  store: StoreId,
  listing: {imageUrl?: string; price?: Price} | null,
  name = store[0]?.toUpperCase() + store.slice(1)
): ProductStore {
  return {
    store,
    name,
    readBy: store === 'lidl' ? 'nightly' : 'pc',
    listing: listing && {
      url: `https://example.test/${store}`,
      title: null,
      checkedAt: null,
      error: null,
      imageUrl: listing.imageUrl ?? null,
    },
    price: listing?.price ?? null,
    lastPaid: null,
  };
}

function product(id: string, stores: ProductStore[]): Product {
  const all: StoreId[] = ['lidl', 'aldi', 'tesco', 'dunnes'];
  return {
    id,
    name: `Product ${id}`,
    stores: all.map(shop => stores.find(s => s.store === shop) ?? entry(shop, null)),
  };
}

describe('shelf tiles', () => {
  const milk = product('milk', [
    entry('lidl', {price: price({best: 1.29, price: 1.29}), imageUrl: 'https://img.test/lidl.jpg'}),
    entry('tesco', {price: price({best: 1.89}), imageUrl: 'https://img.test/tesco.jpg'}),
  ]);
  const bread = product('bread', [entry('tesco', {price: price({best: 1.85})})]);
  const eggs = product('eggs', [entry('aldi', {})]);
  const nothing = product('nothing', []);
  const all = [milk, bread, eggs, nothing];

  it('offers All and the four shops, in that order', () => {
    expect(SHELF_OPTIONS.map(option => option.value)).toEqual([
      'all',
      'lidl',
      'aldi',
      'tesco',
      'dunnes',
    ]);
    expect(SHELF_OPTIONS.map(option => option.label)).toEqual([
      'All',
      'Lidl',
      'Aldi',
      'Tesco',
      'Dunnes',
    ]);
  });

  it("keeps only the products a shop has a page for, drawn with that shop's reading and photo", () => {
    const tiles = shelfTiles(all, 'tesco');

    expect(tiles.map(tile => tile.product.id)).toEqual(['milk', 'bread']);
    expect(tiles[0]?.entry?.store).toBe('tesco');
    expect(tiles[0]?.imageUrl).toBe('https://img.test/tesco.jpg');
    expect(tiles[1]?.imageUrl).toBeNull();
  });

  it('keeps every product on All and shows the shop with the lowest current price', () => {
    const tiles = shelfTiles(all, 'all');

    expect(tiles.map(tile => tile.product.id)).toEqual(['milk', 'bread', 'eggs', 'nothing']);
    expect(tiles[0]?.entry?.store).toBe('lidl');
    expect(tiles[0]?.imageUrl).toBe('https://img.test/lidl.jpg');
    expect(tiles[2]?.entry?.store).toBe('aldi');
    expect(tiles[3]?.entry).toBeNull();
  });

  it('keeps a product a receipt shows was bought at the shop, though no page is linked, and draws what was paid', () => {
    const butter = product('butter', [
      {...entry('lidl', null), lastPaid: {state: 'known', price: 2.19, paidOn: '2026-11-03'}},
    ]);
    const unbought = product('unbought', [
      {...entry('lidl', null), lastPaid: {state: 'none', price: null, paidOn: null}},
    ]);

    const lidl = shelfTiles([butter, unbought], 'lidl');
    expect(lidl.map(tile => tile.product.id)).toEqual(['butter']);
    const bought = lidl[0]?.entry;
    if (!bought) {
      throw new Error('the Lidl tile has no entry');
    }
    expect(cardPrice(bought, 'lidl')).toMatchObject({
      main: '€2.19',
      seen: 'Last paid 3 Nov',
      was: null,
      promo: null,
    });
    expect(shelfTiles([butter], 'aldi')).toEqual([]);
    const everything = shelfTiles([butter], 'all')[0]?.entry;
    if (!everything) {
      throw new Error('the All tile has no entry');
    }
    expect(everything.store).toBe('lidl');
    expect(cardPrice(everything, 'all').seen).toBe('Lidl · last paid 3 Nov');
  });

  it('records the selected shop on a pick, and none on All', () => {
    expect(pickStore('tesco')).toBe('tesco');
    expect(pickStore('all')).toBeNull();
  });

  describe('by what trips bought', () => {
    const habit = (productId: string, boughtIn: number, usual: boolean): Habit => ({
      productId,
      name: productId,
      boughtIn,
      lastBoughtAt: '2026-11-05T12:00:00Z',
      usual,
    });
    const habits = new Map([
      ['bread', habit('bread', 3, true)],
      ['eggs', habit('eggs', 3, true)],
      ['milk', habit('milk', 1, false)],
    ]);

    it('offers the plain Shelf, the most bought and the usually bought, in that order', () => {
      expect(SHELF_VIEWS.map(view => view.value)).toEqual(['all', 'most', 'usual']);
    });

    it('leaves the Shelf order alone, with the count on each tile, when nothing is sorted', () => {
      const tiles = shelfTiles(all, 'all', 'all', habits);

      expect(tiles.map(tile => tile.product.id)).toEqual(['milk', 'bread', 'eggs', 'nothing']);
      expect(tiles.map(tile => tile.boughtIn)).toEqual([1, 3, 3, 0]);
    });

    it('sorts the most bought first, ties and never-bought keeping the Shelf order', () => {
      const tiles = shelfTiles(all, 'all', 'most', habits);

      expect(tiles.map(tile => tile.product.id)).toEqual(['bread', 'eggs', 'milk', 'nothing']);
    });

    it('keeps only the usually bought', () => {
      expect(shelfTiles(all, 'all', 'usual', habits).map(tile => tile.product.id)).toEqual([
        'bread',
        'eggs',
      ]);
      expect(shelfTiles(all, 'all', 'usual').map(tile => tile.product.id)).toEqual([]);
    });
  });
});

describe('card price', () => {
  it('shows the card price, the struck old price, the promo, how long it lasts and when it was read', () => {
    const card = cardPrice(
      entry('tesco', {
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
      'tesco'
    );

    expect(card).toEqual({
      main: '€1.89',
      was: '€2.35',
      promo: 'Clubcard Price',
      loyalty: 'with Clubcard',
      until: 'until 15 Nov',
      seen: 'Seen 5 Nov',
      outOfStock: false,
    });
  });

  it('names the shop on All and flags out of stock', () => {
    const card = cardPrice(
      entry('aldi', {
        price: price({state: 'unavailable', price: null, best: null, inStock: false}),
      }),
      'all'
    );

    expect(card.main).toBe('no price');
    expect(card.seen).toBe('Aldi · seen 5 Nov');
    expect(card.outOfStock).toBe(true);
  });

  it('keeps an offer that has not started or has ended to a plain word, with no deal lines', () => {
    const upcoming = cardPrice(
      entry('aldi', {
        price: price({state: 'upcoming', validFrom: '2026-11-12', wasPrice: 3, promoText: 'Deal'}),
      }),
      'aldi'
    );
    const ended = cardPrice(
      entry('aldi', {price: price({state: 'ended', wasPrice: 3, promoText: 'Deal'})}),
      'aldi'
    );

    expect(upcoming).toMatchObject({main: 'from 12 Nov', was: null, promo: null, until: null});
    expect(ended).toMatchObject({main: 'ended', was: null, promo: null});
  });

  it('says no price has been read for a page that has none yet', () => {
    const card = cardPrice(entry('tesco', {}), 'tesco');

    expect(card).toMatchObject({
      main: '—',
      seen: 'No price read yet',
      was: null,
      outOfStock: false,
    });
  });
});
