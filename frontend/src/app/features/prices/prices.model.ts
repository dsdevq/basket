export type StoreId = 'lidl' | 'aldi' | 'tesco' | 'dunnes' | 'centra';

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
  /** True once the shop's sweeps no longer list the catalog product this page is. */
  readonly discontinued?: boolean;
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
  /** How many pages the matcher suggests for this shop that nobody has confirmed or rejected yet. */
  readonly candidates?: number;
}

/**
 * Why the matcher suggested a page: the same barcode, the same name and size, the same kind of product and size once each shop's own
 * label is left out, or a close name with no size to check.
 */
export type MatchBasis = 'barcode' | 'name_size' | 'generic' | 'name';

/** A page in a shop that may be the product's equivalent there, waiting for a person's yes or no. */
export interface Candidate {
  readonly id: string;
  readonly store: StoreId;
  readonly url: string;
  readonly title: string | null;
  readonly barcode: string | null;
  readonly basis: MatchBasis;
  readonly score: number;
}

export interface Product {
  readonly id: string;
  readonly name: string;
  readonly stores: readonly ProductStore[];
}

export type RunOutcome = 'ok' | 'partial' | 'blocked' | 'failed';

/** One sweep of a shop's whole range: `seen` is what it listed, `live` the products the catalog holds after it. */
export interface SweepRun {
  readonly finishedAt: string;
  readonly outcome: RunOutcome;
  readonly seen: number;
  readonly live: number;
  readonly message: string | null;
}

/**
 * How a shop's catalog is doing. `never`: no sweep is recorded (nothing is guessed). `stale`: no complete sweep within `staleAfterDays`
 * (2 for a shop the server sweeps, 9 for one the PC reads). `shrunk`: the latest sweep listed under 90% of the last complete one.
 * `discontinuedOnShelf` counts the shelf's pages the shop no longer lists.
 */
export interface CatalogHealth {
  readonly state: 'never' | 'fresh' | 'stale';
  readonly staleAfterDays: number;
  readonly lastSweep: SweepRun | null;
  readonly lastOkAt: string | null;
  readonly live: number;
  readonly discontinued: number;
  readonly discontinuedOnShelf: number;
  readonly shrunk: {readonly seen: number; readonly previous: number} | null;
}

export interface StoreStatus {
  readonly store: StoreId;
  readonly name: string;
  readonly readBy: ReadBy;
  readonly catalog: CatalogHealth;
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
