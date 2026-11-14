import type {TagVariant} from '@lifekit-hq/ui';

import {detailLines, euro, headline, STORE_NAMES, suggestionLine} from '../prices/price-format';
import type {Product, ProductStore, StoreId} from '../prices/prices.model';

/**
 * A reading older than this many days is not a price to choose a shop by. The same number as the Plan and Promotions
 * (`PromotionBuilder.StaleAfterDays`): a reading from the 3rd day back still counts, the 4th is stale.
 */
export const STALE_AFTER_DAYS = 3;

const MS_PER_DAY = 86_400_000;

/** The shops in the order the app always lists them. */
const SHOP_ORDER: readonly StoreId[] = ['lidl', 'aldi', 'tesco', 'dunnes'];

/**
 * What a shop can say about the product today. `priced` is a usable price; every other state is a reason the row carries a
 * flag instead of a number to trust.
 */
export type CompareState =
  | 'priced'
  | 'stale'
  | 'out_of_stock'
  | 'upcoming'
  | 'ended'
  | 'no_price'
  | 'not_read'
  | 'paid_only'
  | 'not_linked';

/** The states drawn faded: a figure that is not today's price, or no page at all. */
const DIMMED_STATES: ReadonlySet<CompareState> = new Set([
  'stale',
  'out_of_stock',
  'ended',
  'not_linked',
]);

/** One shop's row in the compare sheet, every line already worded. */
export interface CompareRow {
  readonly store: StoreId;
  readonly name: string;
  readonly state: CompareState;
  /** The shop's photo of its equivalent; null when it has none (the initial letter stands in). */
  readonly imageUrl: string | null;
  /** The big figure on the right: the price, or what stands in its place ("from 12 Nov", "—"); empty for a shop with no page. */
  readonly price: string;
  /** The old price, struck through, only for a usable price with a promo. */
  readonly was: string | null;
  /** The flag beside the shop when the reading is not a usable price; null for a usable one. */
  readonly tag: string | null;
  readonly tagVariant: TagVariant;
  /** The lines under the shop name: the shop's title for the product, the deal, the day it was read, what was last paid. */
  readonly lines: string;
  /** False for a shop that has no page for the product and never sold it to the household: nothing to choose there. */
  readonly choosable: boolean;
  /** True for a row whose figure is not today's price to trust, or that has no page: drawn faded. */
  readonly dimmed: boolean;
}

/** The word beside a shop whose reading is not a usable price today. */
function tagFor(state: Exclude<CompareState, 'priced'>): string {
  switch (state) {
    case 'stale':
      return 'Old price';
    case 'out_of_stock':
      return 'Out of stock';
    case 'upcoming':
      return 'Offer not started';
    case 'ended':
      return 'Offer ended';
    case 'no_price':
      return 'No price listed';
    case 'not_read':
      return 'Not read yet';
    case 'paid_only':
      return 'No price read';
    case 'not_linked':
      return 'No page there';
  }
}

/** Today in Dublin as a plain day ("2026-11-14"), the form every reading date arrives in. */
export function dublinToday(now: Date = new Date()): string {
  return new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Europe/Dublin',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).format(now);
}

/** Whole days from one plain day to another; null when either is not a plain day. */
function daysBetween(from: string, to: string): number | null {
  const start = Date.parse(`${from}T00:00:00Z`);
  const end = Date.parse(`${to}T00:00:00Z`);
  return Number.isNaN(start) || Number.isNaN(end) ? null : Math.round((end - start) / MS_PER_DAY);
}

/** True when a reading from `observedOn` is too old to rely on on `today`. A date that cannot be read counts as too old. */
export function isStale(observedOn: string, today: string): boolean {
  const age = daysBetween(observedOn, today);
  return age === null || age > STALE_AFTER_DAYS;
}

function stateOf(entry: ProductStore, today: string): CompareState {
  const {listing, price} = entry;
  if (!listing) {
    return entry.lastPaid?.state === 'known' ? 'paid_only' : 'not_linked';
  }
  if (!price) {
    return 'not_read';
  }
  switch (price.state) {
    case 'upcoming':
      return 'upcoming';
    case 'ended':
      return 'ended';
    case 'unavailable':
      return 'no_price';
    default:
      if (isStale(price.observedOn, today)) {
        return 'stale';
      }
      return price.inStock ? 'priced' : 'out_of_stock';
  }
}

function variantOf(state: CompareState): TagVariant {
  return state === 'priced' ? 'neutral' : 'warning';
}

function titleLine(entry: ProductStore): string[] {
  const title = entry.listing?.title?.trim();
  return title ? [title] : [];
}

/** A reading that is not a usable price keeps its date but sheds the deal: no card price, old price or promo. */
function withoutDeal(entry: ProductStore): ProductStore {
  const price = entry.price;
  return price
    ? {
        ...entry,
        price: {
          ...price,
          loyaltyPrice: null,
          loyaltyScheme: null,
          wasPrice: null,
          promoText: null,
          validUntil: price.state === 'ended' ? price.validUntil : null,
        },
      }
    : entry;
}

function linesOf(entry: ProductStore, state: CompareState): string[] {
  // What was paid is only worth a line when a receipt says so; "not connected yet" is Settings' business, not a shop's.
  const known = entry.lastPaid?.state === 'known' ? entry.lastPaid : null;
  const shown = {...entry, lastPaid: known};
  switch (state) {
    case 'not_linked':
      return ['No page linked yet', ...suggestionLine(entry)];
    case 'paid_only':
      // No page to read: the only fact is a receipt, worded as the Shelf and Prices word it.
      return [
        ...detailLines(shown).filter(line => line.startsWith('Last paid')),
        ...suggestionLine(entry),
      ];
    case 'stale':
    case 'out_of_stock':
    case 'ended':
      return [...titleLine(entry), ...detailLines(withoutDeal(shown))];
    default:
      return [...titleLine(entry), ...detailLines(shown)];
  }
}

function rowOf(entry: ProductStore, today: string): CompareRow {
  const state = stateOf(entry, today);
  const price = entry.price;
  return {
    store: entry.store,
    name: STORE_NAMES[entry.store],
    state,
    imageUrl: entry.listing?.imageUrl ?? null,
    price: state === 'paid_only' ? '—' : headline(entry),
    was: state === 'priced' && price && price.wasPrice !== null ? euro(price.wasPrice) : null,
    tag: state === 'priced' ? null : tagFor(state),
    tagVariant: variantOf(state),
    lines: linesOf(entry, state).join('\n'),
    choosable: state !== 'not_linked',
    dimmed: DIMMED_STATES.has(state),
  };
}

/**
 * The product's equivalent at each of the four shops, in the app's fixed shop order. A shop the product has no entry for still
 * gets its row ("No page there"), so the sheet always answers for all four. Nothing is ranked or called cheapest here: the
 * product endpoint carries no unit price, and a 2 L next to a 1 L is not a comparison; the Plan owns that call.
 */
export function compareRows(product: Product, today: string): CompareRow[] {
  return SHOP_ORDER.map(store => {
    const entry = product.stores.find(candidate => candidate.store === store);
    return rowOf(
      entry ?? {
        store,
        name: STORE_NAMES[store],
        readBy: 'pc',
        listing: null,
        price: null,
        lastPaid: null,
      },
      today
    );
  });
}
