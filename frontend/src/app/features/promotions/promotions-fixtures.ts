import type {
  PromoGap,
  PromoOffer,
  PromoProduct,
  UsualOffer,
  UsualProduct,
} from './promotions.model';

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
    lowestIn90Days: false,
    ...over,
  };
}

export function product(over: Partial<PromoProduct> = {}): PromoProduct {
  return {id: 'p1', name: 'Chicken fillets', unit: 'kg', ranked: 2, offers: [], gaps: [], ...over};
}

export function gap(over: Partial<PromoGap> = {}): PromoGap {
  return {store: 'tesco', storeName: 'Tesco', reason: 'not_read', date: null, ...over};
}

export function usualOffer(over: Partial<UsualOffer> = {}): UsualOffer {
  return {
    catalogProductId: 'c1',
    title: 'Milbona whole milk',
    imageUrl: null,
    url: 'https://www.lidl.ie/p/p1',
    basis: 'name_size',
    offer: offer({
      unit: null,
      unitPrice: null,
      rank: null,
      buyQuantity: 1,
      payTotal: 1.49,
      eachPrice: 1.49,
    }),
    ...over,
  };
}

export function usualProduct(over: Partial<UsualProduct> = {}): UsualProduct {
  return {id: 'u1', name: 'Whole milk 2L', boughtIn: 3, offers: [usualOffer()], ...over};
}
