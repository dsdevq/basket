import {
  effect,
  type EnvironmentProviders,
  inject,
  makeEnvironmentProviders,
  provideEnvironmentInitializer,
  untracked,
} from '@angular/core';
import {provideAppearanceSync} from '@lifekit-hq/ui';

import {SessionStore} from '../auth/session.store';
import {AppPreferencesStore} from './app-preferences.store';

/**
 * Appearance follows the person: `AppearanceService` writes every change to the account and the server's copy wins once
 * `AppPreferencesStore` has loaded. The copy follows the session: read once someone is signed in, forgotten when the
 * session ends (sign-out, or a 401 that dropped it) so the next person starts clean. The device cache is left, because the
 * sign-in page paints from it.
 */
export function provideAppPreferences(): EnvironmentProviders {
  return makeEnvironmentProviders([
    provideAppearanceSync(AppPreferencesStore),
    provideEnvironmentInitializer(() => {
      const session = inject(SessionStore);
      const preferences = inject(AppPreferencesStore);
      effect(() => {
        const signedIn = session.isSignedIn();
        untracked(() => {
          if (signedIn) {
            void preferences.load();
          } else {
            preferences.clear();
          }
        });
      });
    }),
  ]);
}
