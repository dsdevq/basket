import type {StoreId} from '../prices/prices.model';

/** How a shop is selling it: buy several, a price cut, the card price, or just the shelf price. */
export type PromoKind = 'multi_buy' | 'percentage' | 'loyalty' | 'coupon' | 'regular';

/** What a unit price is per: `kg`, `l` or `each` (one piece). */
export type PromoUnit = 'kg' | 'l' | 'each';

/** Why an offer was shown but not ranked (or how its size was found). */
export type PromoFlag = 'stale' | 'out_of_stock' | 'no_size' | 'size_from_name' | 'other_unit';

export interface PromoOffer {
  readonly store: StoreId;
  readonly storeName: string;
  readonly kind: PromoKind;
  /** False for an ordinary shelf price, kept so a promo is compared with it. */
  readonly isPromo: boolean;
  /** "2 for €4", "20% off", "Lidl Plus price", "Shelf price". */
  readonly deal: string;
  readonly promoText: string | null;
  /** The loyalty card the price needs, or null. */
  readonly card: string | null;
  readonly buyQuantity: number;
  readonly payTotal: number;
  /** What one pack costs when the deal's terms are met. */
  readonly eachPrice: number;
  readonly shelfPrice: number | null;
  readonly referencePrice: number | null;
  readonly savingPercent: number | null;
  readonly packSize: string | null;
  readonly unit: PromoUnit | null;
  readonly unitPrice: number | null;
  readonly referenceUnitPrice: number | null;
  readonly observedOn: string;
  readonly validUntil: string | null;
  /** Place among the comparable shops, 1 is cheapest per unit; null when left out of the ranking. */
  readonly rank: number | null;
  readonly flags: readonly PromoFlag[];
  /** True when this promo is the cheapest this shop's page has been in 90 days, and was dearer before. */
  readonly lowestIn90Days: boolean;
}

/** A linked shop with nothing usable today. */
export interface PromoGap {
  readonly store: StoreId;
  readonly storeName: string;
  readonly reason: 'not_read' | 'no_price' | 'upcoming' | 'ended';
  readonly date: string | null;
}

export interface PromoProduct {
  readonly id: string;
  readonly name: string;
  readonly unit: PromoUnit | null;
  /** How many shops were comparable. */
  readonly ranked: number;
  readonly offers: readonly PromoOffer[];
  readonly gaps: readonly PromoGap[];
}

export interface PromotionsResponse {
  readonly asOf: string;
  readonly staleAfterDays: number;
  readonly products: readonly PromoProduct[];
}

/** One shop's offer on a catalog product that is the usual product. `basis` is `linked` or why the matcher proposed it. */
export interface UsualOffer {
  readonly catalogProductId: string;
  /** The shop's own name for it. */
  readonly title: string;
  readonly imageUrl: string | null;
  readonly url: string;
  readonly basis: string;
  readonly offer: PromoOffer;
}

/** A product the household usually buys, with the offers on it today. */
export interface UsualProduct {
  readonly id: string;
  readonly name: string;
  readonly boughtIn: number;
  readonly offers: readonly UsualOffer[];
}

export interface UsualOffersResponse {
  readonly asOf: string;
  readonly staleAfterDays: number;
  readonly products: readonly UsualProduct[];
}
