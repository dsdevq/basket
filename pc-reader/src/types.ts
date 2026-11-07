export type StoreId = 'tesco' | 'aldi';

export const STORES: readonly StoreId[] = ['aldi', 'tesco'];

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
