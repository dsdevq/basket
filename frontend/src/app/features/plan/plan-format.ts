import type {TagVariant, VerdictPill, VerdictStatus} from '@lifekit-hq/ui';

import {euro, shortDate, STORE_NAMES} from '../prices/price-format';
import type {StoreId} from '../prices/prices.model';
import type {
  PlanBlocker,
  PlanCoverage,
  PlanFlag,
  PlanItem,
  PlanPaid,
  PlanStorePrice,
  PlanStoreState,
} from './plan.model';

const enum Rank {
  Best,
  Priced,
  Unusable,
}

const UNIT_SUFFIX = {kg: '/kg', l: '/l', each: ' each'} as const;

function name(store: StoreId | null): string {
  return store ? STORE_NAMES[store] : '';
}

function plural(count: number, one: string, many: string): string {
  return `${count} ${count === 1 ? one : many}`;
}

/** The short label on a line that has no best shop, and the sentence that says why. */
export interface FlagText {
  readonly tag: string;
  readonly detail: string;
}

export function flagText(flag: PlanFlag, staleAfterDays: number): FlagText {
  switch (flag) {
    case 'unmatched':
      return {
        tag: 'Not matched',
        detail: 'Typed in by hand, so there is no product to look up. Pick it on the Shelf.',
      };
    case 'no_price':
      return {tag: 'No price', detail: 'No shop has a usable price for it yet.'};
    case 'stale':
      return {tag: 'Old prices', detail: `Every price is over ${staleAfterDays} days old.`};
    case 'out_of_stock':
      return {tag: 'Out of stock', detail: 'Every shop with a fresh price is out of stock.'};
    case 'not_comparable':
      return {
        tag: "Can't compare",
        detail:
          'Several shops have a fresh price, but they cannot be set side by side (no pack size, or kilos against pieces).',
      };
  }
}

/** The word for a shop whose reading is not a usable price today. */
function stateWord(state: Exclude<PlanStoreState, 'priced'>): string {
  switch (state) {
    case 'stale':
      return 'Old price';
    case 'out_of_stock':
      return 'Out of stock';
    case 'not_read':
      return 'Not read yet';
    case 'no_price':
      return 'No price listed';
    case 'upcoming':
      return 'Offer not started';
    case 'ended':
      return 'Offer ended';
  }
}

/** The word beside a shop's row when its reading is not a usable price today; null for a usable one. */
export function stateTag(state: PlanStoreState): string | null {
  return state === 'priced' ? null : stateWord(state);
}

/** What the tag on a row looks like: the best shop stands out, anything unusable is a warning. */
export function tagVariant(entry: PlanStorePrice): TagVariant {
  if (entry.best) {
    return 'success';
  }
  return entry.state === 'priced' ? 'neutral' : 'warning';
}

/** The row's tag text: "Best", the reason it is unusable, or nothing. */
export function rowTag(entry: PlanStorePrice): string | null {
  return entry.best ? 'Best' : stateTag(entry.state);
}

/** The price on the right of a shop's row: what one pack costs when the deal's terms are met; a dash when there is no reading to price. */
export function priceText(entry: PlanStorePrice): string {
  return entry.offer ? euro(entry.offer.eachPrice) : '—';
}

/** The old price to strike through, only when it is really higher than what is charged now. */
export function wasText(entry: PlanStorePrice): string | null {
  const now = entry.offer?.eachPrice ?? null;
  return entry.wasPrice !== null && now !== null && entry.wasPrice > now
    ? euro(entry.wasPrice)
    : null;
}

/** "Seen 5 Nov · 2 for €4 · needs Clubcard · €4.00/kg": the date first, so it is the last thing a narrow row can lose. */
export function rowDetail(entry: PlanStorePrice): string {
  const parts: string[] = [];
  if (entry.observedOn) {
    parts.push(`Seen ${shortDate(entry.observedOn)}`);
  }
  const offer = entry.offer;
  if (offer) {
    if (offer.isPromo) {
      parts.push(offer.deal);
    }
    if (offer.card && !offer.deal.includes(offer.card)) {
      parts.push(`needs ${offer.card}`);
    }
    if (offer.unitPrice !== null && offer.unit !== null) {
      parts.push(`${euro(offer.unitPrice)}${UNIT_SUFFIX[offer.unit]}`);
    }
  } else if (!entry.observedOn) {
    parts.push('Nothing read');
  }
  return parts.join(' · ');
}

export interface Verdict {
  readonly status: VerdictStatus;
  readonly pills: readonly VerdictPill[];
  readonly caveat: string;
}

/** The answer to "can I do this in one shop?", worded from the server's coverage. Only for a list with lines. */
export function verdict(coverage: PlanCoverage, lineCount: number): Verdict {
  if (coverage.singleStore) {
    const best = name(coverage.store);
    const others = coverage.coveredBy.length;
    return {
      status: {tone: 'success', label: 'One shop covers it'},
      pills: [
        {label: best, tone: 'success'},
        ...(others > 1 ? [{label: `${others} shops can`}] : []),
      ],
      caveat:
        others > 1
          ? `${coverage.coveredBy.map(name).join(', ')} all have a fresh price for every line. ${best} is cheapest for the most.`
          : `${best} has a fresh price for every line.`,
    };
  }
  const blocked = plural(coverage.blockers.length, 'line blocks it', 'lines block it');
  if (coverage.closestStore) {
    const priced = lineCount - coverage.blockers.length;
    return {
      status: {tone: 'warning', label: 'No one shop covers it'},
      pills: [
        {label: `Closest: ${name(coverage.closestStore)}`},
        {label: blocked, tone: 'warning'},
      ],
      caveat: `${name(coverage.closestStore)} has a fresh price for ${priced} of ${plural(lineCount, 'line', 'lines')}.`,
    };
  }
  return {
    status: {tone: 'warning', label: 'No shop prices it yet'},
    pills: [{label: blocked, tone: 'warning'}],
    caveat: 'None of the lines has a fresh price at any shop.',
  };
}

/** The short reason beside a line the closest shop cannot price: the line's own flag when no shop can, else that shop's state for it. */
export function blockerTag(
  blocker: PlanBlocker,
  item: PlanItem | undefined,
  staleAfterDays: number
): string {
  if (item?.flag === blocker.reason) {
    return flagText(item.flag, staleAfterDays).tag;
  }
  switch (blocker.reason) {
    case 'unmatched':
    case 'not_comparable':
      return flagText(blocker.reason, staleAfterDays).tag;
    case 'not_linked':
      return 'No page there';
    default:
      return stateWord(blocker.reason);
  }
}

/** The shops of a line in reading order: the best first, then the other usable prices, then the ones with nothing usable. */
export function orderedStores(item: PlanItem): PlanStorePrice[] {
  const rank = (entry: PlanStorePrice): Rank => {
    if (entry.best) {
      return Rank.Best;
    }
    return entry.state === 'priced' ? Rank.Priced : Rank.Unusable;
  };
  return [...item.stores].sort((a, b) => rank(a) - rank(b));
}

/** "Last paid €1.19 on 3 Nov": the wording of the Shelf and Prices pages, so one price reads the same on every screen. */
export function paidText(paid: PlanPaid): string {
  return `Last paid ${euro(paid.price)} on ${shortDate(paid.paidOn)}`;
}

/**
 * One shop's place in a line: its reading (null when the product has no page there), and what was last paid there (null when
 * nothing was bought there). A shop with only a paid price, such as a product that came in on a receipt, is still a row, so the
 * price paid is not lost for want of a page.
 */
export interface PlanRow {
  readonly store: StoreId;
  readonly storeName: string;
  readonly entry: PlanStorePrice | null;
  readonly paid: PlanPaid | null;
}

/** The rows of a line: the shops with a reading in reading order, then the shops it was only ever bought at. */
export function planRows(item: PlanItem): PlanRow[] {
  const paidAt = (store: StoreId): PlanPaid | null =>
    item.lastPaid.find(paid => paid.store === store) ?? null;
  const read: PlanRow[] = orderedStores(item).map(entry => ({
    store: entry.store,
    storeName: entry.storeName,
    entry,
    paid: paidAt(entry.store),
  }));
  const onlyPaid: PlanRow[] = item.lastPaid
    .filter(paid => !item.stores.some(entry => entry.store === paid.store))
    .map(paid => ({store: paid.store, storeName: paid.storeName, entry: null, paid}));
  return [...read, ...onlyPaid];
}

/** The lines under a shop's name, one fact group per line: the reading (date, deal, card, unit price), then what was last paid. */
export function rowLines(row: PlanRow): string {
  const lines = row.entry ? [rowDetail(row.entry)] : ['No page linked yet'];
  if (row.paid) {
    lines.push(paidText(row.paid));
  }
  return lines.join('\n');
}

/** The word beside a row: the entry's own tag, or that the shop has no page when only a price paid is known. */
export function planRowTag(row: PlanRow): string | null {
  return row.entry ? rowTag(row.entry) : 'No page there';
}

export function planRowVariant(row: PlanRow): TagVariant {
  return row.entry ? tagVariant(row.entry) : 'warning';
}

/** A row with nothing usable today is dimmed, except the best shop. */
export function planRowDimmed(row: PlanRow): boolean {
  return row.entry ? !row.entry.best && row.entry.state !== 'priced' : true;
}

export function planRowPrice(row: PlanRow): string {
  return row.entry ? priceText(row.entry) : '—';
}

export function planRowWas(row: PlanRow): string | null {
  return row.entry ? wasText(row.entry) : null;
}
