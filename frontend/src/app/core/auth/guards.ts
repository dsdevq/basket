import {inject} from '@angular/core';
import {type CanActivateFn, Router} from '@angular/router';

import {OidcSignIn} from './oidc-sign-in';
import {SessionStore} from './session.store';

/** Where people land when the server cannot be asked who they are. */
export const UNREACHABLE_PATH = '/unreachable';

// Everything is injected before the first await: after it, the injection context is gone.

/**
 * Signed-in people only; everyone else is sent to the sign-in page. A passkey session whose account is not linked to
 * the lifekit account yet goes through that sign-in once, and comes back to the page it asked for.
 */
export const authGuard: CanActivateFn = async (_route, state) => {
  const session = inject(SessionStore);
  const router = inject(Router);
  const oidc = inject(OidcSignIn);
  await session.ensureLoaded();
  if (session.loadFailure()) {
    return router.createUrlTree([UNREACHABLE_PATH]);
  }
  if (session.isSignedIn() && session.linkRequired() && oidc.startLinkOnce(state.url)) {
    return false;
  }
  return session.isSignedIn() || router.createUrlTree(['/sign-in']);
};

/**
 * Pages for people who are not signed in yet (sign-in, invite). A route with `data.tolerateOffline` (the invite
 * page, which checks the link with the server itself and says so when it cannot) is let through when the
 * session call fails; redirecting it would also drop the `#token` from the address.
 */
export const guestGuard: CanActivateFn = async route => {
  const session = inject(SessionStore);
  const router = inject(Router);
  await session.ensureLoaded();
  if (session.loadFailure()) {
    return route.data['tolerateOffline'] === true || router.createUrlTree([UNREACHABLE_PATH]);
  }
  return !session.isSignedIn() || router.createUrlTree(['/list']);
};

/** Owner-only pages. The API refuses the calls too; this keeps the link out of sight. */
export const ownerGuard: CanActivateFn = async () => {
  const session = inject(SessionStore);
  const router = inject(Router);
  await session.ensureLoaded();
  if (session.loadFailure()) {
    return router.createUrlTree([UNREACHABLE_PATH]);
  }
  return session.isOwner() || router.createUrlTree(['/list']);
};
