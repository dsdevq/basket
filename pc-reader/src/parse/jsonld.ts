import { parse } from 'node-html-parser';
import type { ParseResult, Reading, StoreId } from '../types.ts';

export interface JsonLdProduct {
  name?: string;
  price?: number;
  inStock?: boolean;
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
