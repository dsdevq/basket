import {computed, inject} from '@angular/core';
import {ToastService} from '@lifekit-hq/ui';
import {patchState, signalStore, withComputed, withMethods, withState} from '@ngrx/signals';

import {SessionStore} from '../../core/auth/session.store';
import {HttpStatus, statusOf} from '../../core/http-status';
import {ListApi} from './list.api';
import type {ListItem} from './list.model';

interface ListState {
  readonly items: readonly ListItem[];
  readonly status: 'loading' | 'ready' | 'error';
  readonly adding: boolean;
}

const MESSAGES = {
  load: 'Could not load the list. Pull down to try again.',
  add: 'Could not add that. Try again.',
  update: 'Could not update that item. It is back as it was.',
  remove: 'Could not delete that item.',
} as const;

/** The shared list. Changes show at once and are rolled back if the server refuses them. */
export const ListStore = signalStore(
  {providedIn: 'root'},
  withState<ListState>({items: [], status: 'loading', adding: false}),
  withComputed(({items}) => ({
    toBuy: computed(() => items().filter(item => !item.checked)),
    done: computed(() => items().filter(item => item.checked)),
  })),
  withMethods(
    (
      store,
      api = inject(ListApi),
      toast = inject(ToastService),
      session = inject(SessionStore)
    ) => {
      const replace = (next: ListItem): void =>
        patchState(store, {items: store.items().map(item => (item.id === next.id ? next : item))});

      return {
        /** Fetches the list. `quiet` keeps what is on screen if the call fails (the background refresh). */
        async load(quiet = false): Promise<void> {
          try {
            patchState(store, {items: await api.items(), status: 'ready'});
          } catch {
            if (!quiet) {
              patchState(store, {status: store.items().length ? 'ready' : 'error'});
              toast.error(MESSAGES.load);
            }
          }
        },

        async add(text: string): Promise<boolean> {
          patchState(store, {adding: true});
          try {
            const created = await api.add(text);
            patchState(store, {
              items: [...store.items().filter(item => item.id !== created.id), created],
            });
            return true;
          } catch {
            toast.error(MESSAGES.add);
            return false;
          } finally {
            patchState(store, {adding: false});
          }
        },

        async toggle(item: ListItem, checked: boolean): Promise<void> {
          const me = session.user();
          const optimistic: ListItem = {
            ...item,
            checked,
            checkedAt: checked ? new Date().toISOString() : null,
            checkedBy: checked && me ? {id: me.id, name: me.displayName} : null,
          };
          replace(optimistic);
          try {
            replace(await api.setChecked(item.id, checked));
          } catch {
            replace(item);
            toast.error(MESSAGES.update);
          }
        },

        async remove(item: ListItem): Promise<void> {
          const before = store.items();
          patchState(store, {items: before.filter(candidate => candidate.id !== item.id)});
          try {
            await api.remove(item.id);
          } catch (error) {
            if (statusOf(error) !== HttpStatus.notFound) {
              patchState(store, {items: before});
              toast.error(MESSAGES.remove);
            }
          }
        },
      };
    }
  )
);
