import { parse } from 'node-html-parser';
import type { ParseResult, Reading, StoreId } from '../types.ts';

export interface JsonLdProduct {
  name?: string;
  price?: number;
  inStock?: boolean;
  image?: string;
}

/** The server's cap on a photo address (`ImageUrls.MaxLength`). */
const MAX_IMAGE_URL = 500;

/** A usable photo address: absolute https, no login or control characters in it, within the server's cap; anything else is no photo. */
export function toImageUrl(value: unknown): string | undefined {
  if (typeof value !== 'string') {
    return undefined;
  }
  const text = value.trim();
  if (text.length === 0 || text.length > MAX_IMAGE_URL || /[\u0000-\u001f\u007f-\u009f]/.test(text)) {
    return undefined;
  }
  try {
    const url = new URL(text);
    return url.protocol === 'https:' && url.username === '' && url.password === '' ? text : undefined;
  } catch {
    return undefined;
  }
}

/** schema.org `image` is a string, a list, or an object with a `url`; the first usable address wins. */
function firstImage(value: unknown): string | undefined {
  if (Array.isArray(value)) {
    return value.map(firstImage).find((found) => found !== undefined);
  }
  return isRecord(value) ? toImageUrl(value['url']) : toImageUrl(value);
}

/** The page's `og:image` (the picture shops give link previews), when it is a usable address. */
export function readOgImage(html: string): string | undefined {
  return toImageUrl(parse(html).querySelector('meta[property="og:image"]')?.getAttribute('content'));
}

/** The first schema.org Product in the page's JSON-LD blocks, reduced to what the reader posts. */
export function readJsonLdProduct(html: string): JsonLdProduct | undefined {
  const root = parse(html);
  for (const script of root.querySelectorAll('script[type="application/ld+json"]')) {
    let data: unknown;
    try {
      data = JSON.parse(script.textContent);
    } catch {
      continue;
    }
    const product = findProduct(data);
    if (product) {
      return product;
    }
  }
  return undefined;
}

function findProduct(node: unknown): JsonLdProduct | undefined {
  if (Array.isArray(node)) {
    for (const item of node) {
      const hit = findProduct(item);
      if (hit) {
        return hit;
      }
    }
    return undefined;
  }
  if (!isRecord(node)) {
    return undefined;
  }
  if (isRecord(node['@graph']) || Array.isArray(node['@graph'])) {
    const hit = findProduct(node['@graph']);
    if (hit) {
      return hit;
    }
  }
  const type = node['@type'];
  const isProduct = type === 'Product' || (Array.isArray(type) && type.includes('Product'));
  if (!isProduct) {
    return undefined;
  }
  const offer = firstOffer(node['offers']);
  const result: JsonLdProduct = {};
  if (typeof node['name'] === 'string' && node['name'].trim()) {
    result.name = node['name'].trim();
  }
  const image = firstImage(node['image']);
  if (image !== undefined) {
    result.image = image;
  }
  if (offer) {
    const price = toMoney(offer['price'] ?? (isRecord(offer['priceSpecification']) ? offer['priceSpecification']['price'] : undefined));
    if (price !== undefined) {
      result.price = price;
    }
    const availability = offer['availability'];
    if (typeof availability === 'string') {
      result.inStock = /InStock|LimitedAvailability|InStoreOnly|OnlineOnly/i.test(availability) ? true : /OutOfStock|SoldOut|Discontinued/i.test(availability) ? false : undefined;
    }
  }
  return result;
}

function firstOffer(offers: unknown): Record<string, unknown> | undefined {
  if (Array.isArray(offers)) {
    return offers.find(isRecord);
  }
  if (isRecord(offers)) {
    const nested = offers['offers'];
    return Array.isArray(nested) ? nested.find(isRecord) : offers;
  }
  return undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** "€1.49", "1.49" or 1.49 as a euro amount with at most cents, or undefined. */
export function toMoney(value: unknown): number | undefined {
  const text = typeof value === 'number' ? String(value) : typeof value === 'string' ? value.replace(/[€\s]/g, '').replace(/,(?=\d{2}$)/, '.') : '';
  if (!/^\d{1,4}(\.\d{1,2})?$/.test(text)) {
    return undefined;
  }
  const amount = Number(text);
  return amount > 0 ? amount : undefined;
}

/**
 * A product page read from its schema.org JSON-LD Product offer alone: price and stock from the offer, the name from the
 * page's first `h1` (the JSON-LD name when there is none). Nothing is read from the visible text, so a carousel or footer
 * with offers of its own cannot put another product's price on this one. A missing optional field is safe; an offer
 * recorded against the wrong product is not.
 */
export function parseJsonLdOnly(store: StoreId, html: string, url: string): ParseResult {
  const ld = readJsonLdProduct(html);
  if (ld?.price === undefined && ld?.inStock !== false) {
    return { kind: 'unreadable', why: 'no price on the page' };
  }

  const reading: Reading = { store, url };
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
