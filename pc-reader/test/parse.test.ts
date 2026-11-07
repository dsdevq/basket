import assert from 'node:assert/strict';
import { test } from 'node:test';
import { detectBlock } from '../src/parse/block.ts';
import { readPage } from '../src/parse/index.ts';
import type { ParseResult } from '../src/types.ts';
import { fixture } from './support.ts';

const ALDI = 'https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541';
const TESCO = 'https://www.tesco.ie/groceries/en-IE/products/254317390';

function reading(result: ParseResult) {
  assert.equal(result.kind, 'reading', JSON.stringify(result));
  return result.kind === 'reading' ? result.reading : (undefined as never);
}

test('aldi: the recorded product page gives its price, title and stock', () => {
  const r = reading(readPage('aldi', ALDI, { status: 200, html: fixture('aldi-product-page.html') }));
  assert.deepEqual(r, { store: 'aldi', url: ALDI, title: 'CLONBAWN Protein Milk', price: 1.49, inStock: true });
});

test('aldi: an on-offer page carries the was price and the discount badge', () => {
  const r = reading(readPage('aldi', ALDI, { status: 200, html: fixture('aldi-product-page-on-sale.html') }));
  assert.deepEqual(r, { store: 'aldi', url: ALDI, title: 'ACTILEAF UHT Unsweetened Almond Drink', price: 1.29, wasPrice: 1.45, promoText: 'Save 11%', inStock: true });
});

test('aldi: with no JSON-LD the price is read from the price block', () => {
  const html = fixture('aldi-product-page-on-sale.html').replace(/<script type="application\/ld\+json">.*?<\/script>/s, '');
  const r = reading(readPage('aldi', ALDI, { status: 200, html }));
  assert.equal(r.price, 1.29);
  assert.equal(r.inStock, undefined);
});

test('aldi: a sold-out product without a price is a valid out-of-stock reading', () => {
  const r = reading(readPage('aldi', ALDI, { status: 200, html: fixture('aldi-product-page-sold-out.html') }));
  assert.equal(r.price, undefined);
  assert.equal(r.inStock, false);
});

test('tesco: the price, title and stock come from the JSON-LD offer, and offer wording in the text is not read', () => {
  const r = reading(readPage('tesco', TESCO, { status: 200, html: fixture('tesco-product-page.html') }));
  assert.deepEqual(r, { store: 'tesco', url: TESCO, title: 'Tesco Fresh Whole Milk 2 Litre', price: 2.15, inStock: true });
});

test('tesco: a plain product has only its price', () => {
  const r = reading(readPage('tesco', TESCO, { status: 200, html: fixture('tesco-product-page-plain.html') }));
  assert.deepEqual(r, { store: 'tesco', url: TESCO, title: 'Tesco Brown Bread 800G', price: 1.65, inStock: true });
});

test('a page with no product data is unreadable, not a guess', () => {
  for (const store of ['aldi', 'tesco'] as const) {
    const result = readPage(store, ALDI, { status: 200, html: fixture('page-without-product.html') });
    assert.equal(result.kind, 'unreadable');
  }
});

test('a 404 is unreadable for that page, not a block', () => {
  assert.equal(readPage('aldi', ALDI, { status: 404, html: '<html><title>Not found</title></html>' }).kind, 'unreadable');
});

test('block: Akamai Access Denied, 403, 429 and challenge pages stop the store', () => {
  const denied = fixture('akamai-access-denied.html');
  assert.equal(readPage('tesco', TESCO, { status: 200, html: denied }).kind, 'blocked');
  assert.equal(readPage('tesco', TESCO, { status: 403, html: denied }).kind, 'blocked');
  assert.equal(detectBlock({ status: 429, html: '' }), 'HTTP 429');
  assert.match(detectBlock({ status: 200, html: '<title>Just a moment...</title>' }) ?? '', /challenge/);
  assert.match(detectBlock({ status: 200, html: '<title>x</title><div class="g-recaptcha">captcha</div>' }) ?? '', /challenge/);
});

test('block: an ordinary product page is not mistaken for a challenge', () => {
  assert.equal(detectBlock({ status: 200, html: fixture('aldi-product-page.html') }), null);
  // A long page that merely mentions a captcha in its scripts is still a product page.
  assert.equal(detectBlock({ status: 200, html: `<title>Milk</title>${'x'.repeat(30_000)}captcha` }), null);
});

test('tesco: offers in a div and list carousel or the footer do not leak into this product', () => {
  const r = reading(readPage('tesco', TESCO, { status: 200, html: fixture('tesco-product-page-carousel.html') }));
  assert.deepEqual(r, { store: 'tesco', url: TESCO, title: 'Tesco Brown Bread 800G', price: 1.65, inStock: true });
});
