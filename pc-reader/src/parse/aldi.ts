import { parse } from 'node-html-parser';
import type { ParseResult, Reading } from '../types.ts';
import { readJsonLdProduct, toMoney } from './jsonld.ts';

/**
 * An aldi.ie product page. The price, stock and name come from the page's schema.org JSON-LD; the was price and the
 * discount badge from the price block (`base-price`) next to the title. Aldi has no loyalty scheme.
 */
export function parseAldi(html: string, url: string): ParseResult {
  const root = parse(html);
  const ld = readJsonLdProduct(html);
  const block = root.querySelector('.base-price--product-details');

  const shown = toMoney(
    block?.querySelector('.base-price__discounted')?.text ?? block?.querySelector('.base-price__regular')?.text,
  );
  const price = ld?.price ?? shown;
  const inStock = ld?.inStock;
  if (price === undefined && inStock !== false) {
    return { kind: 'unreadable', why: 'no price on the page' };
  }

  const reading: Reading = { store: 'aldi', url };
  const name = root.querySelector('h1.product-details__title')?.text.trim() || ld?.name;
  const brand = root.querySelector('.product-details__brand-name')?.text.trim();
  if (name) {
    reading.title = brand && !name.toLowerCase().startsWith(brand.toLowerCase()) ? `${brand} ${name}` : name;
  }
  if (price !== undefined) {
    reading.price = price;
  }
  const was = toMoney(block?.querySelector('.base-price__was-price')?.text);
  if (was !== undefined && price !== undefined && was > price) {
    reading.wasPrice = was;
  }
  const promo = block?.querySelector('.base-price__discount-tag')?.text.replace(/\s+/g, ' ').trim();
  if (promo) {
    reading.promoText = promo;
  }
  if (inStock !== undefined) {
    reading.inStock = inStock;
  }
  return { kind: 'reading', reading };
}
