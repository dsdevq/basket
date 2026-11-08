import {
  type EnvironmentProviders,
  inject,
  isDevMode,
  makeEnvironmentProviders,
  provideEnvironmentInitializer,
} from '@angular/core';
import {provideServiceWorker} from '@angular/service-worker';
import {AppUpdateService} from '@lifekit-hq/core/pwa';

const SERVICE_WORKER_SCRIPT = 'ngsw-worker.js';
const REGISTRATION_DELAY_MS = 30_000;

/**
 * Angular's service worker for the app shell only (`ngsw-config.json` has no data groups). Registers in the
 * production build once the app is stable, so first paint is never delayed; `ng serve` has no worker.
 * `AppUpdateService` is created at start-up so a "new version ready" event is not missed on a page that has
 * no update prompt (sign-in, join).
 */
export function provideAppServiceWorker(): EnvironmentProviders {
  return makeEnvironmentProviders([
    provideServiceWorker(SERVICE_WORKER_SCRIPT, {
      enabled: !isDevMode(),
      registrationStrategy: `registerWhenStable:${REGISTRATION_DELAY_MS}`,
      // The browser re-fetches the worker script instead of trusting its HTTP cache, so a release is seen at once.
      updateViaCache: 'none',
    }),
    provideEnvironmentInitializer(() => void inject(AppUpdateService)),
  ]);
}
