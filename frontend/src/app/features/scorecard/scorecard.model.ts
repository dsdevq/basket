import type {StoreId} from '../prices/prices.model';

/** One shop's wins among the compared lines, and its share of them (0-100). */
export interface ScorecardStore {
  readonly store: StoreId;
  readonly storeName: string;
  readonly wins: number;
  readonly share: number;
}

/** One month ("2026-11") of closed trips: the lines compared and what the winners saved over the dearest shop. */
export interface ScorecardMonth {
  readonly month: string;
  readonly trips: number;
  readonly compared: number;
  readonly saved: number;
}

/** GET /api/scorecard: the shops scored on closed trips of the last `months` months, priced as on each trip's day. */
export interface ScorecardResponse {
  readonly asOf: string;
  /** A reading older than this many days at the time of a trip is left out of the comparison. */
  readonly staleAfterDays: number;
  readonly months: number;
  readonly trips: number;
  readonly compared: number;
  readonly notCompared: number;
  readonly saved: number;
  readonly topStore: StoreId | null;
  readonly stores: readonly ScorecardStore[];
  readonly trend: readonly ScorecardMonth[];
}
