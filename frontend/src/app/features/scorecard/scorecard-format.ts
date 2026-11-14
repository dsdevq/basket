import type {VerdictPill, VerdictStatus} from '@lifekit-hq/ui';

import {euro} from '../prices/price-format';
import type {ScorecardMonth, ScorecardResponse, ScorecardStore} from './scorecard.model';

const CENTS = 100;
const PREVIOUS = 2;
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

function plural(count: number, one: string, many: string): string {
  return `${count} ${count === 1 ? one : many}`;
}

/** "2026-11" -> "Nov 2026". */
export function monthLabel(month: string): string {
  const match = /^(\d{4})-(\d{2})$/.exec(month);
  return match ? `${MONTHS[Number(match[2]) - 1] ?? ''} ${match[1]}`.trim() : month;
}

/** The shops with a win, most wins first (the server's fixed order breaks a tie), then the rest. */
export function orderedStores(card: ScorecardResponse): readonly ScorecardStore[] {
  return card.stores
    .map((store, order) => ({store, order}))
    .sort((a, b) => b.store.wins - a.store.wins || a.order - b.order)
    .map(entry => entry.store);
}

/** Newest month first, so the latest sits under the heading. */
export function newestFirst(card: ScorecardResponse): readonly ScorecardMonth[] {
  return [...card.trend].reverse();
}

export function storeDetail(store: ScorecardStore): string {
  return store.wins
    ? `${plural(store.wins, 'win', 'wins')} · ${store.share}% of compared lines`
    : 'No wins';
}

export function monthDetail(month: ScorecardMonth): string {
  return month.trips
    ? `${plural(month.trips, 'trip', 'trips')} · ${plural(month.compared, 'line', 'lines')} compared`
    : 'No trips';
}

/** How the latest month with compared lines saved against the one before it; null when there is no earlier month to set it against. */
export function trendSentence(card: ScorecardResponse): string {
  const months = card.trend.filter(month => month.compared > 0);
  const latest = months.at(-1);
  const before = months.at(-PREVIOUS);
  if (!latest || !before) {
    return '';
  }
  const change = Math.round((latest.saved - before.saved) * CENTS) / CENTS;
  const name = monthLabel(latest.month);
  if (change === 0) {
    return `${name} saved the same as ${monthLabel(before.month)}.`;
  }
  return `${name} saved ${euro(Math.abs(change))} ${change > 0 ? 'more' : 'less'} than ${monthLabel(before.month)}.`;
}

export interface ScorecardVerdict {
  readonly status: VerdictStatus;
  readonly pills: readonly VerdictPill[];
  readonly caveat: string;
}

/** The answer on top, worded from the server's counts: who wins most, what the winners saved, and how many lines could not be compared. */
export function verdict(card: ScorecardResponse): ScorecardVerdict {
  const top = card.stores.find(store => store.store === card.topStore);
  const skipped = card.notCompared
    ? ` ${plural(card.notCompared, 'line was', 'lines were')} not compared: typed, or without two fresh prices.`
    : '';
  if (!top) {
    return {
      status: {tone: 'warning', label: 'Nothing to score yet'},
      pills: [{label: plural(card.trips, 'trip', 'trips'), tone: 'warning'}],
      caveat: `No line from your closed trips had fresh prices at two shops.${skipped}`,
    };
  }
  return {
    status: {tone: 'success', label: `${top.storeName} wins most`},
    pills: [
      {label: `${euro(card.saved)} saved`, tone: 'success'},
      {label: plural(card.compared, 'line', 'lines')},
    ],
    caveat:
      `${top.storeName} was cheapest on ${top.wins} of ${card.compared} compared lines. Buying each at its cheapest shop instead of the dearest saved ${euro(card.saved)} over ${plural(card.trips, 'trip', 'trips')}.${skipped} ${trendSentence(card)}`.trim(),
  };
}
