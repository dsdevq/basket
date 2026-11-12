import {computed, inject} from '@angular/core';
import {ToastService} from '@lifekit-hq/ui';
import {patchState, signalStore, withComputed, withMethods, withState} from '@ngrx/signals';

import {SessionStore} from '../../core/auth/session.store';
import {HttpStatus, statusOf} from '../../core/http-status';
import type {StoreId} from '../prices/prices.model';
import {ListApi} from './list.api';
import type {ListItem} from './list.model';

interface ListState {
  readonly items: readonly ListItem[];
  readonly status: 'loading' | 'ready' | 'error';
  readonly adding: boolean;
  readonly closing: boolean;
}

const MESSAGES = {
  load: 'Could not load the list. Check your connection and try again.',
  add: 'Could not add that. Try again.',
  update: 'Could not update that item. It is back as it was.',
  remove: 'Could not delete that item.',
  close: 'Could not finish the trip. The list is as it was. Try again.',
  closeChanged: 'The list just changed. Check it and tap Done shopping again.',
} as const;

/** The shared list. Changes show at once and are rolled back if the server refuses them. */
export const ListStore = signalStore(
  {providedIn: 'root'},
  withState<ListState>({items: [], status: 'loading', adding: false, closing: false}),
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
      // Counts local writes to `items`; a refresh that started before a write is stale and must not overwrite it.
      let changes = 0;
      const setItems = (items: readonly ListItem[]): void => {
        changes++;
        patchState(store, {items});
      };
      const replace = (next: ListItem): void =>
        setItems(store.items().map(item => (item.id === next.id ? next : item)));

      /** Fetches the list. `quiet` keeps what is on screen if the call fails (the background refresh). */
      const load = async (quiet = false): Promise<void> => {
        const seen = changes;
        try {
          const items = await api.items();
          patchState(store, changes === seen ? {items, status: 'ready'} : {status: 'ready'});
        } catch {
          if (!quiet) {
            patchState(store, {status: store.items().length ? 'ready' : 'error'});
            toast.error(MESSAGES.load);
          }
        }
      };

      return {
        load,

        async add(text: string): Promise<boolean> {
          patchState(store, {adding: true});
          try {
            const created = await api.add(text);
            setItems([...store.items().filter(item => item.id !== created.id), created]);
            return true;
          } catch {
            toast.error(MESSAGES.add);
            return false;
          } finally {
            patchState(store, {adding: false});
          }
        },

        /** Puts a Shelf pick on the list (one line per product and shop); false when it did not go through. */
        async addProduct(productId: string, shop: StoreId | null): Promise<boolean> {
          try {
            const line = await api.addProduct(productId, shop);
            setItems([...store.items().filter(item => item.id !== line.id), line]);
            return true;
          } catch {
            toast.error(MESSAGES.add);
            return false;
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

        /**
         * Done shopping: the server files the ticked items as a trip and takes them off the list. The list is
         * emptied here only after it said yes, then re-read, so what stays is what the server kept.
         */
        async closeTrip(): Promise<boolean> {
          if (store.closing()) {
            return false;
          }
          patchState(store, {closing: true});
          try {
            await api.close();
            setItems(store.items().filter(item => !item.checked));
            toast.success('Trip saved to History');
            void load(true);
            return true;
          } catch (error) {
            if (statusOf(error) === HttpStatus.conflict) {
              toast.error(MESSAGES.closeChanged);
              void load(true);
            } else {
              toast.error(MESSAGES.close);
            }
            return false;
          } finally {
            patchState(store, {closing: false});
          }
        },

        async remove(item: ListItem): Promise<void> {
          const before = store.items();
          setItems(before.filter(candidate => candidate.id !== item.id));
          try {
            await api.remove(item.id);
          } catch (error) {
            if (statusOf(error) !== HttpStatus.notFound) {
              setItems(before);
              toast.error(MESSAGES.remove);
            }
          }
        },
      };
    }
  )
);
