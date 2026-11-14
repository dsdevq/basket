/** A Shelf product and how often closed trips bought it. `usual` is the "usually bought" set. */
export interface Habit {
  readonly productId: string;
  readonly name: string;
  /** How many closed trips bought it. */
  readonly boughtIn: number;
  readonly lastBoughtAt: string;
  readonly usual: boolean;
}

export interface HabitsResponse {
  readonly products: readonly Habit[];
}
