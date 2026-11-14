import {inject} from '@angular/core';
import {ToastService} from '@lifekit-hq/ui';
import {patchState, signalStore, withMethods, withState} from '@ngrx/signals';

import {ScorecardApi} from './scorecard.api';
import type {ScorecardResponse} from './scorecard.model';

interface ScorecardState {
  readonly card: ScorecardResponse | null;
  readonly status: 'loading' | 'ready' | 'error';
}

const LOAD_FAILED = 'Could not load the scorecard. Try again.';

/** The store scorecard as the server scored it from closed trips; read when the page opens, never pushed. */
export const ScorecardStore = signalStore(
  {providedIn: 'root'},
  withState<ScorecardState>({card: null, status: 'loading'}),
  withMethods((store, api = inject(ScorecardApi), toast = inject(ToastService)) => ({
    async load(quiet = false): Promise<void> {
      try {
        patchState(store, {card: await api.get(), status: 'ready'});
      } catch {
        if (!quiet) {
          patchState(store, {status: store.card() ? 'ready' : 'error'});
          toast.error(LOAD_FAILED);
        }
      }
    },
  }))
);
