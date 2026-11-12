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

export const CATEGORY_SEPARATOR = ' > ';
const MAX_CATEGORY = 200;

/**
 * The category path of a product page: the crumbs of its schema.org BreadcrumbList (or, without one, of a breadcrumb `nav`)
 * between the shop's front page and the product, joined with " > ". The front page ("Home", or a link to `/`) is dropped,
 * and so is the last crumb when it is the product (its name, or its address, is the page's). `rootNames` are further
 * leading crumbs that only name the whole catalogue (Aldi's "Products"). Undefined when no breadcrumb is left.
 */
export function readCategory(html: string, url: string, productName?: string, rootNames: readonly string[] = []): string | undefined {
  const root = parse(html);
  let crumbs: Crumb[] | undefined;
  for (const script of root.querySelectorAll('script[type="application/ld+json"]')) {
    try {
      crumbs = findBreadcrumbs(JSON.parse(script.textContent));
    } catch {
      continue;
    }
    if (crumbs) {
      break;
    }
  }
  crumbs ??= root
    .querySelectorAll('nav[aria-label*="readcrumb" i] li, [class*="readcrumb" i] li')
    .map((li): Crumb => ({ name: li.text.replace(/\s+/g, ' ').trim(), href: li.querySelector('a')?.getAttribute('href') ?? undefined }));
  crumbs = crumbs.filter((c) => c.name);
  if (crumbs.length > 0 && isFrontPage(crumbs[0]!)) {
    crumbs.shift();
  }
  const roots = rootNames.map((n) => n.toLowerCase());
  while (crumbs.length > 0 && roots.includes(crumbs[0]!.name.toLowerCase())) {
    crumbs.shift();
  }
  const last = crumbs[crumbs.length - 1];
  if (last && (last.name.toLowerCase() === productName?.toLowerCase() || samePath(last.href, url))) {
    crumbs.pop();
  }
  const path = crumbs.map((c) => c.name).join(CATEGORY_SEPARATOR);
  return path ? path.slice(0, MAX_CATEGORY) : undefined;
}

interface Crumb {
  name: string;
  href?: string;
}

function findBreadcrumbs(node: unknown): Crumb[] | undefined {
  if (Array.isArray(node)) {
    return node.map(findBreadcrumbs).find((hit) => hit);
  }
  if (!isRecord(node)) {
    return undefined;
  }
  if (node['@graph']) {
    const hit = findBreadcrumbs(node['@graph']);
    if (hit) {
      return hit;
    }
  }
  const items = node['itemListElement'];
  if (node['@type'] !== 'BreadcrumbList' || !Array.isArray(items)) {
    return undefined;
  }
  return items.filter(isRecord).map((entry): Crumb => {
    const item = entry['item'];
    const name = typeof entry['name'] === 'string' ? entry['name'] : isRecord(item) && typeof item['name'] === 'string' ? item['name'] : '';
    const href = typeof item === 'string' ? item : isRecord(item) && typeof item['@id'] === 'string' ? item['@id'] : undefined;
    return { name: name.replace(/\s+/g, ' ').trim(), href };
  });
}

function isFrontPage(crumb: Crumb): boolean {
  return crumb.name.toLowerCase() === 'home' || pathOf(crumb.href) === '/';
}

function pathOf(href: string | undefined): string | undefined {
  if (!href) {
    return undefined;
  }
  try {
    return new URL(href, 'https://placeholder.invalid').pathname;
  } catch {
    return undefined;
  }
}

function samePath(href: string | undefined, url: string): boolean {
  const path = pathOf(href);
  return path !== undefined && path !== '/' && path === pathOf(url);
}

/**
 * A product page read from its schema.org JSON-LD Product offer alone: price and stock from the offer, the name from the
 * page's first `h1` (the JSON-LD name when there is none). Nothing is read from the visible text, so a carousel or footer
 * with offers of its own cannot put another product's price on this one. The photo is the Product's JSON-LD `image`, else
 * the page's `og:image`; an address that is not plain https is dropped and the reading is posted without it. A missing optional field is safe; an offer
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
  const category = readCategory(html, url, title);
  if (category) {
    reading.category = category;
  }
  const imageUrl = ld?.image ?? readOgImage(html);
  if (imageUrl !== undefined) {
    reading.imageUrl = imageUrl;
  }
  return { kind: 'reading', reading };
}
