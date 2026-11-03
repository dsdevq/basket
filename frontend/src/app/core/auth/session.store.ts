import {computed, inject} from '@angular/core';
import {patchState, signalStore, withComputed, withMethods, withState} from '@ngrx/signals';

import {AuthApi} from './auth.api';
import type {SessionUser} from './session.model';

interface SessionState {
  readonly loaded: boolean;
  readonly user: SessionUser | null;
}

/** Who is signed in. Loaded once from `/api/session`; sign-in and registration set it directly. */
export const SessionStore = signalStore(
  {providedIn: 'root'},
  withState<SessionState>({loaded: false, user: null}),
  withComputed(({user}) => ({
    isSignedIn: computed(() => user() !== null),
    isOwner: computed(() => user()?.role === 'owner'),
  })),
  withMethods((store, api = inject(AuthApi)) => ({
    /** Resolves once the first answer is in; a failed call counts as signed out. */
    async ensureLoaded(): Promise<void> {
      if (store.loaded()) {
        return;
      }
      try {
        const session = await api.session();
        patchState(store, {loaded: true, user: session.user});
      } catch {
        patchState(store, {loaded: true, user: null});
      }
    },
    async refresh(): Promise<void> {
      const session = await api.session();
      patchState(store, {loaded: true, user: session.user});
    },
    setUser(user: SessionUser | null): void {
      patchState(store, {loaded: true, user});
    },
    async signOut(): Promise<void> {
      try {
        await api.signOut();
      } finally {
        patchState(store, {user: null});
      }
    },
  }))
);
