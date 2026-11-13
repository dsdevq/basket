import {computed, inject} from '@angular/core';
import {patchState, signalStore, withComputed, withMethods, withState} from '@ngrx/signals';

import {describeFailure} from '../http-status';
import {AuthApi} from './auth.api';
import {OidcSignIn} from './oidc-sign-in';
import type {SessionUser} from './session.model';

interface SessionState {
  readonly loaded: boolean;
  readonly user: SessionUser | null;
  /** The signed-in account has no lifekit login yet and the lifekit sign-in is on here. */
  readonly linkRequired: boolean;
  /** Why the last `/api/session` call failed, or null when it did not. A failure is not "signed out". */
  readonly loadFailure: string | null;
}

/** Who is signed in. Loaded once from `/api/session`; sign-in and registration set it directly. */
export const SessionStore = signalStore(
  {providedIn: 'root'},
  withState<SessionState>({loaded: false, user: null, linkRequired: false, loadFailure: null}),
  withComputed(({user}) => ({
    isSignedIn: computed(() => user() !== null),
    isOwner: computed(() => user()?.role === 'owner'),
  })),
  withMethods((store, api = inject(AuthApi), oidc = inject(OidcSignIn)) => ({
    /**
     * Resolves once the first answer is in. When the server cannot be reached (or answers with an error) the
     * store stays unloaded and says why in `loadFailure`, so the next call asks again.
     */
    async ensureLoaded(): Promise<void> {
      if (store.loaded()) {
        return;
      }
      try {
        const session = await api.session();
        patchState(store, {
          loaded: true,
          user: session.user,
          linkRequired: session.linkRequired === true,
          loadFailure: null,
        });
      } catch (error) {
        patchState(store, {
          loaded: false,
          user: null,
          linkRequired: false,
          loadFailure: describeFailure(error),
        });
      }
    },
    async refresh(): Promise<void> {
      const session = await api.session();
      patchState(store, {
        loaded: true,
        user: session.user,
        linkRequired: session.linkRequired === true,
        loadFailure: null,
      });
    },
    setUser(user: SessionUser | null): void {
      patchState(store, {loaded: true, user, linkRequired: false, loadFailure: null});
    },
    /** Ends the session and clears the store. Returns the lifekit account's end-session address to leave for, if any. */
    async signOut(): Promise<string | null> {
      try {
        const ended = await api.signOut();
        return ended?.endSessionUrl ?? null;
      } finally {
        patchState(store, {user: null, linkRequired: false});
        oidc.forgetLinkAttempt();
      }
    },
  }))
);
