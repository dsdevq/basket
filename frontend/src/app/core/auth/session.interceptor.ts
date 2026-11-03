import {type HttpInterceptorFn} from '@angular/common/http';
import {inject} from '@angular/core';
import {Router} from '@angular/router';
import {tap} from 'rxjs';

import {HttpStatus, statusOf} from '../http-status';
import {SessionStore} from './session.store';

/** A 401 from the API on a signed-in screen means the session ended: drop it and go to sign-in. */
export const sessionInterceptor: HttpInterceptorFn = (request, next) => {
  const session = inject(SessionStore);
  const router = inject(Router);
  return next(request).pipe(
    tap({
      error: (error: unknown) => {
        if (
          statusOf(error) === HttpStatus.unauthorized &&
          session.isSignedIn() &&
          !request.url.startsWith('/api/auth/')
        ) {
          session.setUser(null);
          void router.navigateByUrl('/sign-in');
        }
      },
    })
  );
};
