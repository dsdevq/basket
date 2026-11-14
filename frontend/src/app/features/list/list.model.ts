import type {StoreId} from '../prices/prices.model';

export interface Person {
  readonly id: string;
  readonly name: string;
}

export interface ListItem {
  readonly id: string;
  readonly text: string;
  readonly checked: boolean;
  readonly addedAt: string;
  readonly addedBy: Person;
  readonly checkedAt: string | null;
  readonly checkedBy: Person | null;
  /** The product this line was picked as on the Shelf; null for a free-text line. */
  readonly productId: string | null;
  /** The shop it was picked from; null for a free-text line or a pick from "All". */
  readonly store: StoreId | null;
  /** The product's photo address from its shop page; null for a free-text line or a product with no photo. */
  readonly imageUrl: string | null;
}
