import {provideHttpClient, withInterceptors} from '@angular/common/http';
import {type ApplicationConfig, ErrorHandler} from '@angular/core';
import {provideRouter, TitleStrategy, withComponentInputBinding} from '@angular/router';
import {provideToastPreferenceErrors} from '@lifekit-hq/ui';

import {APP_ROUTES} from './app.routes';
import {sessionInterceptor} from './core/auth/session.interceptor';
import {CrashErrorHandler} from './core/diagnostics/crash-screen';
import {BasketTitleStrategy} from './core/title.strategy';

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(APP_ROUTES, withComponentInputBinding()),
    provideHttpClient(withInterceptors([sessionInterceptor])),
    provideToastPreferenceErrors(),
    {provide: ErrorHandler, useClass: CrashErrorHandler},
    {provide: TitleStrategy, useClass: BasketTitleStrategy},
  ],
};
