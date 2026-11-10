import assert from 'node:assert/strict';
import { test } from 'node:test';
import { detectBlock } from '../src/parse/block.ts';
import { readPage } from '../src/parse/index.ts';
import type { ParseResult } from '../src/types.ts';
import { fixture } from './support.ts';

const ALDI = 'https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541';
const TESCO = 'https://www.tesco.ie/groceries/en-IE/products/254317390';
const DUNNES = 'https://www.dunnesstoresgrocery.com/product/dunnes-stores-irish-whole-milk-2l-id-100317001';

function reading(result: ParseResult) {
  assert.equal(result.kind, 'reading', JSON.stringify(result));
  return result.kind === 'reading' ? result.reading : (undefined as never);
}

test('aldi: the recorded product page gives its price, title and stock', () => {
  const r = reading(readPage('aldi', ALDI, { status: 200, html: fixture('aldi-product-page.html') }));
  assert.deepEqual(r, { store: 'aldi', url: ALDI, title: 'CLONBAWN Protein Milk', price: 1.49, inStock: true, category: 'Chilled Food > Milk', imageUrl: 'https://dm.emea.cms.aldi.cx/is/image/aldiprodeu/product/jpg/scaleWidth/300/d458166c-11f4-413b-be01-a553ebe074ce/Protein%20Milk' });
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

test('aldi: the photo is the JSON-LD image, else og:image, and only a plain https address', () => {
  const html = fixture('aldi-product-page.html');
  const ld = /<script type="application\/ld\+json">.*?<\/script>/gs;
  const withOg = html.replace(ld, '').replace('</head>', '<meta property="og:image" content="https://dm.emea.cms.aldi.cx/is/image/og.jpg?a=1&amp;b=2"></head>');
  assert.equal(reading(readPage('aldi', ALDI, { status: 200, html: withOg })).imageUrl, 'https://dm.emea.cms.aldi.cx/is/image/og.jpg?a=1&b=2');

  const insecure = html.replace('"image":["https://', '"image":["http://');
  assert.equal(reading(readPage('aldi', ALDI, { status: 200, html: insecure })).imageUrl, undefined);
  const none = html.replace(/"image":\[[^\]]*\],?/, '');
  assert.equal(reading(readPage('aldi', ALDI, { status: 200, html: none })).imageUrl, undefined);
  const withNewline = html.replace('Protein%20Milk"', 'Protein\\nMilk"');
  assert.equal(reading(readPage('aldi', ALDI, { status: 200, html: withNewline })).imageUrl, undefined);
  const withTab = html.replace('Protein%20Milk"', 'Protein\\tMilk"');
  assert.equal(reading(readPage('aldi', ALDI, { status: 200, html: withTab })).imageUrl, undefined);
  const tooLong = html.replace('Protein%20Milk"', `${'a'.repeat(600)}"`);
  assert.equal(reading(readPage('aldi', ALDI, { status: 200, html: tooLong })).imageUrl, undefined);
});

test('tesco: the price, title and stock come from the JSON-LD offer, and offer wording in the text is not read', () => {
  const r = reading(readPage('tesco', TESCO, { status: 200, html: fixture('tesco-product-page.html') }));
  assert.deepEqual(r, { store: 'tesco', url: TESCO, title: 'Tesco Fresh Whole Milk 2 Litre', price: 2.15, inStock: true, category: 'Fresh Food > Milk' });
});

test('tesco: a plain product has only its price', () => {
  const r = reading(readPage('tesco', TESCO, { status: 200, html: fixture('tesco-product-page-plain.html') }));
  assert.deepEqual(r, { store: 'tesco', url: TESCO, title: 'Tesco Brown Bread 800G', price: 1.65, inStock: true });
});

test('a page with no product data is unreadable, not a guess', () => {
  for (const store of ['aldi', 'dunnes', 'tesco'] as const) {
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

test('dunnes: the price, title and stock come from the JSON-LD offer, and the old price and carousel in the text are not read', () => {
  const r = reading(readPage('dunnes', DUNNES, { status: 200, html: fixture('dunnes-product-page.html') }));
  assert.deepEqual(r, { store: 'dunnes', url: DUNNES, title: 'Dunnes Stores Irish Whole Milk 2L', price: 2.2, inStock: true, category: 'Dairy & Eggs > Milk' });
});

test('dunnes: a sold-out product without a price is a valid out-of-stock reading', () => {
  const r = reading(readPage('dunnes', DUNNES, { status: 200, html: fixture('dunnes-product-page-sold-out.html') }));
  assert.deepEqual(r, { store: 'dunnes', url: DUNNES, title: 'Dunnes Stores Swede', inStock: false });
});

test('dunnes: Cloudflare\'s "Just a moment..." challenge, with or without its 403, stops the store', () => {
  const challenge = fixture('cloudflare-just-a-moment.html');
  assert.match(detectBlock({ status: 403, html: challenge }) ?? '', /HTTP 403/);
  assert.match(readPage('dunnes', DUNNES, { status: 200, html: challenge }).kind, /blocked/);
  const renamed = challenge.replace('Just a moment...', 'Performing security verification');
  assert.match(detectBlock({ status: 200, html: renamed }) ?? '', /challenge/);
  assert.match(detectBlock({ status: 200, html: '<title>Dunnes</title><script src="https://challenges.cloudflare.com/x.js"></script>' }) ?? '', /challenge/);
});

test('dunnes: the category comes from the breadcrumb nav, and the carousel list is not mistaken for it', () => {
  const r = reading(readPage('dunnes', DUNNES, { status: 200, html: fixture('dunnes-product-page.html') }));
  assert.equal(r.category, 'Dairy & Eggs > Milk');
});

test('a page with no breadcrumb has no category, and an absent one is left out of the reading', () => {
  const r = reading(readPage('tesco', TESCO, { status: 200, html: fixture('tesco-product-page-plain.html') }));
  assert.equal('category' in r, false);
});

test('a breadcrumb that is only the front page and the product leaves no category', () => {
  const html = fixture('tesco-product-page.html').replace(/"itemListElement":\[.*?\]\}/s, '"itemListElement":[{"name":"Home","item":"https://www.tesco.ie/"},{"name":"Tesco Fresh Whole Milk 2 Litre","item":"https://www.tesco.ie/groceries/en-IE/products/254317390"}]}');
  const r = reading(readPage('tesco', TESCO, { status: 200, html }));
  assert.equal('category' in r, false);
});

test('tesco and dunnes: the photo is og:image, else the JSON-LD image, and only a plain https address', () => {
  for (const [store, url, file] of [['tesco', TESCO, 'tesco-product-page.html'], ['dunnes', DUNNES, 'dunnes-product-page.html']] as const) {
    const html = fixture(file);
    const read = (page: string) => reading(readPage(store, url, { status: 200, html: page })).imageUrl;
    assert.equal(read(html), undefined);

    const og = (content: string) => html.replace('</head>', `<meta property="og:image" content="${content}"></head>`);
    assert.equal(read(og('https://img.example.ie/p/1.jpg?a=1&amp;b=2')), 'https://img.example.ie/p/1.jpg?a=1&b=2');

    const withLd = html.replace('"offers"', '"image":["https://img.example.ie/ld/1.jpg","https://img.example.ie/ld/2.jpg"],"offers"');
    assert.equal(read(withLd), 'https://img.example.ie/ld/1.jpg');
    assert.equal(read(withLd.replace('</head>', '<meta property="og:image" content="https://img.example.ie/og.jpg"></head>')), 'https://img.example.ie/og.jpg');

    // A bad og:image is dropped, not a failed read; the JSON-LD image still serves.
    assert.equal(read(og('http://img.example.ie/p/1.jpg')), undefined);
    assert.equal(read(withLd.replace('</head>', '<meta property="og:image" content="/relative.jpg"></head>')), 'https://img.example.ie/ld/1.jpg');
    assert.equal(read(og(`https://img.example.ie/${'a'.repeat(600)}`)), undefined);
    assert.equal(read(og('https://user:pw@img.example.ie/p.jpg')), undefined);
  }
});

test('dunnes: a sold-out reading still carries its photo', () => {
  const html = fixture('dunnes-product-page-sold-out.html').replace('</head>', '<meta property="og:image" content="https://img.example.ie/swede.jpg"></head>');
  const r = reading(readPage('dunnes', DUNNES, { status: 200, html }));
  assert.deepEqual(r, { store: 'dunnes', url: DUNNES, title: 'Dunnes Stores Swede', inStock: false, imageUrl: 'https://img.example.ie/swede.jpg' });
});
