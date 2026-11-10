import {inject} from '@angular/core';
import {ToastService} from '@lifekit-hq/ui';
import {patchState, signalStore, withMethods, withState} from '@ngrx/signals';

import {HistoryApi} from './history.api';
import type {ReloadResult, Trip} from './history.model';

interface HistoryState {
  readonly trips: readonly Trip[];
  readonly hasMore: boolean;
  readonly status: 'loading' | 'ready' | 'error';
  readonly loadingMore: boolean;
  /** The trip being put back on the list, so its button shows progress and cannot be tapped twice. */
  readonly reloading: string | null;
}

const MESSAGES = {
  load: 'Could not load the history. Check your connection and try again.',
  more: 'Could not load older trips. Try again.',
  reload: 'Could not put that trip on the list. Try again.',
} as const;

/** What the toast says after "Same as last time". */
export function reloadMessage({added, alreadyOnList}: ReloadResult): string {
  if (!added) {
    return 'Everything from that trip is already on the list';
  }
  const items = added === 1 ? '1 item' : `${added} items`;
  return alreadyOnList ? `Added ${items}. The rest was already on the list` : `Added ${items}`;
}

/** The closed trips, newest first, a page at a time. */
export const HistoryStore = signalStore(
  {providedIn: 'root'},
  withState<HistoryState>({
    trips: [],
    hasMore: false,
    status: 'loading',
    loadingMore: false,
    reloading: null,
  }),
  withMethods((store, api = inject(HistoryApi), toast = inject(ToastService)) => {
    // Counts loads that start from the top; an older page that lands after one must not be appended to it.
    let generation = 0;

    return {
      /** Fetches the newest page. It keeps what is on screen if a repeat load fails. */
      async load(): Promise<void> {
        const mine = ++generation;
        try {
          const page = await api.trips();
          if (mine === generation) {
            patchState(store, {
              trips: page.trips,
              hasMore: page.hasMore,
              status: 'ready',
              loadingMore: false,
            });
          }
        } catch {
          if (mine === generation) {
            patchState(store, {status: store.trips().length ? 'ready' : 'error'});
            toast.error(MESSAGES.load);
          }
        }
      },

      async loadMore(): Promise<void> {
        const last = store.trips().at(-1);
        if (!last || !store.hasMore() || store.loadingMore()) {
          return;
        }
        const mine = generation;
        patchState(store, {loadingMore: true});
        try {
          const page = await api.trips(last.closedAt);
          if (mine === generation) {
            const seen = new Set(store.trips().map(trip => trip.id));
            patchState(store, {
              trips: [...store.trips(), ...page.trips.filter(trip => !seen.has(trip.id))],
              hasMore: page.hasMore,
            });
          }
        } catch {
          toast.error(MESSAGES.more);
        } finally {
          if (mine === generation) {
            patchState(store, {loadingMore: false});
          }
        }
      },

      /** Puts the trip's items back on the list. True when the list now has them (so the page can show it). */
      async reload(trip: Trip): Promise<boolean> {
        if (store.reloading()) {
          return false;
        }
        patchState(store, {reloading: trip.id});
        try {
          toast.success(reloadMessage(await api.reload(trip.id)));
          return true;
        } catch {
          toast.error(MESSAGES.reload);
          return false;
        } finally {
          patchState(store, {reloading: null});
        }
      },
    };
  })
);
