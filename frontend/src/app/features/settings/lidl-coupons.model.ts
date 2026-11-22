/** One Lidl Plus coupon. `counts` is true when it is switched on and in date, so it changes a Lidl price. */
export interface LidlCoupon {
  readonly id: string;
  readonly title: string;
  readonly discount: string | null;
  readonly description: string | null;
  readonly validFrom: string | null;
  readonly validUntil: string | null;
  readonly activated: boolean;
  readonly counts: boolean;
}

/** The answer of every coupon route. `fresh` is false when Lidl could not be asked and this is the last list basket kept. */
export interface LidlCoupons {
  readonly state: 'not_connected' | 'connected' | 'needs_new_token';
  readonly fresh: boolean;
  readonly coupons: readonly LidlCoupon[];
  readonly activated: number;
  readonly refused: number;
}
