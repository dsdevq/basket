import type {Price, StoreId} from '../prices/prices.model';

/** One catalog product as a result tile shows it. `price` is the latest reading, dated by `price.observedOn`. */
export interface CatalogItem {
  readonly id: string;
  readonly store: StoreId;
  readonly storeName: string;
  readonly name: string;
  readonly brand: string | null;
  readonly sizeText: string | null;
  /** The shop's own comparison price, euro per `unitPriceBasis` ("1 KG"). */
  readonly unitPrice: number | null;
  readonly unitPriceBasis: string | null;
  readonly imageUrl: string | null;
  readonly url: string;
  readonly price: Price | null;
  /** The last time a sweep listed the product. */
  readonly lastSeenAt: string;
}

export interface CatalogSearchResponse {
  readonly query: string;
  readonly store: StoreId | null;
  readonly items: readonly CatalogItem[];
}

/** One catalog product in full. `discontinuedAt` is set once the shops stopped listing it. */
export interface CatalogProduct extends Omit<CatalogItem, 'lastSeenAt'> {
  readonly sizeAmount: number | null;
  readonly sizeUnit: 'g' | 'ml' | 'each' | null;
  readonly sizePacks: number;
  readonly barcode: string | null;
  readonly category: string | null;
  readonly firstSeenAt: string;
  readonly lastSeenAt: string;
  readonly discontinuedAt: string | null;
}

/** Why an offer's unit price is missing or not to be compared. */
export type ComparisonFlag = 'stale' | 'out_of_stock' | 'no_price' | 'no_size' | 'other_unit';

/**
 * How an offer compares per unit. `unitPrice` is euro per `unit` (kg, l or each), worked out from the parsed size, so shops are in one
 * unit whatever their labels say. `difference` is this unit price minus the source's: negative is cheaper. Set only when both are comparable.
 */
export interface CatalogComparison {
  readonly unit: 'kg' | 'l' | 'each' | null;
  readonly pack: string | null;
  readonly unitPrice: number | null;
  readonly difference: number | null;
  readonly flags: readonly ComparisonFlag[];
}

/** One catalog product in the equivalents answer. `basis` and `score` are null for the source itself. */
export interface CatalogEquivalentItem {
  readonly item: CatalogItem;
  readonly basis: 'barcode' | 'name_size' | 'name' | 'generic' | null;
  readonly score: number | null;
  readonly comparison: CatalogComparison;
}

/** The best (at most three) equivalents in one shop; empty when it has none. */
export interface StoreEquivalents {
  readonly store: StoreId;
  readonly storeName: string;
  readonly items: readonly CatalogEquivalentItem[];
}

/**
 * The product's equivalents in the other three shops. `reason` is `no_size_or_barcode` when the product states neither, so nothing was
 * looked up (the lists are empty, which is not "no equivalent exists").
 */
export interface CatalogEquivalents {
  readonly product: CatalogEquivalentItem;
  readonly reason: 'no_size_or_barcode' | null;
  readonly stores: readonly StoreEquivalents[];
}
