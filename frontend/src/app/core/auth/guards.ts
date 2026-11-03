import {inject} from '@angular/core';
import {type CanActivateFn, Router} from '@angular/router';

import {SessionStore} from './session.store';

// Everything is injected before the first await: after it, the injection context is gone.

/** Signed-in people only; everyone else is sent to the sign-in page. */
export const authGuard: CanActivateFn = async () => {
  const session = inject(SessionStore);
  const router = inject(Router);
  await session.ensureLoaded();
  return session.isSignedIn() || router.createUrlTree(['/sign-in']);
};

/** Pages for people who are not signed in yet (sign-in, invite). */
export const guestGuard: CanActivateFn = async () => {
  const session = inject(SessionStore);
  const router = inject(Router);
  await session.ensureLoaded();
  return !session.isSignedIn() || router.createUrlTree(['/list']);
};

/** Owner-only pages. The API refuses the calls too; this keeps the link out of sight. */
export const ownerGuard: CanActivateFn = async () => {
  const session = inject(SessionStore);
  const router = inject(Router);
  await session.ensureLoaded();
  return session.isOwner() || router.createUrlTree(['/list']);
};
