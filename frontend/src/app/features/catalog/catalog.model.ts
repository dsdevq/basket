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
