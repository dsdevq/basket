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
  /**
   * Where a ticked line was bought, as recorded in the open trip: the shop it was picked from, else the one the plan suggested,
   * until the person changes it. Null while the line is not ticked, or when no shop was known.
   */
  readonly boughtStore: StoreId | null;
  /** How many packs to buy (2 to 99); null is one. The Plan multiplies its totals by it. */
  readonly quantity: number | null;
  /** A free-text note on the line ("the green ones"); null when there is none. */
  readonly note: string | null;
}

/** The most packs of one line, as the server accepts. */
export const MAX_QUANTITY = 99;

/** The longest note, as the server accepts. */
export const MAX_NOTE_LENGTH = 200;
