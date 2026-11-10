import type {PromoGap, PromoOffer, PromoProduct} from './promotions.model';

/** Test helpers: an offer with every field set, overridden per case. */
export function offer(over: Partial<PromoOffer> = {}): PromoOffer {
  return {
    store: 'lidl',
    storeName: 'Lidl',
    kind: 'multi_buy',
    isPromo: true,
    deal: '2 for €4',
    promoText: '2 for €4',
    card: null,
    buyQuantity: 2,
    payTotal: 4,
    eachPrice: 2,
    shelfPrice: 2.19,
    referencePrice: 2.19,
    savingPercent: 9,
    packSize: '500 g',
    unit: 'kg',
    unitPrice: 4,
    referenceUnitPrice: 4.38,
    observedOn: '2026-11-06',
    validUntil: '2026-11-11',
    rank: 1,
    flags: [],
    ...over,
  };
}

export function product(over: Partial<PromoProduct> = {}): PromoProduct {
  return {id: 'p1', name: 'Chicken fillets', unit: 'kg', ranked: 2, offers: [], gaps: [], ...over};
}

export function gap(over: Partial<PromoGap> = {}): PromoGap {
  return {store: 'tesco', storeName: 'Tesco', reason: 'not_read', date: null, ...over};
}
