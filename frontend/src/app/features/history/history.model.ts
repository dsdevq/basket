import type {Person} from '../list/list.model';

export interface TripItem {
  readonly id: string;
  readonly text: string;
  readonly checkedAt: string | null;
  /** Who ticked it; null once that person's account is gone. */
  readonly checkedBy: Person | null;
  /** The Shelf product the line was picked as; null for a typed one-off, which can be promoted to the Shelf. */
  readonly productId: string | null;
}

/** A shopping trip closed with "Done shopping". */
export interface Trip {
  readonly id: string;
  readonly closedAt: string;
  /** `lidl_plus` for a trip made from a Lidl Plus receipt; null for one closed on the list. */
  readonly source: 'lidl_plus' | null;
  /** What the receipt came to; null for a trip closed on the list, which keeps no prices. */
  readonly total: number | null;
  readonly items: readonly TripItem[];
}

export interface TripPage {
  readonly trips: readonly Trip[];
  readonly hasMore: boolean;
}

export interface ReloadResult {
  readonly added: number;
  readonly alreadyOnList: number;
}
