import {ChangeDetectionStrategy, Component, inject} from '@angular/core';
import {Router} from '@angular/router';
import {ButtonComponent, PageComponent} from '@lifekit-hq/ui';

import {AUTH_PAGE_HOST, AuthIntroComponent} from './auth-intro.component';

/** Where the server sends a lifekit account that matches no basket account while older accounts are still unlinked. */
@Component({
  selector: 'bk-link-required-page',
  imports: [AuthIntroComponent, ButtonComponent, PageComponent],
  host: AUTH_PAGE_HOST,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-page title="One more step" kind="list">
      <bk-auth-intro
        lead="basket needs to link your lifekit account to your existing basket account first."
      />
      <p class="text-cmn-sm text-text-secondary">
        Open basket on the phone you already use, or sign in with your passkey first.
      </p>
      <cmn-button (clicked)="passkey()" variant="primary" icon="KeyRound">
        Sign in with a passkey
      </cmn-button>
    </cmn-page>
  `,
})
export class LinkRequiredPage {
  private readonly router = inject(Router);

  protected passkey(): Promise<boolean> {
    return this.router.navigate(['/sign-in'], {queryParams: {info: 'link_required'}});
  }
}
