import type {StoreId} from './prices.model';

/** One dated reading: `effective` is what a pack costs when the deal's terms are met, null when the shop listed no price. */
export interface HistoryPoint {
  readonly observedOn: string;
  readonly effective: number | null;
  readonly price: number | null;
  readonly wasPrice: number | null;
  readonly loyaltyPrice: number | null;
  readonly loyaltyScheme: string | null;
  readonly promoText: string | null;
  readonly inStock: boolean;
}

export interface HistoryExtreme {
  readonly price: number;
  readonly on: string;
}

/** The last `days` days of one product in one shop. */
export interface PriceHistory {
  readonly productId: string;
  readonly store: StoreId;
  readonly storeName: string;
  readonly days: number;
  readonly from: string;
  readonly to: string;
  readonly points: readonly HistoryPoint[];
  readonly lowest: HistoryExtreme | null;
  readonly highest: HistoryExtreme | null;
  readonly current: number | null;
  /** The latest price is the cheapest of the window and was dearer before. */
  readonly lowestIn90Days: boolean;
}
