import assert from 'node:assert/strict';
import { test } from 'node:test';
import { parseStoreArg, parseWorkList } from '../src/worklist.ts';

test('the example shape loads, trimmed of query, fragment and trailing slash, duplicates dropped', () => {
  const items = parseWorkList([
    { store: 'aldi', url: 'https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541?x=1#top' },
    { store: 'aldi', url: 'https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541/' },
    { store: 'tesco', url: ' https://www.tesco.ie/groceries/en-IE/products/254317390 ' },
    { store: 'dunnes', url: 'https://www.dunnesstoresgrocery.com/product/dunnes-stores-swede-100317581?ref=x' },
  ]);
  assert.deepEqual(items, [
    { store: 'aldi', url: 'https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541' },
    { store: 'tesco', url: 'https://www.tesco.ie/groceries/en-IE/products/254317390' },
    { store: 'dunnes', url: 'https://www.dunnesstoresgrocery.com/product/dunnes-stores-swede-100317581' },
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
    { store: 'dunnes', url: 'https://www.dunnesstores.com/paul-costelloe-living-studio-brown-faux-fur-hat-3647114/p' },
    { store: 'dunnes', url: 'http://www.dunnesstoresgrocery.com/product/dunnes-stores-swede-100317581' },
    { store: 'dunnes', url: 'https://www.dunnesstoresgrocery.com/categories/fresh-meat-poultry-id-47181' },
    { store: 'dunnes', url: 'https://www.dunnesstoresgrocery.com/product/dunnes-stores-swede-100317581@evil.example' },
    { store: 'dunnes', url: 'https://www.tesco.ie/product/dunnes-stores-swede-100317581' },
    'https://www.tesco.ie/groceries/en-IE/products/254317390',
  ];
  for (const entry of bad) {
    assert.throws(() => parseWorkList([{ store: 'aldi', url: 'https://www.aldi.ie/product/x-000000000000422541' }, entry]), /entry 2/);
  }
  assert.throws(() => parseWorkList({}), /JSON array/);
});

test('"dunnes" is a store the --store argument takes', () => {
  assert.equal(parseStoreArg(['--store', 'dunnes']), 'dunnes');
  assert.equal(parseStoreArg(['--store=dunnes']), 'dunnes');
  assert.throws(() => parseStoreArg(['--store', 'lidl']), /aldi\|dunnes\|tesco/);
});
