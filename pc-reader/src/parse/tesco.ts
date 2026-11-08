import type { ParseResult } from '../types.ts';
import { parseJsonLdOnly } from './jsonld.ts';

/**
 * A tesco.ie product page, read from the page's schema.org JSON-LD Product offer alone. The old price, the Clubcard
 * price, the promo text and the offer dates stay unset, because Tesco's class names are generated and change, and a
 * related-products carousel or a footer on the same page carries offers of its own.
 *
 * Not yet checked against a live page: tesco.ie refused the author's PC with Akamai's "Access Denied" (2026-11-07),
 * so this follows the schema.org markup and is tested on hand-written fixtures only.
 */
export function parseTesco(html: string, url: string): ParseResult {
  return parseJsonLdOnly('tesco', html, url);
}
