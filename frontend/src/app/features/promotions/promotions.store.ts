import {inject} from '@angular/core';
import {ToastService} from '@lifekit-hq/ui';
import {patchState, signalStore, withMethods, withState} from '@ngrx/signals';

import {PromotionsApi} from './promotions.api';
import type {PromoProduct, UsualProduct} from './promotions.model';

interface PromotionsState {
  readonly products: readonly PromoProduct[];
  readonly usual: readonly UsualProduct[];
  readonly staleAfterDays: number;
  readonly status: 'loading' | 'ready' | 'error';
}

/** The server's limit until it says (the rule shows it before the first answer). */
const DEFAULT_STALE_DAYS = 3;

const LOAD_FAILED = 'Could not load the promotions. Try again.';

/** The current promotions of every tracked product, ranked by the server. */
export const PromotionsStore = signalStore(
  {providedIn: 'root'},
  withState<PromotionsState>({
    products: [],
    usual: [],
    staleAfterDays: DEFAULT_STALE_DAYS,
    status: 'loading',
  }),
  withMethods((store, api = inject(PromotionsApi), toast = inject(ToastService)) => ({
    async load(quiet = false): Promise<void> {
      try {
        // The usual-products view is an addition: when only it fails the promotions still show.
        const [all, usual] = await Promise.all([api.all(), api.usual().catch(() => null)]);
        patchState(store, {
          products: all.products,
          usual: usual?.products ?? store.usual(),
          staleAfterDays: all.staleAfterDays,
          status: 'ready',
        });
      } catch {
        if (!quiet) {
          patchState(store, {status: store.products().length ? 'ready' : 'error'});
          toast.error(LOAD_FAILED);
        }
      }
    },
  }))
);
