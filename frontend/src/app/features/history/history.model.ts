import type {Person} from '../list/list.model';

export interface TripItem {
  readonly id: string;
  readonly text: string;
  readonly checkedAt: string | null;
  /** Who ticked it; null once that person's account is gone. */
  readonly checkedBy: Person | null;
}

/** A shopping trip closed with "Done shopping". */
export interface Trip {
  readonly id: string;
  readonly closedAt: string;
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
