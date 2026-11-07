import assert from 'node:assert/strict';
import { test } from 'node:test';
import { parseWorkList } from '../src/worklist.ts';

test('the example shape loads, trimmed of query, fragment and trailing slash, duplicates dropped', () => {
  const items = parseWorkList([
    { store: 'aldi', url: 'https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541?x=1#top' },
    { store: 'aldi', url: 'https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541/' },
    { store: 'tesco', url: ' https://www.tesco.ie/groceries/en-IE/products/254317390 ' },
  ]);
  assert.deepEqual(items, [
    { store: 'aldi', url: 'https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541' },
    { store: 'tesco', url: 'https://www.tesco.ie/groceries/en-IE/products/254317390' },
  ]);
});

test('anything that is not a product page on the shop is refused with its position', () => {
  const bad = [
    { store: 'lidl', url: 'https://www.lidl.ie/p/aubergine/p11214436' },
    { store: 'aldi', url: 'http://www.aldi.ie/product/x-000000000000422541' },
    { store: 'aldi', url: 'https://evil.example/product/x-000000000000422541' },
    { store: 'aldi', url: 'https://www.aldi.ie/products/chilled-food' },
    { store: 'tesco', url: 'https://www.tesco.ie/groceries/en-IE/search?query=milk' },
    { store: 'tesco', url: 42 },
    'https://www.tesco.ie/groceries/en-IE/products/254317390',
  ];
  for (const entry of bad) {
    assert.throws(() => parseWorkList([{ store: 'aldi', url: 'https://www.aldi.ie/product/x-000000000000422541' }, entry]), /entry 2/);
  }
  assert.throws(() => parseWorkList({}), /JSON array/);
});
