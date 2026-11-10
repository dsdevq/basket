import {provideHttpClient, withInterceptors} from '@angular/common/http';
import {type ApplicationConfig, ErrorHandler} from '@angular/core';
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
import {CrashErrorHandler, provideMountMarker} from './core/diagnostics/crash-screen';
import {APP_ICONS} from './core/icons';
import {provideAppServiceWorker} from './core/pwa/service-worker.provider';
import {BasketTitleStrategy} from './core/title.strategy';
import {APP_VERSION} from './core/version';

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(APP_ROUTES, withComponentInputBinding()),
    provideHttpClient(withInterceptors([sessionInterceptor])),
    provideLucideIcons(APP_ICONS),
    provideAppMark('bk'),
    provideToastPreferenceErrors(),
    provideDesignPresets({id: 'lifekit', label: 'Lifekit'}),
    provideWhatsNew({version: APP_VERSION, storageKey: 'basket.whatsNew.lastSeen'}),
    {provide: ErrorHandler, useClass: CrashErrorHandler},
    provideMountMarker(),
    provideAppServiceWorker(),
    {provide: TitleStrategy, useClass: BasketTitleStrategy},
  ],
};
