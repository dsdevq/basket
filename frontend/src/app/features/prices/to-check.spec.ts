import type {Habit} from '../habits/habits.model';
import type {Product, ProductStore} from './prices.model';
import {isUnlinked, toCheckRows, unlinkedText} from './to-check';

function shop(store: ProductStore['store'], candidates?: number, linked = false): ProductStore {
  return {
    store,
    name: store,
    readBy: 'nightly',
    listing: linked
      ? {url: 'https://x.ie/p', title: null, checkedAt: null, error: null, imageUrl: null}
      : null,
    price: null,
    lastPaid: null,
    candidates,
  };
}

function product(id: string, ...stores: ProductStore[]): Product {
  return {id, name: id, stores};
}

function habit(productId: string, boughtIn: number, lastBoughtAt: string): Habit {
  return {productId, name: productId, boughtIn, lastBoughtAt, usual: false};
}

describe('toCheckRows', () => {
  it('lists one row per shop with suggestions, skipping shops without', () => {
    const rows = toCheckRows(
      [product('a', shop('lidl'), shop('aldi', 1), shop('tesco', 2))],
      new Map()
    );

    expect(rows.map(row => row.entry.store)).toEqual(['aldi', 'tesco']);
  });

  it('puts the most-bought product first, then the latest trip, then never-bought in shelf order', () => {
    const products = [
      product('never1', shop('aldi', 1)),
      product('twice-old', shop('aldi', 1)),
      product('five', shop('aldi', 1)),
      product('twice-new', shop('aldi', 1)),
      product('never2', shop('aldi', 1)),
    ];
    const habits = new Map([
      ['five', habit('five', 5, '2026-11-01')],
      ['twice-old', habit('twice-old', 2, '2026-10-01')],
      ['twice-new', habit('twice-new', 2, '2026-11-10')],
    ]);

    expect(toCheckRows(products, habits).map(row => row.product.id)).toEqual([
      'five',
      'twice-new',
      'twice-old',
      'never1',
      'never2',
    ]);
  });
});

describe('isUnlinked', () => {
  it('is true only for a shop with no page and no suggestion', () => {
    expect(isUnlinked(shop('lidl'))).toBe(true);
    expect(isUnlinked(shop('lidl', 0))).toBe(true);
    expect(isUnlinked(shop('lidl', 1))).toBe(false);
    expect(isUnlinked(shop('lidl', undefined, true))).toBe(false);
  });
});

describe('unlinkedText', () => {
  it('counts the shops', () => {
    expect(unlinkedText(1)).toBe('1 shop with no page linked yet');
    expect(unlinkedText(3)).toBe('3 shops with no page linked yet');
  });
});
