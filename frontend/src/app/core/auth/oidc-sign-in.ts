import {DOCUMENT} from '@angular/common';
import {inject, Injectable} from '@angular/core';

import {BrowserNavigation} from './browser-navigation';

const START_PATH = '/api/auth/oidc/start';
/** Set before the link-up round trip starts, so a refused or abandoned attempt is not retried on every page. */
const LINK_ATTEMPT_KEY = 'basket.oidc.linkAttempted';

/** Sign-in through the lifekit account: the server starts it and redirects to Logto, so the SPA only navigates. */
@Injectable({providedIn: 'root'})
export class OidcSignIn {
  private readonly navigation = inject(BrowserNavigation);
  private readonly document = inject(DOCUMENT);

  /**
   * Whether the lifekit account sign-in is on here. The server answers the start address with a redirect when it is
   * and with 404 when it is not configured; the redirect is not followed, so nothing leaves the page.
   */
  public async isAvailable(): Promise<boolean> {
    try {
      const response = await fetch(START_PATH, {redirect: 'manual', credentials: 'same-origin'});
      return response.type === 'opaqueredirect';
    } catch {
      return false;
    }
  }

  public start(returnUrl?: string): void {
    const query = returnUrl?.startsWith('/') ? `?returnUrl=${encodeURIComponent(returnUrl)}` : '';
    this.navigation.assign(START_PATH + query);
  }

  /** Starts the flow for a signed-in account that is not linked yet, but only once per browser tab session. True when it left. */
  public startLinkOnce(returnUrl: string): boolean {
    const storage = this.storage();
    if (storage?.getItem(LINK_ATTEMPT_KEY)) {
      return false;
    }
    try {
      storage?.setItem(LINK_ATTEMPT_KEY, '1');
    } catch {
      // Without storage the attempt cannot be remembered, so do not risk a loop.
      return false;
    }
    this.start(returnUrl);
    return true;
  }

  public forgetLinkAttempt(): void {
    try {
      this.storage()?.removeItem(LINK_ATTEMPT_KEY);
    } catch {
      // Nothing was stored.
    }
  }

  private storage(): Storage | null {
    try {
      return this.document.defaultView?.sessionStorage ?? null;
    } catch {
      return null;
    }
  }
}
