import type { ParseResult, Reading } from '../types.ts';
import { CATEGORY_SEPARATOR, readOgImage, toImageUrl, toMoney } from './jsonld.ts';
import { readAssignedJson } from './state.ts';

const STATE_MARKER = 'window.__PRELOADED_STATE__=';
const MAX_CATEGORY = 200;

/**
 * A dunnesstoresgrocery.com product page (a Mi9 Retail storefront), read from the Redux state the server renders into the
 * page (`window.__PRELOADED_STATE__.product`): the site publishes no schema.org Product JSON-LD, so the JSON-LD route that
 * serves Tesco reads nothing here. The price, stock (`available`), name, category (the deepest `categoryBreadcrumb` without
 * the leading "/Grocery") and photo (`image.default`, else `og:image`) come from that product. A promotion that is not
 * tied to a loyalty card becomes the promo text and its dates; the was price is not read, since no recorded page shows one.
 * Nothing is read from the visible text, where carousels carry other products' prices.
 *
 * The product's `sku` must equal the number at the end of the page's address, else the page is unreadable: an offer
 * recorded against the wrong product is worse than a missing one. Dunnes has no card price in basket.
 *
 * Checked on recorded pages (2025-09-07, 2026-11-04), not on a live one: Cloudflare challenges the reader's profile.
 */
export function parseDunnes(html: string, url: string): ParseResult {
  const product = readProduct(html, url);
  if (!product) {
    return { kind: 'unreadable', why: 'no price on the page' };
  }
  const price = toMoney(product['price']);
  const inStock = typeof product['available'] === 'boolean' ? product['available'] : undefined;
  if (price === undefined && inStock !== false) {
    return { kind: 'unreadable', why: 'no price on the page' };
  }

  const reading: Reading = { store: 'dunnes', url };
  const title = typeof product['name'] === 'string' ? product['name'].trim() : '';
  if (title) {
    reading.title = title;
  }
  if (price !== undefined) {
    reading.price = price;
  }
  if (inStock !== undefined) {
    reading.inStock = inStock;
  }
  const category = readCategory(product['categories']);
  if (category) {
    reading.category = category;
  }
  const image = isRecord(product['image']) ? toImageUrl(product['image']['default']) : undefined;
  const imageUrl = image ?? readOgImage(html);
  if (imageUrl !== undefined) {
    reading.imageUrl = imageUrl;
  }
  if (price !== undefined) {
    Object.assign(reading, readPromotion(product['promotions']));
  }
  return { kind: 'reading', reading };
}

/** The state's product, when it is the page's own: its sku is the number at the end of the address. */
function readProduct(html: string, url: string): Record<string, unknown> | undefined {
  const product = readAssignedJson(html, STATE_MARKER)?.['product'];
  const id = /-(\d{6,12})$/.exec(new URL(url).pathname)?.[1];
  return isRecord(product) && id !== undefined && String(product['sku']) === id ? product : undefined;
}

/** "Fresh Fruit > Tropical & Exotic Fruit > Kiwi" from the deepest `/Grocery/Fresh Fruit/...` breadcrumb. */
function readCategory(categories: unknown): string | undefined {
  if (!Array.isArray(categories)) {
    return undefined;
  }
  const paths = categories.filter(isRecord).map((c) => c['categoryBreadcrumb']).filter((b): b is string => typeof b === 'string');
  const deepest = paths.reduce((a, b) => (b.split('/').length > a.split('/').length ? b : a), '');
  const crumbs = deepest.split('/').map((c) => c.trim()).filter(Boolean);
  if (crumbs[0]?.toLowerCase() === 'grocery') {
    crumbs.shift();
  }
  const path = crumbs.join(CATEGORY_SEPARATOR);
  return path ? path.slice(0, MAX_CATEGORY) : undefined;
}

/** The first promotion that is not for a loyalty card: its wording, and its local start and end dates when they read. */
function readPromotion(promotions: unknown): Pick<Reading, 'promoText' | 'validFrom' | 'validUntil'> {
  const promo = Array.isArray(promotions) ? promotions.filter(isRecord).find((p) => p['loyaltyBased'] !== true) : undefined;
  const text = [promo?.['description'], promo?.['name']].find((t): t is string => typeof t === 'string' && t.trim() !== '')?.trim();
  if (!promo || !text) {
    return {};
  }
  const from = toIsoDate(promo['startDate']);
  const until = toIsoDate(promo['endDate']);
  return { promoText: text, ...(from && until && from <= until ? { validFrom: from, validUntil: until } : {}) };
}

/** "24/11/2026" as "2026-11-24", or undefined. */
function toIsoDate(value: unknown): string | undefined {
  const m = typeof value === 'string' ? /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(value) : null;
  return m ? `${m[3]}-${m[2]}-${m[1]}` : undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
