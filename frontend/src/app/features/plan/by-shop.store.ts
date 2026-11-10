import {inject} from '@angular/core';
import {ToastService} from '@lifekit-hq/ui';
import {patchState, signalStore, withMethods, withState} from '@ngrx/signals';

import {ByShopApi} from './by-shop.api';
import type {ByShopResponse} from './by-shop.model';

interface ByShopState {
  readonly basket: ByShopResponse | null;
  readonly status: 'loading' | 'ready' | 'error';
}

const LOAD_FAILED = 'Could not load the shop totals. Try again.';

/** The open list priced at each shop and split at the cheapest, as the server summed it from the plan. */
export const ByShopStore = signalStore(
  {providedIn: 'root'},
  withState<ByShopState>({basket: null, status: 'loading'}),
  withMethods((store, api = inject(ByShopApi), toast = inject(ToastService)) => ({
    async load(quiet = false): Promise<void> {
      try {
        patchState(store, {basket: await api.get(), status: 'ready'});
      } catch {
        if (!quiet) {
          patchState(store, {status: store.basket() ? 'ready' : 'error'});
          toast.error(LOAD_FAILED);
        }
      }
    },
  }))
);
