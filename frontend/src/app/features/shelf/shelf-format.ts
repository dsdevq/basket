import type {Habit} from '../habits/habits.model';
import {euro, headline, shortDate, STORE_NAMES} from '../prices/price-format';
import type {Product, ProductStore, StoreId} from '../prices/prices.model';

/** What the Shelf is showing: every product, or the ones one shop sells. */
export type ShelfFilter = 'all' | StoreId;

export const SHELF_OPTIONS: readonly {readonly value: ShelfFilter; readonly label: string}[] = [
  {value: 'all', label: 'All'},
  ...(Object.entries(STORE_NAMES) as [StoreId, string][]).map(([value, label]) => ({value, label})),
];

/**
 * How the Shelf is ordered and narrowed by what closed trips say: every product as listed, the ones bought most first, or
 * only the ones usually bought.
 */
export type ShelfView = 'all' | 'most' | 'usual';

export const SHELF_VIEWS: readonly {readonly value: ShelfView; readonly label: string}[] = [
  {value: 'all', label: 'All products'},
  {value: 'most', label: 'Most bought'},
  {value: 'usual', label: 'Usually bought'},
];

/** One card: a product, the shop's entry whose reading it shows, and the photo to draw. */
export interface ShelfTile {
  readonly product: Product;
  readonly entry: ProductStore | null;
  readonly imageUrl: string | null;
  /** How many closed trips bought it; 0 when none did. */
  readonly boughtIn: number;
}

/** The shop a pick is recorded against: the selected one, or none on "All". */
export function pickStore(filter: ShelfFilter): StoreId | null {
  return filter === 'all' ? null : filter;
}

/** A shop with no page linked for a product can still be where it was bought: a receipt gives its price and day. */
function boughtThere(entry: ProductStore): boolean {
  return entry.lastPaid?.state === 'known';
}

function cheapest(product: Product): ProductStore | null {
  const read = product.stores.filter(entry => entry.listing && entry.price);
  const current = read.filter(
    entry => entry.price?.state === 'current' && entry.price.best !== null
  );
  const lowest = [...current].sort((a, b) => (a.price?.best ?? 0) - (b.price?.best ?? 0))[0];
  return (
    lowest ??
    read[0] ??
    product.stores.find(entry => entry.listing) ??
    product.stores.find(boughtThere) ??
    null
  );
}

function shopTiles(
  products: readonly Product[],
  filter: ShelfFilter
): Omit<ShelfTile, 'boughtIn'>[] {
  if (filter === 'all') {
    return products.map(product => {
      const entry = cheapest(product);
      const imageUrl =
        entry?.listing?.imageUrl ??
        product.stores.find(candidate => candidate.listing?.imageUrl)?.listing?.imageUrl ??
        null;
      return {product, entry, imageUrl};
    });
  }
  return products.flatMap(product => {
    const entry = product.stores.find(
      candidate => candidate.store === filter && (candidate.listing || boughtThere(candidate))
    );
    return entry ? [{product, entry, imageUrl: entry.listing?.imageUrl ?? null}] : [];
  });
}

/**
 * The cards for a filter. A shop's filter keeps the products that shop has a page for, or that a receipt shows were bought there
 * (the Lidl Plus receipts seed the Lidl shelf), and shows that shop's reading and photo. "All" keeps every product and shows the shop with the lowest current price, or the first with any reading.
 */
export function shelfTiles(
  products: readonly Product[],
  filter: ShelfFilter,
  view: ShelfView = 'all',
  habits: ReadonlyMap<string, Habit> = new Map()
): ShelfTile[] {
  const tiles = shopTiles(products, filter).map(tile => ({
    ...tile,
    boughtIn: habits.get(tile.product.id)?.boughtIn ?? 0,
  }));
  if (view === 'all') {
    return tiles;
  }
  const kept = view === 'usual' ? tiles.filter(tile => habits.get(tile.product.id)?.usual) : tiles;
  // A stable sort: products bought equally often (or never) keep the Shelf's own order.
  return [...kept].sort((a, b) => b.boughtIn - a.boughtIn);
}

/** The price part of a card, every line already worded. Null fields are not drawn. */
export interface CardPrice {
  /** The big figure: the best price today, or what the shop says instead ("from 12 Nov", "ended", "no price"). */
  readonly main: string;
  /** The old price, struck through, only while the offer is on. */
  readonly was: string | null;
  readonly promo: string | null;
  readonly loyalty: string | null;
  readonly until: string | null;
  /** When it was read, and at which shop on "All". */
  readonly seen: string;
  readonly outOfStock: boolean;
}

export function cardPrice(entry: ProductStore, filter: ShelfFilter): CardPrice {
  const price = entry.price;
  const seen = (what: string): string =>
    filter === 'all'
      ? `${entry.name} · ${what}`
      : `${what[0]?.toUpperCase() ?? ''}${what.slice(1)}`;
  if (!price && !entry.listing && entry.lastPaid?.state === 'known') {
    // Seeded from a receipt: no page to read, so the card shows what was paid and when.
    return {
      main: euro(entry.lastPaid.price),
      was: null,
      promo: null,
      loyalty: null,
      until: null,
      seen: seen(`last paid ${shortDate(entry.lastPaid.paidOn)}`),
      outOfStock: false,
    };
  }
  if (!price) {
    return {
      main: headline(entry),
      was: null,
      promo: null,
      loyalty: null,
      until: null,
      seen: seen('no price read yet'),
      outOfStock: false,
    };
  }
  const current = price.state === 'current';
  return {
    main: headline(entry),
    was: current && price.wasPrice !== null ? euro(price.wasPrice) : null,
    promo: current ? price.promoText : null,
    // The big figure already is the card price when it is the lowest, so the line only says whose card it is.
    loyalty:
      current && price.loyaltyPrice !== null && price.loyaltyScheme
        ? price.loyaltyPrice === price.best
          ? `with ${price.loyaltyScheme}`
          : `${price.loyaltyScheme} ${euro(price.loyaltyPrice)}`
        : null,
    until: current && price.validUntil ? `until ${shortDate(price.validUntil)}` : null,
    seen: seen(`seen ${shortDate(price.observedOn)}`),
    outOfStock: !price.inStock,
  };
}
