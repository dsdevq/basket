import {DOCUMENT} from '@angular/common';
import {inject, Injectable} from '@angular/core';

/** Leaves the SPA for a server address (sign-in at the lifekit account, its end-session page); one seam so tests can watch it. */
@Injectable({providedIn: 'root'})
export class BrowserNavigation {
  private readonly document = inject(DOCUMENT);

  public assign(url: string): void {
    this.document.defaultView?.location.assign(url);
  }
}
