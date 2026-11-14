import {computed, inject} from '@angular/core';
import {patchState, signalStore, withComputed, withMethods, withState} from '@ngrx/signals';

import {HabitsApi} from './habits.api';
import type {Habit} from './habits.model';

interface HabitsState {
  readonly habits: readonly Habit[];
}

/**
 * What closed trips say the household buys most and usually. It only decorates the Shelf and the basket, so a failed read
 * keeps what is on screen and says nothing: the pages work the same without it.
 */
export const HabitsStore = signalStore(
  {providedIn: 'root'},
  withState<HabitsState>({habits: []}),
  withComputed(({habits}) => ({
    usual: computed(() => habits().filter(habit => habit.usual)),
    byProduct: computed(() => new Map(habits().map(habit => [habit.productId, habit]))),
  })),
  withMethods((store, api = inject(HabitsApi)) => ({
    async load(): Promise<void> {
      try {
        patchState(store, {habits: await api.all()});
      } catch {
        // Keep the last read; the pages do not depend on it.
      }
    },
  }))
);
