import {inject} from '@angular/core';
import {ToastService} from '@lifekit-hq/ui';
import {patchState, signalStore, withMethods, withState} from '@ngrx/signals';

import {HttpStatus, statusOf} from '../../core/http-status';
import type {CatalogItem} from '../catalog/catalog.model';
import {PricesApi} from './prices.api';
import type {Alias, Candidate, Product, StoreId, StoreStatus} from './prices.model';

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
  catalog: 'Could not add that product. Try again.',
  remove: 'Could not delete that product.',
  unlink: 'Could not remove that link.',
  candidates: 'Could not load the suggestions. Try again.',
  decide: 'Could not save that answer. Try again.',
  alias: 'Could not save that name. Try again.',
  answered: 'Someone already answered that suggestion. Check the list again.',
} as const;

/** The household's products with the latest price each shop showed for them. */
export const PricesStore = signalStore(
  {providedIn: 'root'},
  withState<PricesState>({products: [], stores: [], status: 'loading', adding: false}),
  withMethods((store, api = inject(PricesApi), toast = inject(ToastService)) => {
    // Counts loads and local writes to `products`; a response that is no longer the latest of them is stale and dropped.
    let changes = 0;
    /** The server's words when it refused a name (taken, too many, the product's own), else a plain failure. */
    const aliasRefusal = (error: unknown): string => {
      const message = (error as {error?: {message?: string}} | null)?.error?.message;
      const refused =
        statusOf(error) === HttpStatus.badRequest || statusOf(error) === HttpStatus.conflict;
      return refused && message ? message : MESSAGES.alias;
    };
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

      /**
       * A tapped catalog row: one call, so the product is never added without its page. The server names the product from the row,
       * takes its barcode, links the row's page and photo in its shop as the person's choice, and looks for the other shops' pages.
       * Returns whether the product is now on the shelf.
       */
      async addFromCatalog(item: CatalogItem): Promise<boolean> {
        patchState(store, {adding: true});
        try {
          await api.addFromCatalog(item.id);
          await this.load(true);
          return true;
        } catch (error) {
          // A refusal (already tracked, no longer listed, barcode taken) says why in the server's words.
          const message = (error as {error?: {message?: string}} | null)?.error?.message;
          toast.error(
            statusOf(error) === HttpStatus.conflict && message ? message : MESSAGES.catalog
          );
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

      /** Adds another name to a product; returns the server's refusal text (taken, too many, the product's own name) or null when saved. */
      async addAlias(product: Product, text: string): Promise<string | null> {
        try {
          await api.addAlias(product.id, text);
          await this.load(true);
          return null;
        } catch (error) {
          return aliasRefusal(error);
        }
      },

      async renameAlias(product: Product, alias: Alias, text: string): Promise<string | null> {
        try {
          await api.renameAlias(product.id, alias.id, text);
          await this.load(true);
          return null;
        } catch (error) {
          return aliasRefusal(error);
        }
      },

      async removeAlias(product: Product, alias: Alias): Promise<boolean> {
        try {
          await api.removeAlias(product.id, alias.id);
        } catch (error) {
          if (statusOf(error) !== HttpStatus.notFound) {
            toast.error(MESSAGES.alias);
            return false;
          }
        }
        await this.load(true);
        return true;
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

      /** Yes: the page becomes the product's confirmed page in that shop. False when it could not be saved, including when the suggestion was answered meanwhile. */
      async confirm(product: Product, candidate: Candidate): Promise<boolean> {
        try {
          await api.confirm(product.id, candidate.id);
        } catch (error) {
          if (statusOf(error) === HttpStatus.notFound) {
            toast.error(MESSAGES.answered);
            await this.load(true);
          } else {
            toast.error(MESSAGES.decide);
          }
          return false;
        }
        await this.load(true);
        return true;
      },

      /** No: the page is never suggested again. False when it could not be saved. A 404 means the other person already answered it, so it counts as done. */
      async reject(product: Product, candidate: Candidate): Promise<boolean> {
        try {
          await api.reject(product.id, candidate.id);
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
