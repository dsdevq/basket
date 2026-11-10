import type {PromoOffer} from '../promotions/promotions.model';
import type {PlanItem, PlanPaid, PlanStorePrice} from './plan.model';

/** Test helpers: a promotions offer, a shop's entry for a line and a line, each with every field set and overridden per case. */
export function offer(over: Partial<PromoOffer> = {}): PromoOffer {
  return {
    store: 'lidl',
    storeName: 'Lidl',
    kind: 'regular',
    isPromo: false,
    deal: 'Shelf price',
    promoText: null,
    card: null,
    buyQuantity: 1,
    payTotal: 1.29,
    eachPrice: 1.29,
    shelfPrice: 1.29,
    referencePrice: null,
    savingPercent: null,
    packSize: '2 L',
    unit: 'l',
    unitPrice: 0.65,
    referenceUnitPrice: null,
    observedOn: '2026-11-05',
    validUntil: null,
    rank: 1,
    flags: [],
    ...over,
  };
}

export function entry(over: Partial<PlanStorePrice> = {}): PlanStorePrice {
  return {
    store: 'lidl',
    storeName: 'Lidl',
    state: 'priced',
    observedOn: '2026-11-05',
    price: 1.29,
    wasPrice: null,
    promoText: null,
    offer: offer(),
    best: false,
    ...over,
  };
}

export function line(over: Partial<PlanItem> = {}): PlanItem {
  return {
    id: 'i1',
    text: 'Whole milk 2L',
    productId: 'p1',
    productName: 'Whole milk 2L',
    pickedStore: null,
    flag: null,
    bestStore: null,
    stores: [],
    lastPaid: [],
    ...over,
  };
}

export function paid(over: Partial<PlanPaid> = {}): PlanPaid {
  return {store: 'lidl', storeName: 'Lidl', price: 1.19, paidOn: '2026-11-03', ...over};
}
