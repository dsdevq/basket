import {inject} from '@angular/core';
import {ToastService} from '@lifekit-hq/ui';
import {patchState, signalStore, withMethods, withState} from '@ngrx/signals';

import {PlanApi} from './plan.api';
import type {PlanCoverage, PlanItem} from './plan.model';

interface PlanState {
  readonly asOf: string | null;
  readonly staleAfterDays: number;
  readonly items: readonly PlanItem[];
  readonly coverage: PlanCoverage | null;
  readonly status: 'loading' | 'ready' | 'error';
}

/** The server's limit until it says (the note shows it before the first answer). */
const DEFAULT_STALE_DAYS = 3;

const LOAD_FAILED = 'Could not load the plan. Try again.';

/** The plan for the open lines of the list: the best shop per line and whether one shop covers it all, as the server worked it out. */
export const PlanStore = signalStore(
  {providedIn: 'root'},
  withState<PlanState>({
    asOf: null,
    staleAfterDays: DEFAULT_STALE_DAYS,
    items: [],
    coverage: null,
    status: 'loading',
  }),
  withMethods((store, api = inject(PlanApi), toast = inject(ToastService)) => ({
    async load(quiet = false): Promise<void> {
      try {
        const plan = await api.get();
        patchState(store, {
          asOf: plan.asOf,
          staleAfterDays: plan.staleAfterDays,
          items: plan.items,
          coverage: plan.coverage,
          status: 'ready',
        });
      } catch {
        if (!quiet) {
          patchState(store, {status: store.coverage() ? 'ready' : 'error'});
          toast.error(LOAD_FAILED);
        }
      }
    },
  }))
);
