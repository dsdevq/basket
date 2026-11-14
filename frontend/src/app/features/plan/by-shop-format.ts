import type {VerdictPill, VerdictStatus} from '@lifekit-hq/ui';

import {euro, shortDate} from '../prices/price-format';
import type {ByShopLine, ByShopResponse, ByShopStore} from './by-shop.model';

const UNIT_SUFFIX = {kg: '/kg', l: '/l', each: ' each'} as const;

function plural(count: number, one: string, many: string): string {
  return `${count} ${count === 1 ? one : many}`;
}

function joined(parts: readonly string[]): string {
  return parts.join(' · ');
}

/** The answer on top, worded from the server's totals: the cheapest shop that prices every line, else the closest one. */
export interface ByShopVerdict {
  readonly status: VerdictStatus;
  readonly pills: readonly VerdictPill[];
  readonly caveat: string;
}

/** The split set against the cheapest single shop, in words; empty when the split cannot be set against a whole basket. */
function splitSentence(basket: ByShopResponse): string {
  const {split} = basket;
  if (split.versusCheapest === null) {
    return '';
  }
  const splitCost = `Splitting the list across shops costs ${euro(split.total)}`;
  if (split.versusCheapest < 0) {
    return ` ${splitCost}, ${euro(-split.versusCheapest)} less.`;
  }
  return split.versusCheapest > 0
    ? ` ${splitCost}, ${euro(split.versusCheapest)} more, because it buys the better value pack, not the cheaper one.`
    : ` ${splitCost}, the same.`;
}

export function closestStore(basket: ByShopResponse): ByShopStore | null {
  const best = [...basket.stores].sort((a, b) => b.priced - a.priced)[0];
  return best && best.priced > 0 ? best : null;
}

export function verdict(basket: ByShopResponse): ByShopVerdict {
  const cheapest = basket.stores.find(store => store.cheapest);
  if (cheapest) {
    const saving = basket.split.versusCheapest;
    return {
      status: {tone: 'success', label: `${cheapest.storeName} is cheapest`},
      pills: [
        {label: euro(cheapest.total), tone: 'success'},
        ...(saving !== null && saving < 0 ? [{label: `Split saves ${euro(-saving)}`}] : []),
      ],
      caveat: `${cheapest.storeName} has a fresh price for every line, ${euro(cheapest.total)} in all.${splitSentence(basket)}`,
    };
  }
  const closest = closestStore(basket);
  if (!closest) {
    return {
      status: {tone: 'warning', label: 'No shop prices it yet'},
      pills: [{label: plural(basket.lineCount, 'line', 'lines'), tone: 'warning'}],
      caveat: 'None of the lines has a fresh price at any shop.',
    };
  }
  const placed = basket.lineCount - basket.split.unplaced.length;
  return {
    status: {tone: 'warning', label: 'No shop prices the whole list'},
    pills: [
      {label: `Closest: ${closest.storeName}`},
      {label: `${plural(closest.missing.length, 'line', 'lines')} missing`, tone: 'warning'},
    ],
    caveat:
      `${closest.storeName} has a fresh price for ${closest.priced} of ${plural(basket.lineCount, 'line', 'lines')}, ${euro(closest.total)} so far.` +
      (basket.split.groups.length
        ? ` Splitting the list across shops places ${placed} of ${basket.lineCount} for ${euro(basket.split.total)}.`
        : ''),
  };
}

/** Under a shop's name: how many lines its total covers and how old its oldest price is, then what in it needs care. */
export function storeDetail(store: ByShopStore, lineCount: number): string {
  const parts = [`${store.priced} of ${plural(lineCount, 'line', 'lines')}`];
  if (store.oldestSeenOn) {
    parts.push(`oldest price ${shortDate(store.oldestSeenOn)}`);
  }
  if (store.cards.length) {
    parts.push(`${store.cards.join(' and ')} price${store.cards.length === 1 ? '' : 's'} included`);
  }
  if (store.promoLines && !store.cards.length) {
    parts.push(`${plural(store.promoLines, 'offer', 'offers')} included`);
  }
  if (store.packsDiffer) {
    parts.push(`packs differ on ${store.packsDiffer}`);
  }
  return joined(parts);
}

/** "Seen 5 Nov · 2 for €4 · needs Clubcard · €4.00/kg": the date first, so it is the last thing a narrow row can lose. */
export function lineDetail(line: ByShopLine): string {
  const parts = [`Seen ${shortDate(line.observedOn)}`];
  if (line.isPromo) {
    parts.push(line.deal);
  }
  if (line.card && !line.deal.includes(line.card)) {
    parts.push(`needs ${line.card}`);
  }
  if (line.unitPrice !== null && line.unit !== null) {
    parts.push(`${euro(line.unitPrice)}${UNIT_SUFFIX[line.unit]}`);
  }
  return joined(parts);
}

/** A run of one shop's lines that sit in the same section of the shop. */
export interface WalkSection {
  readonly section: string;
  readonly lines: readonly ByShopLine[];
}

/**
 * A shop's lines cut into the sections you walk through, each under its own header (the sectioned list of AnyList and the
 * Tesco app). The API already sends the lines in walk order, so a section is a run of neighbours; nothing is re-sorted here.
 */
export function walkSections(lines: readonly ByShopLine[]): WalkSection[] {
  const sections: {section: string; lines: ByShopLine[]}[] = [];
  for (const line of lines) {
    const last = sections.at(-1);
    if (last?.section === line.section) {
      last.lines.push(line);
    } else {
      sections.push({section: line.section, lines: [line]});
    }
  }
  return sections;
}

/** The stores in reading order: the cheapest complete shop, the other complete ones by total, then the incomplete by lines priced. */
export function orderedShops(basket: ByShopResponse): ByShopStore[] {
  return [...basket.stores].sort((a, b) => {
    if (a.complete !== b.complete) {
      return a.complete ? -1 : 1;
    }
    return a.complete ? a.total - b.total : b.priced - a.priced;
  });
}
