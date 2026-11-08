import type {Paid, Price, ProductStore, RunOutcome, StoreStatus} from './prices.model';

const CENTS = 2;
const MONTH_OF_YEAR = 1;
const DAY_OF_MONTH = 2;
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** "2026-11-05" -> "5 Nov". Dates arrive as plain days (Dublin), so they are never shifted by a time zone. */
export function shortDate(day: string | null): string {
  const match = /^\d{4}-(\d{2})-(\d{2})$/.exec(day ?? '');
  return match
    ? `${Number(match[DAY_OF_MONTH])} ${MONTHS[Number(match[MONTH_OF_YEAR]) - 1] ?? ''}`.trim()
    : '';
}

export function euro(value: number | null): string {
  return value === null ? '' : `€${value.toFixed(CENTS)}`;
}

/** What the row shows on the right: the best price today, else what the shop says instead. */
export function headline(entry: ProductStore): string {
  const price = entry.price;
  if (!entry.listing) {
    return '';
  }
  if (!price) {
    return '—';
  }
  switch (price.state) {
    case 'current':
      return euro(price.best);
    case 'upcoming':
      return `from ${shortDate(price.validFrom)}`;
    case 'ended':
      return 'ended';
    default:
      return 'no price';
  }
}

function priceParts(price: Price): string[] {
  const parts: string[] = [];
  if (price.state === 'upcoming') {
    parts.push(
      `offer ${euro(price.price ?? price.loyaltyPrice)} starts ${shortDate(price.validFrom)}`
    );
  } else if (price.state === 'ended') {
    parts.push(`offer ended ${shortDate(price.validUntil)}`);
  } else if (price.state === 'unavailable') {
    parts.push('no price listed');
  } else {
    if (price.loyaltyPrice !== null && price.loyaltyScheme) {
      const shelf = price.price === null ? '' : `, ${euro(price.price)} without`;
      parts.push(`${price.loyaltyScheme} ${euro(price.loyaltyPrice)}${shelf}`);
    }
    if (price.wasPrice !== null) {
      parts.push(`was ${euro(price.wasPrice)}`);
    }
    if (price.promoText) {
      parts.push(price.promoText);
    }
    if (price.validUntil) {
      parts.push(`until ${shortDate(price.validUntil)}`);
    }
  }
  return parts;
}

export function lastPaidLine(paid: Paid | null): string {
  if (!paid) {
    return '';
  }
  return paid.state === 'known'
    ? `Last paid ${euro(paid.price)} on ${shortDate(paid.paidOn)}`
    : 'Last paid: not yet connected';
}

function unreadLine(entry: ProductStore): string {
  if (entry.readBy === 'pc') {
    // Tesco, Aldi and Dunnes block the server; their prices arrive when the PC next reads them.
    return 'Waiting for the next PC read';
  }
  if (!entry.listing) {
    return 'No page linked yet';
  }
  return entry.listing.error
    ? `Not read yet · ${entry.listing.error}`
    : 'Not read yet - the nightly pull will fetch it';
}

/**
 * The lines under the store name, one fact group per line: what the price is (card price, old price, promo,
 * how long it lasts), then when it was read and how that went. A shop with no reading yet has one line.
 */
export function detailLines(entry: ProductStore): string[] {
  const price = entry.price;
  const paid = lastPaidLine(entry.lastPaid);
  if (!price) {
    return [unreadLine(entry), paid].filter(Boolean);
  }
  const read = [`Seen ${shortDate(price.observedOn)}`];
  if (paid) {
    read.push(paid);
  }
  if (entry.listing?.error) {
    read.push(`Last read failed: ${entry.listing.error}`);
  }
  return [priceParts(price).join(' · '), read.join(' · ')].filter(Boolean);
}

const OUTCOME_TITLES: Record<RunOutcome, string> = {
  ok: '',
  partial: 'some products could not be read',
  blocked: "blocks basket's automated requests",
  failed: 'could not be read',
};

/** The warning for a store whose last nightly pull did not fully work; empty when it did (or never ran). */
export function storeProblem(status: StoreStatus): string {
  const run = status.lastRun;
  return run && run.outcome !== 'ok' ? `${status.name} ${OUTCOME_TITLES[run.outcome]}` : '';
}
