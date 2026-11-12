// Registers the shared lk-* PWA elements (update prompt, offline banner, install hint) before bootstrap so
// lk-install-hint captures the browser's one-shot `beforeinstallprompt` event.
import '@lifekit-hq/elements';

import {bootstrapApplication} from '@angular/platform-browser';

import {AppComponent} from './app/app.component';
import {appConfig} from './app/app.config';
import {installCrashFallback, showCrashScreen} from './app/core/diagnostics/crash-screen';

installCrashFallback(window);
bootstrapApplication(AppComponent, appConfig).catch((error: unknown) => {
  console.error(error);
  showCrashScreen(document, error);
});
