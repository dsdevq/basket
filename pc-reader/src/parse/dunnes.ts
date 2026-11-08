import type { ParseResult } from '../types.ts';
import { parseJsonLdOnly } from './jsonld.ts';

/**
 * A dunnesstoresgrocery.com product page, read from the page's schema.org JSON-LD Product offer alone, as Tesco's is.
 * The old price, promo text and offer dates stay unset until a live page shows where they can be read safely. Dunnes has
 * no card price in basket.
 *
 * Not yet checked against a live page: Cloudflare answered the author's PC with its "Just a moment..." challenge
 * (2026-11-08), so this follows the schema.org markup and is tested on hand-written fixtures only.
 */
export function parseDunnes(html: string, url: string): ParseResult {
  return parseJsonLdOnly('dunnes', html, url);
}
