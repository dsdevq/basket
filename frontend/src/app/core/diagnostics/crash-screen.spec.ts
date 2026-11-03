import {Location} from '@angular/common';
import {ChangeDetectionStrategy, Component, ErrorHandler, inject} from '@angular/core';
import {TestBed} from '@angular/core/testing';
import {provideRouter, Router, type Routes} from '@angular/router';

import {AppComponent} from '../../app.component';
import {
  CRASH_ELEMENT_ID,
  CrashErrorHandler,
  errorNameOf,
  installCrashFallback,
  isAppMounted,
  MOUNTED_ATTRIBUTE,
  provideMountMarker,
  showCrashScreen,
} from './crash-screen';

const panel = () => document.getElementById(CRASH_ELEMENT_ID);

describe('crash screen', () => {
  afterEach(() => {
    panel()?.remove();
    vi.restoreAllMocks();
  });

  it('names the error and shows nothing else of it', () => {
    const error = new TypeError('token=SECRET123 failed at https://basket/join');
    error.stack = 'TypeError: token=SECRET123\n    at secret.js:1';

    showCrashScreen(document, error);

    const text = panel()?.textContent ?? '';
    expect(text).toContain('Something went wrong');
    expect(text).toContain('Error: TypeError');
    expect(text).not.toContain('SECRET123');
    expect(text).not.toContain('secret.js');
    expect(text).toContain('Reload');
    expect(panel()?.getAttribute('role')).toBe('alert');
  });

  it('draws one panel however many errors follow', () => {
    showCrashScreen(document, new Error('a'));
    showCrashScreen(document, new RangeError('b'));

    expect(document.querySelectorAll(`#${CRASH_ELEMENT_ID}`)).toHaveLength(1);
    expect(panel()?.textContent).toContain('Error: Error');
  });

  it.each([
    [new RangeError('x'), 'RangeError'],
    ['a string with a token abc', 'string'],
    [null, 'object'],
    [undefined, 'undefined'],
    [{name: '<img src=x onerror=alert(1)>'}, 'img srcx onerroralert1'],
    [{name: ''}, 'Error'],
    [{name: 'x'.repeat(200)}, 'x'.repeat(60)],
  ])('reduces %o to the safe name %s', (error, name) => {
    expect(errorNameOf(error)).toBe(name);
  });

  it('shows the screen for an uncaught error and an unhandled rejection, not for noise', () => {
    installCrashFallback(window);

    window.dispatchEvent(new ErrorEvent('error', {message: 'ResizeObserver loop completed'}));
    expect(panel()).toBeNull();

    const rejection = new Event('unhandledrejection') as Event & {reason: unknown};
    rejection.reason = {status: 500, name: 'HttpErrorResponse'};
    window.dispatchEvent(rejection);
    expect(panel()).toBeNull();

    window.dispatchEvent(new ErrorEvent('error', {error: new SyntaxError('bad'), message: 'bad'}));
    expect(panel()?.textContent).toContain('Error: SyntaxError');
  });

  it('is Angular’s error handler too, and leaves HTTP failures to the screens', () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const handler = new CrashErrorHandler();

    handler.handleError({status: 0, name: 'HttpErrorResponse'});
    expect(panel()).toBeNull();

    handler.handleError(new Error('boom'));
    expect(panel()?.textContent).toContain('Something went wrong');
  });

  describe('with the real shell and routing', () => {
    @Component({template: 'a routed page', changeDetection: ChangeDetectionStrategy.OnPush})
    class PageComponent {}

    const throwingGuard = () => {
      throw new TypeError('guard broke');
    };

    async function boot(initialUrl: string, routes: Routes): Promise<void> {
      TestBed.configureTestingModule({
        rethrowApplicationErrors: false,
        providers: [
          provideRouter(routes),
          provideMountMarker(),
          {provide: ErrorHandler, useClass: CrashErrorHandler},
        ],
      });
      const fixture = TestBed.createComponent(AppComponent);
      TestBed.inject(Location).go(initialUrl);
      TestBed.inject(Router).initialNavigation();
      await fixture.whenStable();
    }

    const rejection = (reason: unknown) => {
      const event = new Event('unhandledrejection') as Event & {reason: unknown};
      event.reason = reason;
      window.dispatchEvent(event);
    };

    beforeEach(() => {
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      installCrashFallback(window);
    });

    afterEach(() => document.documentElement.removeAttribute(MOUNTED_ATTRIBUTE));

    it('is not mounted while <router-outlet> is up but no routed page has rendered', () => {
      TestBed.configureTestingModule({providers: [provideRouter([]), provideMountMarker()]});
      const fixture = TestBed.createComponent(AppComponent);
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('router-outlet')).not.toBeNull();
      expect(isAppMounted(document)).toBe(false);

      new CrashErrorHandler().handleError(new Error('boom'));
      expect(panel()?.textContent).toContain('Something went wrong');
    });

    it('explains a lazy chunk that fails to load', async () => {
      await boot('/broken', [
        {
          path: 'broken',
          loadComponent: () =>
            Promise.reject(new TypeError('Failed to fetch dynamically imported module')),
        },
      ]);

      expect(isAppMounted(document)).toBe(false);
      expect(panel()?.textContent).toContain('Error: TypeError');
    });

    it('explains a guard that throws', async () => {
      await boot('/guarded', [
        {path: 'guarded', canActivate: [throwingGuard], component: PageComponent},
      ]);

      expect(isAppMounted(document)).toBe(false);
      expect(panel()?.textContent).toContain('Error: TypeError');
    });

    it('explains an uncaught error that happens before the first page renders', () => {
      TestBed.configureTestingModule({providers: [provideRouter([]), provideMountMarker()]});
      TestBed.createComponent(AppComponent).detectChanges();

      window.dispatchEvent(
        new ErrorEvent('error', {error: new SyntaxError('bad'), message: 'bad'})
      );

      expect(panel()?.textContent).toContain('Error: SyntaxError');
    });

    it('only logs stray errors once the first routed page has rendered', async () => {
      await boot('/page', [{path: 'page', component: PageComponent}]);
      expect(isAppMounted(document)).toBe(true);
      const log = vi.mocked(console.error);
      log.mockClear();
      const error = new SyntaxError('bad');
      const stray = new RangeError('stray');
      const fromAngular = new Error('boom');

      window.dispatchEvent(new ErrorEvent('error', {error, message: 'bad'}));
      rejection(stray);
      TestBed.inject(ErrorHandler).handleError(fromAngular);

      expect(panel()).toBeNull();
      expect(log).toHaveBeenCalledWith(error);
      expect(log).toHaveBeenCalledWith(stray);
      expect(log).toHaveBeenCalledWith(fromAngular);
    });

    it('counts the page a guard redirects to', async () => {
      await boot('/old', [
        {
          path: 'old',
          canActivate: [() => inject(Router).parseUrl('/page')],
          component: PageComponent,
        },
        {path: 'page', component: PageComponent},
      ]);

      expect(isAppMounted(document)).toBe(true);
    });
  });
});
