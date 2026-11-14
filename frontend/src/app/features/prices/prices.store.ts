import {inject} from '@angular/core';
import {ToastService} from '@lifekit-hq/ui';
import {patchState, signalStore, withMethods, withState} from '@ngrx/signals';

import {HttpStatus, statusOf} from '../../core/http-status';
import {PricesApi} from './prices.api';
import type {Candidate, Product, StoreId, StoreStatus} from './prices.model';

interface PricesState {
  readonly products: readonly Product[];
  readonly stores: readonly StoreStatus[];
  readonly status: 'loading' | 'ready' | 'error';
  readonly adding: boolean;
}

const MESSAGES = {
  load: 'Could not load the prices. Try again.',
  add: 'Could not add that. Try again.',
  duplicate: 'That product is already tracked.',
  remove: 'Could not delete that product.',
  unlink: 'Could not remove that link.',
  candidates: 'Could not load the suggestions. Try again.',
  decide: 'Could not save that answer. Try again.',
} as const;

/** The household's products with the latest price each shop showed for them. */
export const PricesStore = signalStore(
  {providedIn: 'root'},
  withState<PricesState>({products: [], stores: [], status: 'loading', adding: false}),
  withMethods((store, api = inject(PricesApi), toast = inject(ToastService)) => {
    // Counts loads and local writes to `products`; a response that is no longer the latest of them is stale and dropped.
    let changes = 0;
    return {
      async load(quiet = false): Promise<void> {
        const seen = ++changes;
        try {
          const all = await api.all();
          patchState(
            store,
            changes === seen
              ? {products: all.products, stores: all.stores, status: 'ready'}
              : {status: 'ready'}
          );
        } catch {
          if (!quiet) {
            patchState(store, {status: store.products().length ? 'ready' : 'error'});
            toast.error(MESSAGES.load);
          }
        }
      },

      async add(name: string): Promise<boolean> {
        patchState(store, {adding: true});
        try {
          await api.add(name);
          await this.load(true);
          return true;
        } catch (error) {
          toast.error(statusOf(error) === HttpStatus.conflict ? MESSAGES.duplicate : MESSAGES.add);
          return false;
        } finally {
          patchState(store, {adding: false});
        }
      },

      async remove(product: Product): Promise<void> {
        const before = store.products();
        changes++;
        patchState(store, {products: before.filter(candidate => candidate.id !== product.id)});
        try {
          await api.remove(product.id);
        } catch (error) {
          if (statusOf(error) !== HttpStatus.notFound) {
            changes++;
            patchState(store, {products: before});
            toast.error(MESSAGES.remove);
          }
        }
      },

      /** Returns the server's refusal text for a bad address, or null when it was saved. */
      async link(product: Product, shop: StoreId, url: string): Promise<string | null> {
        try {
          await api.link(product.id, shop, url);
          await this.load(true);
          return null;
        } catch (error) {
          const message = (error as {error?: {message?: string}} | null)?.error?.message;
          return statusOf(error) === HttpStatus.badRequest && message
            ? message
            : 'Could not save that link. Try again.';
        }
      },

      async unlink(product: Product, shop: StoreId): Promise<void> {
        try {
          await api.unlink(product.id, shop);
          await this.load(true);
        } catch (error) {
          if (statusOf(error) !== HttpStatus.notFound) {
            toast.error(MESSAGES.unlink);
          }
        }
      },

      /** The pages suggested for a product in one shop, likeliest first; null when they could not be loaded. */
      async candidates(product: Product, shop: StoreId): Promise<readonly Candidate[] | null> {
        try {
          const {candidates} = await api.candidates(product.id);
          return candidates.filter(candidate => candidate.store === shop);
        } catch {
          toast.error(MESSAGES.candidates);
          return null;
        }
      },

      /** Yes: the page becomes the product's confirmed page in that shop. False when it could not be saved. */
      async confirm(product: Product, candidate: Candidate): Promise<boolean> {
        return this.decide(product, () => api.confirm(product.id, candidate.id));
      },

      /** No: the page is never suggested again. False when it could not be saved. */
      async reject(product: Product, candidate: Candidate): Promise<boolean> {
        return this.decide(product, () => api.reject(product.id, candidate.id));
      },

      // A 404 means the other person already answered it (or the page was linked meanwhile): the answer is persisted, so reload.
      async decide(product: Product, call: () => Promise<unknown>): Promise<boolean> {
        try {
          await call();
        } catch (error) {
          if (statusOf(error) !== HttpStatus.notFound) {
            toast.error(MESSAGES.decide);
            return false;
          }
        }
        await this.load(true);
        return true;
      },
    };
  })
);
