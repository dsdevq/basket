import type {Product} from '../prices/prices.model';
import {fold, matchedAlias, productMatches, wouldAddName} from './link-format';

const EGGS: Product = {
  id: 'p1',
  name: 'Free Range Eggs',
  stores: [],
  aliases: [
    {id: 'a1', text: 'Яйця'},
    {id: 'a2', text: 'Œufs frais'},
  ],
};

describe('link-format', () => {
  it('folds case and accents', () => {
    expect(fold('  Crème   BRÛLÉE ')).toBe('creme brulee');
  });

  it('matches a product by its name or by any other name, every typed word having to be found', () => {
    expect(productMatches(EGGS, '')).toBe(true);
    expect(productMatches(EGGS, 'free eggs')).toBe(true);
    expect(productMatches(EGGS, 'яйця')).toBe(true);
    expect(productMatches(EGGS, 'oeufs')).toBe(false);
    expect(productMatches(EGGS, 'oeufs frais')).toBe(false);
    expect(productMatches(EGGS, 'œufs')).toBe(true);
    expect(productMatches(EGGS, 'milk')).toBe(false);
    expect(productMatches({id: 'p2', name: 'Milk', stores: []}, 'milk')).toBe(true);
  });

  it('names the other name that made a product match, only when its own name did not', () => {
    expect(matchedAlias(EGGS, 'яй')).toBe('Яйця');
    expect(matchedAlias(EGGS, 'eggs')).toBeNull();
    expect(matchedAlias(EGGS, '')).toBeNull();
    expect(matchedAlias(EGGS, 'zzz')).toBeNull();
  });

  it('adds a name only when the words are not the product name and not a name it already has', () => {
    expect(wouldAddName(EGGS, 'Яйця курячі')).toBe(true);
    expect(wouldAddName(EGGS, ' яйця ')).toBe(false);
    expect(wouldAddName(EGGS, 'free range EGGS')).toBe(false);
    expect(wouldAddName(EGGS, '  ')).toBe(false);
  });
});
