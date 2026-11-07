import { parse } from 'node-html-parser';
import type { ParseResult, Reading } from '../types.ts';
import { readJsonLdProduct, toMoney } from './jsonld.ts';

const MONEY = String.raw`€\s?(\d{1,4}(?:\.\d{2})?)`;
const OTHER_REGIONS = 'section, aside, article, nav, footer, h2, h3';

/**
 * A tesco.ie product page. The price, stock and name come from the page's schema.org JSON-LD. The Clubcard price, the
 * old price and the offer dates are read from the visible text next to their labels ("Clubcard Price", "Was",
 * "from ... until ..."), because Tesco's class names are generated and change; a label that is not there adds nothing.
 * They are read only from the block around the page's single <h1> (the product's own title), never from the whole page,
 * so a related-products carousel or a footer cannot lend its offer to this product. When the block cannot be told apart
 * from the rest of the page (no single <h1>, other page regions inside it, or two different offers of one kind in it)
 * they stay unset: a missing optional field is safe, an offer recorded against the wrong product is not.
 *
 * Not yet checked against a live page: tesco.ie refused the author's PC with Akamai's "Access Denied" (2026-11-07),
 * so these rules follow the schema.org markup and Tesco's wording and are tested on a hand-written fixture only.
 */
export function parseTesco(html: string, url: string): ParseResult {
  const ld = readJsonLdProduct(html);
  if (ld?.price === undefined && ld?.inStock !== false) {
    return { kind: 'unreadable', why: 'no price on the page' };
  }

  const root = parse(html);
  for (const junk of root.querySelectorAll('script, style, noscript')) {
    junk.remove();
  }
  const headings = root.querySelectorAll('h1');
  const block = headings.length === 1 ? headings[0]?.parentNode : undefined;
  const text = block && !block.querySelector(OTHER_REGIONS) ? block.text.replace(/\s+/g, ' ') : '';

  const reading: Reading = { store: 'tesco', url };
  const title = headings[0]?.text.trim() || ld?.name;
  if (title) {
    reading.title = title;
  }
  if (ld?.price !== undefined) {
    reading.price = ld.price;
  }
  if (ld?.inStock !== undefined) {
    reading.inStock = ld.inStock;
  }

  const club = labelledMoney(text, String.raw`Clubcard Price`);
  if (club !== undefined && (reading.price === undefined || club < reading.price)) {
    reading.loyaltyPrice = club;
    reading.loyaltyScheme = 'Clubcard';
    reading.promoText = 'Clubcard Price';
  }
  const was = labelledMoney(text, String.raw`Was`);
  if (was !== undefined && reading.price !== undefined && was > reading.price) {
    reading.wasPrice = was;
  }
  const untilDates = new Set([...text.matchAll(/until\s+(\d{1,2}\/\d{1,2}\/\d{4})/gi)].map((m) => m[1]));
  const window = untilDates.size > 1 ? null : /from\s+(\d{1,2})\/(\d{1,2})\/(\d{4})\s+(?:until|to)\s+(\d{1,2})\/(\d{1,2})\/(\d{4})/i.exec(text);
  const until = window || untilDates.size > 1 ? undefined : /until\s+(\d{1,2})\/(\d{1,2})\/(\d{4})/i.exec(text);
  if (window) {
    reading.validFrom = isoDate(window[3], window[2], window[1]);
    reading.validUntil = isoDate(window[6], window[5], window[4]);
  } else if (until) {
    reading.validUntil = isoDate(until[3], until[2], until[1]);
  }
  return { kind: 'reading', reading };
}

/** The one euro amount written right after <label> (else right before it), or undefined when there is none or several. */
function labelledMoney(text: string, label: string): number | undefined {
  const after = amounts(text, new RegExp(`\\b${label}\\b:?\\s*${MONEY}`, 'gi'));
  const found = after.size > 0 ? after : amounts(text, new RegExp(`${MONEY}\\s*\\b${label}\\b`, 'gi'));
  return found.size === 1 ? toMoney([...found][0]) : undefined;
}

function amounts(text: string, pattern: RegExp): Set<string | undefined> {
  return new Set([...text.matchAll(pattern)].map((m) => m[1]));
}

/** A real calendar date as yyyy-mm-dd, or undefined: the server rejects the whole batch on a date it cannot read. */
function isoDate(year: string | undefined, month: string | undefined, day: string | undefined): string | undefined {
  if (!year || !month || !day) {
    return undefined;
  }
  const iso = `${year}-${month.padStart(2, '0')}-${day.padStart(2, '0')}`;
  const parsed = new Date(`${iso}T00:00:00Z`);
  return Number.isNaN(parsed.getTime()) || parsed.toISOString().slice(0, 10) !== iso ? undefined : iso;
}
