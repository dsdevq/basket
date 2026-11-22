import {computed, effect, inject, untracked} from '@angular/core';
import {ToastService} from '@lifekit-hq/ui';
import {patchState, signalStore, withComputed, withMethods, withState} from '@ngrx/signals';

import {SessionStore} from '../../core/auth/session.store';
import {HttpStatus, statusOf} from '../../core/http-status';
import {offlineCache} from '../../core/offline/offline-cache';
import type {StoreId} from '../prices/prices.model';
import {ListApi} from './list.api';
import type {ListItem} from './list.model';
import {newLocalId, type QueuedEdit, retarget, targetOf} from './list.queue';

interface ListState {
  readonly items: readonly ListItem[];
  readonly status: 'loading' | 'ready' | 'error';
  readonly adding: boolean;
  readonly closing: boolean;
  /** Edits made with no signal and not yet sent, oldest first. */
  readonly queued: readonly QueuedEdit[];
}

const MESSAGES = {
  load: 'Could not load the list. Check your connection and try again.',
  add: 'Could not add that. Try again.',
  update: 'Could not update that item. It is back as it was.',
  link: 'Could not link that line. Try again.',
  boughtAt: 'Could not change the shop. It is back as it was.',
  remove: 'Could not delete that item.',
  offline: (savedAt: string) =>
    `No signal. Showing the list as it was on ${new Date(savedAt).toLocaleString('en-IE', {
      weekday: 'short',
      hour: '2-digit',
      minute: '2-digit',
    })}. Changes need a connection.`,
  rejected: 'A change made without signal was not accepted. The list is as the server has it.',
  discarded: 'You were signed out, so the changes waiting to send were discarded.',
  waiting: 'Changes made without signal are still waiting to send. Try again once they have gone.',
  close: 'Could not finish the trip. The list is as it was. Try again.',
  closeChanged: 'The list just changed. Check it and tap Done shopping again.',
} as const;

interface Submitted {
  /** True when there was no signal (or edits were already waiting): the edit is kept and sent later. */
  readonly queued: boolean;
  /** The server's answer, when the edit was sent at once. */
  readonly item?: ListItem;
}

/**
 * The shared list. Changes show at once and are rolled back if the server refuses them. With no signal an edit is
 * kept on this phone instead and sent, in order, when the connection is back.
 */
export const ListStore = signalStore(
  {providedIn: 'root'},
  withState<ListState>({items: [], status: 'loading', adding: false, closing: false, queued: []}),
  withComputed(({items}) => ({
    toBuy: computed(() => items().filter(item => !item.checked)),
    done: computed(() => items().filter(item => item.checked)),
  })),
  withComputed(({queued}) => ({
    /** How many edits are kept on this phone, waiting for a signal. */
    waiting: computed(() => queued().length),
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
      // What this phone last knew of the list, kept for the next start with no signal; only once the list has loaded.
      const remember = (): void => {
        const me = session.user();
        if (me && store.status() === 'ready') {
          offlineCache.writeList(me.id, store.items());
        }
      };
      const setItems = (items: readonly ListItem[]): void => {
        changes++;
        patchState(store, {items});
        remember();
      };
      const replace = (next: ListItem): void =>
        setItems(store.items().map(item => (item.id === next.id ? next : item)));

      const setQueued = (queued: readonly QueuedEdit[]): void => {
        patchState(store, {queued});
        const me = session.user();
        if (me) {
          offlineCache.writeQueue(me.id, queued);
        }
      };
      // The waiting edits belong to the person signed in: read back for each person, and gone when they sign out.
      let hydratedFor: string | null = null;
      const hydrate = (): void => {
        const id = session.user()?.id ?? null;
        if (id !== hydratedFor) {
          if (hydratedFor && store.queued().length) {
            toast.info(MESSAGES.discarded);
          }
          hydratedFor = id;
          patchState(store, {queued: id ? offlineCache.readQueue<QueuedEdit>(id) : []});
        }
      };
      effect(() => {
        session.user();
        untracked(hydrate);
      });

      const run = (edit: QueuedEdit): Promise<ListItem | undefined> => {
        switch (edit.kind) {
          case 'add':
            return api.add(edit.text);
          case 'check':
            return api.setChecked(edit.id, edit.checked);
          case 'boughtAt':
            return api.setBoughtAt(edit.id, edit.store);
          case 'remove':
            return api.remove(edit.id).then(() => undefined);
        }
      };

      /** The queue without this edit; a refused add also takes the edits that depended on the line it would have made. */
      const withoutEdit = (edit: QueuedEdit, refused: boolean): readonly QueuedEdit[] =>
        store
          .queued()
          .filter(
            other =>
              other !== edit &&
              !(refused && edit.kind === 'add' && targetOf(other) === edit.localId)
          );

      /** Takes a sent edit off the queue and shows what the server answered. */
      const applySent = (edit: QueuedEdit, answer: ListItem | undefined): void => {
        setQueued(
          withoutEdit(edit, false).map(other =>
            edit.kind === 'add' && answer ? retarget(other, edit.localId, answer.id) : other
          )
        );
        if (edit.kind === 'add' && answer) {
          setItems(
            store
              .items()
              .filter(line => line.id !== answer.id)
              .map(line => (line.id === edit.localId ? answer : line))
          );
        } else if (answer && !store.queued().some(other => targetOf(other) === answer.id)) {
          // Skipped while a later edit to the same line is still waiting, so the line does not flicker back.
          replace(answer);
        }
      };

      // One replay at a time; a second caller waits for the one already running.
      let draining: Promise<boolean> | null = null;
      const drain = async (): Promise<boolean> => {
        let rejected = false;
        for (let edit = store.queued()[0]; edit; edit = store.queued()[0]) {
          try {
            applySent(edit, await run(edit));
          } catch (error) {
            const status = statusOf(error);
            if (
              status === 0 ||
              status === HttpStatus.unauthorized ||
              status === HttpStatus.tooManyRequests ||
              status >= HttpStatus.serverError
            ) {
              // Still no signal, session ended, or the server is busy: stop here and leave the queue as it is. (A session that ended is cleared with the sign-out, not kept.)
              break;
            }
            // A line that is already gone is as good as done; anything else the server refused is dropped.
            rejected ||= status !== HttpStatus.notFound;
            if (edit.kind === 'add') {
              setItems(store.items().filter(line => line.id !== edit.localId));
            }
            setQueued(withoutEdit(edit, true));
          }
        }
        if (rejected) {
          toast.error(MESSAGES.rejected);
        }
        return rejected;
      };
      /** Sends what was made with no signal, oldest first; stops at the first edit that still cannot go. Says whether the server refused one. */
      const flush = (): Promise<boolean> => {
        hydrate();
        draining ??= drain().finally(() => (draining = null));
        return draining;
      };

      /** Shows the list as this phone last saw it, when it has not loaded yet this session. */
      const showRemembered = (quiet: boolean): boolean => {
        const me = session.user();
        const cached =
          me && store.status() !== 'ready' ? offlineCache.readList<ListItem>(me.id) : null;
        if (!cached) {
          return false;
        }
        patchState(store, {items: cached.items, status: 'ready'});
        if (!quiet) {
          toast.info(MESSAGES.offline(cached.savedAt));
        }
        return true;
      };
      const fail = (quiet: boolean): void => {
        if (!quiet) {
          patchState(store, {status: store.items().length ? 'ready' : 'error'});
          toast.error(MESSAGES.load);
        }
      };

      /**
       * Fetches the list, after sending any edits made with no signal. `quiet` keeps what is on screen if the
       * call fails (the background refresh).
       */
      const load = async (quiet = false): Promise<void> => {
        await flush();
        if (store.queued().length) {
          // The server's list does not have these edits yet, so it must not replace what this phone shows.
          if (!showRemembered(quiet)) {
            fail(quiet);
          }
          return;
        }
        const seen = changes;
        try {
          const items = await api.items();
          patchState(store, changes === seen ? {items, status: 'ready'} : {status: 'ready'});
          remember();
        } catch (error) {
          // No answer at all and the list never loaded this session: show it as this phone last saw it.
          if (statusOf(error) === 0 && showRemembered(quiet)) {
            return;
          }
          fail(quiet);
        }
      };

      /** Sends the edit now, or keeps it for later when there is no signal or earlier edits are still waiting. */
      const send = async (edit: QueuedEdit): Promise<Submitted> => {
        hydrate();
        if (store.queued().length) {
          // Edits made earlier must reach the server first, so this one waits behind them.
          setQueued([...store.queued(), edit]);
          void load(true);
          return {queued: true};
        }
        try {
          return {queued: false, item: await run(edit)};
        } catch (error) {
          if (statusOf(error) !== 0) {
            throw error;
          }
          setQueued([...store.queued(), edit]);
          return {queued: true};
        }
      };
      // Edits go out one at a time, in the order they were made, so one that fails with no signal queues the next.
      let sending: Promise<unknown> = Promise.resolve();
      const submit = (edit: QueuedEdit): Promise<Submitted> => {
        const result = sending.then(() => send(edit));
        sending = result.catch(() => undefined);
        return result;
      };

      return {
        load,

        async add(text: string): Promise<boolean> {
          patchState(store, {adding: true});
          try {
            const localId = newLocalId();
            const {queued, item: created} = await submit({kind: 'add', localId, text});
            if (created) {
              setItems([...store.items().filter(item => item.id !== created.id), created]);
            } else if (queued) {
              // Shown at once with a local id; the server's line replaces it when the edit is sent.
              const me = session.user();
              setItems([
                ...store.items(),
                {
                  id: localId,
                  text,
                  checked: false,
                  addedAt: new Date().toISOString(),
                  addedBy: {id: me?.id ?? '', name: me?.displayName ?? ''},
                  checkedAt: null,
                  checkedBy: null,
                  productId: null,
                  store: null,
                  imageUrl: null,
                  boughtStore: null,
                },
              ]);
            }
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

        /**
         * Sets the shop of a product's line from the compare sheet. A line is one product at one shop, so a product already
         * waiting at another shop (or at none) is moved: the new line is added first and the old one removed only after the
         * server took it, so a failure leaves the list as it was. A product waiting at several shops gets the new line added
         * and none removed, because which of them is meant is not known. False when the line did not go through.
         */
        async setProductStore(productId: string, shop: StoreId | null): Promise<boolean> {
          const waiting = store.toBuy().filter(item => item.productId === productId);
          if (waiting.some(item => item.store === shop)) {
            return true;
          }
          if (!(await this.addProduct(productId, shop))) {
            return false;
          }
          if (waiting.length === 1 && waiting[0]) {
            await this.remove(waiting[0]);
          }
          return true;
        },

        async toggle(item: ListItem, checked: boolean): Promise<void> {
          const me = session.user();
          const optimistic: ListItem = {
            ...item,
            checked,
            checkedAt: checked ? new Date().toISOString() : null,
            checkedBy: checked && me ? {id: me.id, name: me.displayName} : null,
            // The server records the shop at the tick (the pick, else the plan's); until it answers, show the pick.
            boughtStore: checked ? (item.boughtStore ?? item.store) : null,
          };
          replace(optimistic);
          try {
            const {item: answer} = await submit({kind: 'check', id: item.id, checked});
            if (answer) {
              replace(answer);
            }
          } catch {
            replace(item);
            toast.error(MESSAGES.update);
          }
        },

        /**
         * Links a typed line to a product, keeping the line's words as another name of it when `remember` is set. The server's
         * own words explain a refusal (the product is already on the list, the words belong to another product); false then.
         */
        async linkProduct(item: ListItem, productId: string, remember: boolean): Promise<boolean> {
          try {
            replace(await api.linkProduct(item.id, productId, remember));
            return true;
          } catch (error) {
            const message = (error as {error?: {message?: string}} | null)?.error?.message;
            toast.error(
              statusOf(error) === HttpStatus.conflict && message ? message : MESSAGES.link
            );
            if (
              statusOf(error) === HttpStatus.notFound ||
              statusOf(error) === HttpStatus.conflict
            ) {
              void load(true);
            }
            return false;
          }
        },

        /** Changes where a ticked item was bought; shown at once and put back if the server refuses. */
        async setBoughtStore(item: ListItem, shop: StoreId): Promise<boolean> {
          replace({...item, boughtStore: shop});
          try {
            const {item: answer} = await submit({kind: 'boughtAt', id: item.id, store: shop});
            if (answer) {
              replace(answer);
            }
            return true;
          } catch {
            replace(item);
            toast.error(MESSAGES.boughtAt);
            return false;
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
            // The trip is whatever the server has ticked, so edits made with no signal must have gone first.
            const refused = await flush();
            if (store.queued().length) {
              toast.error(MESSAGES.waiting);
              return false;
            }
            if (refused) {
              // The screen still shows the refused edit, so the trip must not be filed from it.
              toast.error(MESSAGES.closeChanged);
              void load(true);
              return false;
            }
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
          hydrate();
          try {
            await submit({kind: 'remove', id: item.id});
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
