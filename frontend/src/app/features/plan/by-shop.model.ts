import type {StoreId} from '../prices/prices.model';
import type {PlanBlockReason, PlanFlag} from './plan.model';

/** One line bought at one shop: the price of one pack when the deal is met, the deal behind it and the day the price was read. */
export interface ByShopLine {
  readonly itemId: string;
  readonly text: string;
  readonly price: number;
  readonly deal: string;
  readonly isPromo: boolean;
  /** The loyalty card the price needs; null when it needs none. */
  readonly card: string | null;
  readonly packSize: string | null;
  readonly unitPrice: number | null;
  readonly unit: 'kg' | 'l' | 'each' | null;
  readonly observedOn: string;
}

/** A line a shop cannot price (or the split cannot place), and why. */
export interface ByShopMissing {
  readonly itemId: string;
  readonly text: string;
  readonly reason: PlanBlockReason;
}

/** The whole list at one shop. `total` adds only the `priced` lines; a missing line adds nothing. */
export interface ByShopStore {
  readonly store: StoreId;
  readonly storeName: string;
  readonly priced: number;
  readonly total: number;
  /** Every line is priced here. */
  readonly complete: boolean;
  /** The lowest total among the complete shops. */
  readonly cheapest: boolean;
  /** The day of the oldest price in the total. */
  readonly oldestSeenOn: string | null;
  readonly promoLines: number;
  /** Priced lines whose pack is not the same at every shop that prices them. */
  readonly packsDiffer: number;
  readonly cards: readonly string[];
  readonly missing: readonly ByShopMissing[];
}

export interface ByShopGroup {
  readonly store: StoreId;
  readonly storeName: string;
  readonly total: number;
  readonly lines: readonly ByShopLine[];
}

export interface ByShopUnplaced {
  readonly itemId: string;
  readonly text: string;
  readonly reason: PlanFlag;
}

export interface ByShopSplit {
  readonly total: number;
  /** Every line has a shop. */
  readonly complete: boolean;
  /** The split's total minus the cheapest complete shop's (negative: the split is cheaper); null unless both exist. */
  readonly versusCheapest: number | null;
  readonly groups: readonly ByShopGroup[];
  readonly unplaced: readonly ByShopUnplaced[];
}

export interface ByShopResponse {
  readonly asOf: string;
  readonly staleAfterDays: number;
  readonly lineCount: number;
  readonly stores: readonly ByShopStore[];
  readonly split: ByShopSplit;
}
