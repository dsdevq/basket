import { parse } from 'node-html-parser';
import type { ParseResult, Reading } from '../types.ts';
import { readJsonLdProduct } from './jsonld.ts';

/**
 * A tesco.ie product page. The price, stock and name come from the page's schema.org JSON-LD Product offer. Nothing is
 * read from the visible text: the old price, the Clubcard price, the promo text and the offer dates stay unset, because
 * Tesco's class names are generated and change, and a related-products carousel or a footer on the same page carries
 * offers of its own. A missing optional field is safe; an offer recorded against the wrong product is not.
 *
 * Not yet checked against a live page: tesco.ie refused the author's PC with Akamai's "Access Denied" (2026-11-07),
 * so this follows the schema.org markup and is tested on hand-written fixtures only.
 */
export function parseTesco(html: string, url: string): ParseResult {
  const ld = readJsonLdProduct(html);
  if (ld?.price === undefined && ld?.inStock !== false) {
    return { kind: 'unreadable', why: 'no price on the page' };
  }

  const reading: Reading = { store: 'tesco', url };
  const title = parse(html).querySelector('h1')?.text.trim() || ld?.name;
  if (title) {
    reading.title = title;
  }
  if (ld?.price !== undefined) {
    reading.price = ld.price;
  }
  if (ld?.inStock !== undefined) {
    reading.inStock = ld.inStock;
  }
  return { kind: 'reading', reading };
}
