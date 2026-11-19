import type {Habit} from '../habits/habits.model';
import type {Product, ProductStore} from './prices.model';

/** One proposal waiting for a person: a shop's suggested pages for one product. */
export interface ToCheckRow {
  readonly product: Product;
  readonly entry: ProductStore;
}

/**
 * Every (product, shop) with suggested pages nobody has answered, the most-bought product first. Closed trips rank products
 * (most trips, then the latest trip); a product never bought comes after those, in the Shelf's own order.
 */
export function toCheckRows(
  products: readonly Product[],
  habits: ReadonlyMap<string, Habit>
): ToCheckRow[] {
  const rows = products.flatMap(product =>
    product.stores.filter(entry => (entry.candidates ?? 0) > 0).map(entry => ({product, entry}))
  );
  return rows
    .map((row, index) => ({row, index, habit: habits.get(row.product.id)}))
    .sort(
      (a, b) =>
        (b.habit?.boughtIn ?? 0) - (a.habit?.boughtIn ?? 0) ||
        (b.habit?.lastBoughtAt ?? '').localeCompare(a.habit?.lastBoughtAt ?? '') ||
        a.index - b.index
    )
    .map(({row}) => row);
}

/** A shop row with no page and no suggestion: nothing to read or answer, so the page folds it behind a count. */
export function isUnlinked(entry: ProductStore): boolean {
  return !entry.listing && (entry.candidates ?? 0) <= 0;
}

/** "1 shop with no page linked yet" / "3 shops with no page linked yet". */
export function unlinkedText(count: number): string {
  return `${count} ${count === 1 ? 'shop' : 'shops'} with no page linked yet`;
}
