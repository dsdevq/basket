import { parse } from 'node-html-parser';
import type { ParseResult, Reading } from '../types.ts';
import { readJsonLdProduct, toMoney } from './jsonld.ts';

const MONEY = String.raw`€\s?(\d{1,4}(?:\.\d{2})?)`;

/**
 * A tesco.ie product page. The price, stock and name come from the page's schema.org JSON-LD. The Clubcard price, the
 * old price and the offer dates are read from the visible text next to their labels ("Clubcard Price", "Was",
 * "from ... until ..."), because Tesco's class names are generated and change; a label that is not there adds nothing.
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
  const text = root.text.replace(/\s+/g, ' ');

  const reading: Reading = { store: 'tesco', url };
  const title = root.querySelector('h1')?.text.trim() || ld?.name;
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
  const window = /from\s+(\d{1,2})\/(\d{1,2})\/(\d{4})\s+(?:until|to)\s+(\d{1,2})\/(\d{1,2})\/(\d{4})/i.exec(text);
  const until = window ? undefined : /until\s+(\d{1,2})\/(\d{1,2})\/(\d{4})/i.exec(text);
  if (window) {
    reading.validFrom = isoDate(window[3], window[2], window[1]);
    reading.validUntil = isoDate(window[6], window[5], window[4]);
  } else if (until) {
    reading.validUntil = isoDate(until[3], until[2], until[1]);
  }
  return { kind: 'reading', reading };
}

/** The euro amount written right before or right after <label>, or undefined. */
function labelledMoney(text: string, label: string): number | undefined {
  const after = new RegExp(`${label}:?\\s*${MONEY}`, 'i').exec(text);
  const before = new RegExp(`${MONEY}\\s*${label}`, 'i').exec(text);
  return toMoney(after?.[1] ?? before?.[1]);
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
