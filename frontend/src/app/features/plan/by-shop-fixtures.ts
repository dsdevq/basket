import type {ByShopLine, ByShopResponse, ByShopStore} from './by-shop.model';

/** Test helpers: a shop's total and a bought line, each with every field set and overridden per case. */
export function shop(over: Partial<ByShopStore> = {}): ByShopStore {
  return {
    store: 'lidl',
    storeName: 'Lidl',
    priced: 0,
    total: 0,
    complete: false,
    cheapest: false,
    oldestSeenOn: null,
    promoLines: 0,
    packsDiffer: 0,
    cards: [],
    missing: [],
    ...over,
  };
}

export function bought(over: Partial<ByShopLine> = {}): ByShopLine {
  return {
    itemId: 'a',
    text: 'Whole milk 2L',
    price: 1.29,
    deal: 'Shelf price',
    isPromo: false,
    card: null,
    packSize: '2 L',
    unitPrice: 0.65,
    unit: 'l',
    observedOn: '2026-11-05',
    ...over,
  };
}

export function basket(over: Partial<ByShopResponse> = {}): ByShopResponse {
  return {
    asOf: '2026-11-06',
    staleAfterDays: 3,
    lineCount: 2,
    stores: [],
    split: {total: 0, complete: false, versusCheapest: null, groups: [], unplaced: []},
    ...over,
  };
}
