import {euro, shortDate} from '../prices/price-format';
import type {StoreId} from '../prices/prices.model';
import type {PromoFlag, PromoGap, PromoOffer, PromoProduct, PromoUnit} from './promotions.model';

/** "per kg", "per litre", "per piece": the words the rule uses. */
export const UNIT_WORDS: Record<PromoUnit, string> = {kg: 'kg', l: 'litre', each: 'piece'};

const UNIT_SUFFIX: Record<PromoUnit, string> = {kg: '/kg', l: '/l', each: ' each'};

/** "€4.92/kg", "€0.30 each": what a shelf label prints. Empty when the pack size is not known. */
export function unitPriceText(offer: PromoOffer): string {
  return offer.unitPrice === null || offer.unit === null
    ? ''
    : `${euro(offer.unitPrice)}${UNIT_SUFFIX[offer.unit]}`;
}

/** The right-hand figure of a row: the unit price, or why there is none. */
export function headline(offer: PromoOffer): string {
  return unitPriceText(offer) || `${euro(offer.eachPrice)} a pack`;
}

function flagText(flag: PromoFlag): string {
  switch (flag) {
    case 'stale':
      return 'Old reading, not ranked';
    case 'out_of_stock':
      return 'Out of stock, not ranked';
    case 'no_size':
      return 'No pack size found, not ranked';
    case 'size_from_name':
      return 'Size taken from the product name';
    case 'other_unit':
      return 'Sold by a different unit, not compared';
  }
}

/** Line one: what the deal is and what it asks of you (the card, how many to buy). */
function dealLine(offer: PromoOffer): string {
  const parts = [offer.deal];
  if (offer.card) {
    parts.push(`needs ${offer.card}`);
  }
  if (offer.savingPercent !== null) {
    parts.push(`saves ${offer.savingPercent}%`);
  }
  return parts.join(' · ');
}

/** Line two: the working, so the unit price can be checked by hand ("buy 2 for €4.00, €2.00 a pack, 500 g"). */
function workingLine(offer: PromoOffer): string {
  const parts: string[] = [];
  if (offer.buyQuantity > 1) {
    parts.push(`buy ${offer.buyQuantity} for ${euro(offer.payTotal)}`);
  }
  parts.push(`${euro(offer.eachPrice)} a pack`);
  if (offer.packSize) {
    parts.push(`${offer.packSize} pack`);
  }
  if (offer.referenceUnitPrice !== null && offer.unit !== null) {
    parts.push(`was ${euro(offer.referenceUnitPrice)}${UNIT_SUFFIX[offer.unit]}`);
  }
  return parts.join(' · ');
}

/** Line three: how old the price is, when the offer ends, and anything that kept it out of the ranking. */
function readingLine(offer: PromoOffer): string {
  const parts = [`Seen ${shortDate(offer.observedOn)}`];
  if (offer.validUntil) {
    parts.push(`until ${shortDate(offer.validUntil)}`);
  }
  for (const flag of offer.flags) {
    parts.push(flagText(flag));
  }
  return parts.join(' · ');
}

export function offerLines(offer: PromoOffer): string[] {
  return [dealLine(offer), workingLine(offer), readingLine(offer)];
}

/** A place only means something against at least one other shop. */
const MIN_COMPARED = 2;

/** "Best of 3", "2nd of 3", or empty when the offer was not ranked or stands alone. */
export function placeText(offer: PromoOffer, product: PromoProduct): string {
  if (offer.rank === null || product.ranked < MIN_COMPARED) {
    return '';
  }
  const n = offer.rank;
  return n === 1 ? `Best of ${product.ranked}` : `#${n} of ${product.ranked}`;
}

export function gapText(gap: PromoGap): string {
  switch (gap.reason) {
    case 'not_read':
      return 'No price read yet';
    case 'no_price':
      return 'No price listed';
    case 'upcoming':
      return `Offer starts ${shortDate(gap.date)}`;
    case 'ended':
      return `Offer ended ${shortDate(gap.date)}`;
  }
}

export interface StoreGroup {
  readonly store: StoreId;
  readonly name: string;
  readonly entries: readonly {readonly product: PromoProduct; readonly offer: PromoOffer}[];
}

/** The promos of each shop, shop by shop; ordinary shelf prices are left out, this is the list of what is on offer. */
export function byStore(products: readonly PromoProduct[]): StoreGroup[] {
  const groups = new Map<StoreId, {name: string; entries: StoreGroup['entries'][number][]}>();
  for (const product of products) {
    for (const offer of product.offers) {
      if (!offer.isPromo) {
        continue;
      }
      const group = groups.get(offer.store) ?? {name: offer.storeName, entries: []};
      group.entries.push({product, offer});
      groups.set(offer.store, group);
    }
  }
  return [...groups.entries()]
    .map(([store, group]) => ({store, name: group.name, entries: group.entries}))
    .sort((a, b) => a.name.localeCompare(b.name));
}
