import type {Paid, Price, Product, ProductStore, StoreId} from '../prices/prices.model';
import {compareRows, dublinToday, isStale, STALE_AFTER_DAYS} from './compare-format';

const TODAY = '2026-11-14';

function price(over: Partial<Price> = {}): Price {
  return {
    observedOn: '2026-11-13',
    state: 'current',
    price: 1.29,
    wasPrice: null,
    loyaltyPrice: null,
    loyaltyScheme: null,
    promoText: null,
    validFrom: null,
    validUntil: null,
    best: 1.29,
    inStock: true,
    ...over,
  };
}

function entry(store: StoreId, over: Partial<ProductStore> = {}): ProductStore {
  return {
    store,
    name: store,
    readBy: 'pc',
    listing: {
      url: `https://example.test/${store}`,
      title: `${store} whole milk 2 L`,
      checkedAt: null,
      error: null,
      imageUrl: `https://img.example.test/${store}.jpg`,
    },
    price: price(),
    lastPaid: null,
    ...over,
  };
}

const unlinked = (store: StoreId, lastPaid: Paid | null = null): ProductStore =>
  entry(store, {listing: null, price: null, lastPaid});

function milk(stores: ProductStore[]): Product {
  return {id: 'milk', name: 'Whole milk 2L', stores};
}

function rowFor(store: StoreId, e: ProductStore) {
  const rows = compareRows(
    milk([
      e,
      ...(['lidl', 'aldi', 'tesco', 'dunnes'] as StoreId[])
        .filter(s => s !== store)
        .map(s => unlinked(s)),
    ]),
    TODAY
  );
  return rows.find(row => row.store === store);
}

describe('compareRows', () => {
  it('answers for all four shops in the app order, even when the product has an entry for none of them', () => {
    const rows = compareRows(milk([]), TODAY);

    expect(rows.map(row => row.store)).toEqual(['lidl', 'aldi', 'tesco', 'dunnes']);
    expect(rows.every(row => row.state === 'not_linked' && !row.choosable)).toBe(true);
  });

  it('shows a fresh price with the shop photo, the shop title, the day it was read and no flag', () => {
    const row = rowFor('aldi', entry('aldi'));

    expect(row).toMatchObject({
      state: 'priced',
      price: '€1.29',
      tag: null,
      was: null,
      imageUrl: 'https://img.example.test/aldi.jpg',
      choosable: true,
    });
    expect(row?.lines).toBe('aldi whole milk 2 L\nSeen 13 Nov');
  });

  it('strikes the old price through and words the deal for a fresh promo', () => {
    const row = rowFor(
      'tesco',
      entry('tesco', {
        price: price({
          best: 1.0,
          price: 1.0,
          wasPrice: 1.29,
          promoText: 'Clubcard Price',
          loyaltyPrice: 1.0,
          loyaltyScheme: 'Clubcard',
        }),
      })
    );

    expect(row?.price).toBe('€1.00');
    expect(row?.was).toBe('€1.29');
    expect(row?.lines).toContain('Clubcard Price');
  });

  it('counts a reading from the third day back and flags the fourth as an old price, still dated and never replaced', () => {
    const third = rowFor('lidl', entry('lidl', {price: price({observedOn: '2026-11-11'})}));
    const fourth = rowFor('lidl', entry('lidl', {price: price({observedOn: '2026-11-10'})}));

    expect(third?.state).toBe('priced');
    expect(fourth).toMatchObject({
      state: 'stale',
      tag: 'Old price',
      tagVariant: 'warning',
      was: null,
    });
    expect(fourth?.price).toBe('€1.29');
    expect(fourth?.lines).toContain('Seen 10 Nov');
  });

  it('flags a shop that is out of stock, an offer not started or ended, and a listing with no price', () => {
    expect(rowFor('lidl', entry('lidl', {price: price({inStock: false})}))).toMatchObject({
      state: 'out_of_stock',
      tag: 'Out of stock',
    });
    expect(
      rowFor('lidl', entry('lidl', {price: price({state: 'upcoming', validFrom: '2026-11-20'})}))
    ).toMatchObject({state: 'upcoming', tag: 'Offer not started', price: 'from 20 Nov'});
    expect(
      rowFor('lidl', entry('lidl', {price: price({state: 'ended', validUntil: '2026-11-12'})}))
    ).toMatchObject({state: 'ended', tag: 'Offer ended', price: 'ended'});
    expect(rowFor('lidl', entry('lidl', {price: price({state: 'unavailable'})}))).toMatchObject({
      state: 'no_price',
      tag: 'No price listed',
    });
  });

  it('says a linked page has not been read yet instead of showing a number', () => {
    const row = rowFor('dunnes', entry('dunnes', {price: null}));

    expect(row).toMatchObject({
      state: 'not_read',
      tag: 'Not read yet',
      price: '—',
      choosable: true,
    });
    expect(row?.lines).toContain('Waiting for the next PC read');
  });

  it('shows a shop with no page as not there, dimmed and not choosable, with its suggested pages', () => {
    const row = rowFor('dunnes', {...unlinked('dunnes'), candidates: 2});

    expect(row).toMatchObject({
      state: 'not_linked',
      tag: 'No page there',
      price: '',
      choosable: false,
      imageUrl: null,
    });
    expect(row?.lines).toBe('No page linked yet\n2 suggested pages to check');
  });

  it('gives a shop known only from a receipt the last paid price, not a current one, and lets it be chosen', () => {
    const row = rowFor(
      'lidl',
      unlinked('lidl', {state: 'known', price: 1.19, paidOn: '2026-11-03'})
    );

    expect(row).toMatchObject({
      state: 'paid_only',
      tag: 'No price read',
      price: '—',
      choosable: true,
    });
    expect(row?.lines).toBe('Last paid €1.19 on 3 Nov');
  });
});

describe('isStale', () => {
  it('uses the shared window and treats an unreadable day as too old', () => {
    expect(STALE_AFTER_DAYS).toBe(3);
    expect(isStale('2026-11-11', TODAY)).toBe(false);
    expect(isStale('2026-11-10', TODAY)).toBe(true);
    expect(isStale('', TODAY)).toBe(true);
  });
});

describe('dublinToday', () => {
  it('is the Dublin day, not the UTC one', () => {
    // 23:30 UTC on 14 Nov is already 15 Nov only in summer time; in November Dublin is on UTC.
    expect(dublinToday(new Date('2026-11-14T23:30:00Z'))).toBe('2026-11-14');
    // 23:30 UTC on 20 Jun is 00:30 on 21 Jun in Dublin (UTC+1).
    expect(dublinToday(new Date('2026-06-20T23:30:00Z'))).toBe('2026-06-21');
  });
});
