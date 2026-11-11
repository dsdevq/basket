export type StoreId = 'tesco' | 'aldi' | 'dunnes';

export const STORES: readonly StoreId[] = ['aldi', 'dunnes', 'tesco'];

/**
 * The stores a run actually reads. Tesco is paused (its prices are not worth keeping warm for now): entries for it in
 * the page list are skipped with one log line per run. To read Tesco again, add 'tesco' back here.
 */
export const ENABLED_STORES: readonly StoreId[] = ['aldi', 'dunnes'];

/** One reading in the shape `POST /api/ingest/prices` takes. Absent fields are left out, never sent as null. */
export interface Reading {
  store: StoreId;
  url: string;
  title?: string;
  price?: number;
  wasPrice?: number;
  loyaltyPrice?: number;
  loyaltyScheme?: string;
  promoText?: string;
  validFrom?: string;
  validUntil?: string;
  inStock?: boolean;
  /** The product photo's address (https, at most 500 characters); the server stores the address, never the picture. */
  imageUrl?: string;
  /** The page's breadcrumb as a path, outermost first, " > " between ("Chilled Food > Milk"). */
  category?: string;
}

export interface WorkItem {
  store: StoreId;
  url: string;
}

/** What the browser got back for one page load. */
export interface Page {
  status: number;
  html: string;
}

export type ParseResult =
  | { kind: 'reading'; reading: Reading }
  /** The store refused or challenged this PC. Nothing more is asked of that store this run. */
  | { kind: 'blocked'; why: string }
  /** The page loaded but says nothing the parser understands. Nothing is posted for it. */
  | { kind: 'unreadable'; why: string };
