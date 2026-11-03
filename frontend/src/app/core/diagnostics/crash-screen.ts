import {type ErrorHandler} from '@angular/core';

/** The id of the fallback panel; `public/boot-guard.js` draws the same one when the bundle never starts. */
export const CRASH_ELEMENT_ID = 'bk-crash';

const MAX_NAME_LENGTH = 60;

/**
 * The name of whatever was thrown, and nothing else: no message, no stack, no URL, so a screenshot of the
 * fallback can be shared without leaking an invite token.
 */
export function errorNameOf(error: unknown): string {
  const raw =
    typeof (error as {name?: unknown} | null)?.name === 'string'
      ? (error as {name: string}).name
      : typeof error;
  const name = raw.replace(/[^\w .:-]/g, '').slice(0, MAX_NAME_LENGTH);
  return name || 'Error';
}

/** Failures with an HTTP status are reported by the screen that made the call; they are not crashes. */
function isHttpFailure(error: unknown): boolean {
  return typeof (error as {status?: unknown} | null)?.status === 'number';
}

/** True once Angular has rendered something into `<bk-root>`; from then on a stray error is not a crash. */
export function isAppMounted(doc: Document): boolean {
  return (doc.querySelector('bk-root')?.childNodes.length ?? 0) > 0;
}

/** Draws "Something went wrong" over the page. Plain DOM, so it works even when Angular itself is broken. */
export function showCrashScreen(doc: Document, error: unknown): void {
  if (doc.getElementById(CRASH_ELEMENT_ID)) {
    return;
  }
  const panel = doc.createElement('div');
  panel.id = CRASH_ELEMENT_ID;
  panel.setAttribute('role', 'alert');
  panel.style.cssText =
    'position:fixed;inset:0;z-index:2147483647;display:flex;flex-direction:column;align-items:center;' +
    'justify-content:center;gap:12px;padding:24px;text-align:center;font:16px/1.4 system-ui,sans-serif;' +
    'background:Canvas;color:CanvasText';

  const heading = doc.createElement('h1');
  heading.textContent = 'Something went wrong';
  heading.style.cssText = 'margin:0;font-size:22px';
  const detail = doc.createElement('p');
  detail.textContent = `Error: ${errorNameOf(error)}`;
  detail.style.margin = '0';
  const hint = doc.createElement('p');
  hint.textContent = 'Reload the page. If it keeps happening, tell whoever sent you the link.';
  hint.style.cssText = 'margin:0;max-width:24em';
  const reload = doc.createElement('button');
  reload.type = 'button';
  reload.textContent = 'Reload';
  reload.style.cssText = 'font:inherit;padding:10px 24px;border-radius:8px';
  reload.addEventListener('click', () => doc.defaultView?.location.reload());

  panel.append(heading, detail, hint, reload);
  doc.body.append(panel);
}

function reportCrash(doc: Document, error: unknown): void {
  if (isAppMounted(doc)) {
    console.error(error);
  } else {
    showCrashScreen(doc, error);
  }
}

/**
 * Catches what Angular does not: errors thrown outside a component and promises nobody awaited. An error event
 * without an error object (ResizeObserver noise, cross-origin "Script error.") says nothing and is ignored.
 * Once the app has mounted, such errors are only logged.
 */
export function installCrashFallback(win: Window): void {
  win.addEventListener('error', event => {
    if (event.error) {
      reportCrash(win.document, event.error);
    }
  });
  win.addEventListener('unhandledrejection', event => {
    if (!isHttpFailure(event.reason)) {
      reportCrash(win.document, event.reason);
    }
  });
}

/** Angular's route into the same fallback: template errors, failed lazy chunks, a throwing guard. */
export class CrashErrorHandler implements ErrorHandler {
  public handleError(error: unknown): void {
    console.error(error);
    if (!isHttpFailure(error) && !isAppMounted(document)) {
      showCrashScreen(document, error);
    }
  }
}
