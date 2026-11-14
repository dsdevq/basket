import {accountPreferencesStore} from '@lifekit-hq/core/preferences';

/**
 * The signed-in person's account-tier preferences (`GET` / `PATCH /api/me/preferences`). Today that is the appearance
 * (theme mode, seed colour, intensity) and nothing else: the design preset stays per device, as the library keeps it.
 * `AppearanceService` writes it, `provideAppPreferences` loads it after sign-in and clears it on sign-out.
 */
export const AppPreferencesStore = accountPreferencesStore('/api/me/preferences');
