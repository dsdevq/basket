import type {Product} from '../prices/prices.model';

/** Lower case with the accents folded away, so "Crème" and "creme" are the same word. */
export function fold(text: string): string {
  return text.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase().replace(/\s+/g, ' ').trim();
}

/** Whether every word typed is found in the product's name or in one of its other names. An empty search matches every product. */
export function productMatches(product: Product, query: string): boolean {
  const words = fold(query).split(' ').filter(Boolean);
  if (!words.length) {
    return true;
  }
  const names = fold([product.name, ...(product.aliases ?? []).map(alias => alias.text)].join(' '));
  return words.every(word => names.includes(word));
}

/** The other name that made a product match what was typed, to show why a row with a different name is listed. */
export function matchedAlias(product: Product, query: string): string | null {
  const words = fold(query).split(' ').filter(Boolean);
  if (!words.length || words.every(word => fold(product.name).includes(word))) {
    return null;
  }
  return (
    (product.aliases ?? []).find(alias => words.every(word => fold(alias.text).includes(word)))
      ?.text ?? null
  );
}

/** Whether linking with these words would add a name: not the product's own name and not one it already has. */
export function wouldAddName(product: Product, words: string): boolean {
  const folded = fold(words);
  return (
    folded.length > 0 &&
    folded !== fold(product.name) &&
    !(product.aliases ?? []).some(alias => fold(alias.text) === folded)
  );
}
