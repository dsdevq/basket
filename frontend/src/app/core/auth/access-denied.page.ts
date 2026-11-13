import {ChangeDetectionStrategy, Component, inject} from '@angular/core';
import {ButtonComponent, PageComponent} from '@lifekit-hq/ui';

import {AUTH_PAGE_HOST, AuthIntroComponent} from './auth-intro.component';
import {OidcSignIn} from './oidc-sign-in';

/** Where the server sends someone whose lifekit account is not allowed into basket. */
@Component({
  selector: 'bk-access-denied-page',
  imports: [AuthIntroComponent, ButtonComponent, PageComponent],
  host: AUTH_PAGE_HOST,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-page title="Access denied" kind="list">
      <bk-auth-intro lead="This lifekit account does not have access to basket." />
      <p class="text-cmn-sm text-text-secondary">
        Ask for basket to be added to your account, or sign in with a different one.
      </p>
      <cmn-button (clicked)="retry()" variant="primary" icon="RefreshCw">Try again</cmn-button>
    </cmn-page>
  `,
})
export class AccessDeniedPage {
  private readonly oidc = inject(OidcSignIn);

  protected retry(): void {
    this.oidc.start();
  }
}
