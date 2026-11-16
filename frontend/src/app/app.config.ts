import {provideHttpClient, withInterceptors} from '@angular/common/http';
import {
  type ApplicationConfig,
  ErrorHandler,
  inject,
  provideEnvironmentInitializer,
} from '@angular/core';
import {provideRouter, TitleStrategy, withComponentInputBinding} from '@angular/router';
import {
  provideAppMark,
  provideDesignPresets,
  provideLucideIcons,
  provideToastPreferenceErrors,
  provideWhatsNew,
} from '@lifekit-hq/ui';

import {APP_ROUTES} from './app.routes';
import {sessionInterceptor} from './core/auth/session.interceptor';
import {SessionStore} from './core/auth/session.store';
import {CrashErrorHandler, provideMountMarker} from './core/diagnostics/crash-screen';
import {APP_ICONS} from './core/icons';
import {provideAppPreferences} from './core/preferences/preferences.provider';
import {provideAppServiceWorker} from './core/pwa/service-worker.provider';
import {BasketTitleStrategy} from './core/title.strategy';
import {APP_VERSION} from './core/version';

/** Owner-only What's new lines are read through the signed-in role, which exists once the injector does. */
let isOwner = (): boolean => false;

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(APP_ROUTES, withComponentInputBinding()),
    provideHttpClient(withInterceptors([sessionInterceptor])),
    provideLucideIcons(APP_ICONS),
    provideAppMark('bk'),
    provideAppPreferences(),
    provideToastPreferenceErrors(),
    provideDesignPresets({id: 'lifekit', label: 'Lifekit'}),
    provideEnvironmentInitializer(() => {
      const session = inject(SessionStore);
      isOwner = () => session.isOwner();
    }),
    provideWhatsNew({
      version: APP_VERSION,
      storageKey: 'basket.whatsNew.lastSeen',
      includeOwnerNotes: () => isOwner(),
    }),
    {provide: ErrorHandler, useClass: CrashErrorHandler},
    provideMountMarker(),
    provideAppServiceWorker(),
    {provide: TitleStrategy, useClass: BasketTitleStrategy},
  ],
};
