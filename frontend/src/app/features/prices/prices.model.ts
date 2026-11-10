export type StoreId = 'lidl' | 'aldi' | 'tesco' | 'dunnes';

/** `current` is usable today; `upcoming` starts later; `ended` has run out since it was read; `unavailable` has no price. */
export type PriceState = 'current' | 'upcoming' | 'ended' | 'unavailable';

export interface Price {
  readonly observedOn: string;
  readonly state: PriceState;
  readonly price: number | null;
  readonly wasPrice: number | null;
  readonly loyaltyPrice: number | null;
  readonly loyaltyScheme: string | null;
  readonly promoText: string | null;
  readonly validFrom: string | null;
  readonly validUntil: string | null;
  /** The lowest price a card holder pays today, when `current`. */
  readonly best: number | null;
  /** False when the shop listed the product as out of stock on the day it was read. */
  readonly inStock: boolean;
}

export interface Listing {
  readonly url: string;
  readonly title: string | null;
  readonly checkedAt: string | null;
  readonly error: string | null;
  /** The shop's photo of the product (https), once a reader has sent one. */
  readonly imageUrl: string | null;
}

/**
 * The last price really paid, from Lidl Plus receipts: `known`, `none` (connected, but no receipt has this product) or
 * `not_connected` (the Lidl Plus token has not been pasted yet).
 */
export interface Paid {
  readonly state: 'not_connected' | 'none' | 'known';
  readonly price: number | null;
  readonly paidOn: string | null;
}

/** `nightly`: the server reads it every night. `pc`: read by a browser on the owner's PC when it is on. */
export type ReadBy = 'nightly' | 'pc';

export interface ProductStore {
  readonly store: StoreId;
  readonly name: string;
  readonly readBy: ReadBy;
  readonly listing: Listing | null;
  readonly price: Price | null;
  readonly lastPaid: Paid | null;
}

export interface Product {
  readonly id: string;
  readonly name: string;
  readonly stores: readonly ProductStore[];
}

export type RunOutcome = 'ok' | 'partial' | 'blocked' | 'failed';

export interface StoreStatus {
  readonly store: StoreId;
  readonly name: string;
  readonly readBy: ReadBy;
  readonly lastRun: {
    readonly finishedAt: string;
    readonly outcome: RunOutcome;
    readonly listings: number;
    readonly read: number;
    readonly message: string | null;
  } | null;
}

export interface PricesResponse {
  readonly products: readonly Product[];
  readonly stores: readonly StoreStatus[];
}
