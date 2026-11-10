import type {StoreId} from '../prices/prices.model';
import type {PromoOffer} from '../promotions/promotions.model';

/** Why a line has no best shop; absent when it has one. */
export type PlanFlag = 'unmatched' | 'no_price' | 'stale' | 'out_of_stock' | 'not_comparable';

/** What one shop can say about a line. Only `priced` counts towards covering the list. */
export type PlanStoreState =
  'priced' | 'stale' | 'out_of_stock' | 'not_read' | 'no_price' | 'upcoming' | 'ended';

/** Why a line keeps one shop from covering the list: the line's flag, else that shop's own state, else it has no page there. */
export type PlanBlockReason = PlanFlag | Exclude<PlanStoreState, 'priced'> | 'not_linked';

/** One linked shop for one line: the reading itself (`price`, `wasPrice`, `promoText`) and the normalised `offer`. */
export interface PlanStorePrice {
  readonly store: StoreId;
  readonly storeName: string;
  readonly state: PlanStoreState;
  readonly observedOn: string | null;
  readonly price: number | null;
  readonly wasPrice: number | null;
  readonly promoText: string | null;
  readonly offer: PromoOffer | null;
  readonly best: boolean;
}

/** One open line of the list. */
export interface PlanItem {
  readonly id: string;
  readonly text: string;
  readonly productId: string | null;
  readonly productName: string | null;
  /** The shop the line was picked at on the Shelf; it does not steer the answer. */
  readonly pickedStore: StoreId | null;
  readonly flag: PlanFlag | null;
  readonly bestStore: StoreId | null;
  readonly stores: readonly PlanStorePrice[];
}

export interface PlanBlocker {
  readonly itemId: string;
  readonly text: string;
  readonly reason: PlanBlockReason;
}

export interface PlanCoverage {
  /** True when at least one shop prices every open line. */
  readonly singleStore: boolean;
  /** The one that is best for the most lines, when `singleStore`. */
  readonly store: StoreId | null;
  readonly coveredBy: readonly StoreId[];
  /** When not `singleStore`: the shop pricing the most lines (null when none prices any). */
  readonly closestStore: StoreId | null;
  readonly blockers: readonly PlanBlocker[];
}

export interface PlanResponse {
  readonly asOf: string;
  readonly staleAfterDays: number;
  readonly items: readonly PlanItem[];
  readonly coverage: PlanCoverage;
}
