import {ChangeDetectionStrategy, Component, inject, signal} from '@angular/core';
import {Router} from '@angular/router';
import {AlertComponent, ButtonComponent, PageComponent} from '@lifekit-hq/ui';

import {AuthApi} from '../../core/auth/auth.api';
import {AUTH_PAGE_HOST, AuthIntroComponent} from '../../core/auth/auth-intro.component';
import {
  PasskeyCancelledError,
  PasskeyClient,
  PasskeyUnsupportedError,
} from '../../core/auth/passkey.client';
import {SessionStore} from '../../core/auth/session.store';
import {HttpStatus, statusOf} from '../../core/http-status';

function describe(error: unknown): string {
  if (error instanceof PasskeyCancelledError) {
    return 'Sign-in was cancelled. Try again when you are ready.';
  }
  if (error instanceof PasskeyUnsupportedError) {
    return 'This browser cannot use passkeys. Open basket in Safari or Chrome.';
  }
  const status = statusOf(error);
  if (status === HttpStatus.tooManyRequests) {
    return 'Too many tries. Wait a minute and try again.';
  }
  if (status === HttpStatus.unauthorized) {
    return 'That passkey is not registered here. Ask for a new invite link.';
  }
  return 'Could not sign in. Check your connection and try again.';
}

@Component({
  selector: 'bk-sign-in-page',
  imports: [AuthIntroComponent, AlertComponent, ButtonComponent, PageComponent],
  host: AUTH_PAGE_HOST,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-page title="basket" kind="list">
      <bk-auth-intro lead="The shopping list for the two of you." />
      @if (problem()) {
        <cmn-alert [title]="problem()" variant="error" />
      }
      <cmn-button [loading]="busy()" (clicked)="signIn()" variant="primary" icon="KeyRound">
        Sign in with a passkey
      </cmn-button>
      <p class="text-cmn-xs text-text-secondary">New here? Open the invite link you were sent.</p>
    </cmn-page>
  `,
})
export class SignInPage {
  private readonly api = inject(AuthApi);
  private readonly passkeys = inject(PasskeyClient);
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);

  protected readonly busy = signal(false);
  protected readonly problem = signal('');

  protected async signIn(): Promise<void> {
    this.busy.set(true);
    this.problem.set('');
    try {
      const options = await this.api.signInOptions();
      const credential = await this.passkeys.get(options);
      await this.api.signIn(credential);
      await this.session.refresh();
      await this.router.navigateByUrl('/list');
    } catch (error) {
      this.problem.set(describe(error));
    } finally {
      this.busy.set(false);
    }
  }
}
