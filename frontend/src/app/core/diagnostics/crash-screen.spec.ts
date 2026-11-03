import {
  CRASH_ELEMENT_ID,
  CrashErrorHandler,
  errorNameOf,
  installCrashFallback,
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
});
