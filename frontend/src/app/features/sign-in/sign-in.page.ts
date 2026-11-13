import {ChangeDetectionStrategy, Component, inject, type OnInit, signal} from '@angular/core';
import {ActivatedRoute, Router} from '@angular/router';
import {AlertComponent, ButtonComponent, PageComponent} from '@lifekit-hq/ui';

import {AuthApi} from '../../core/auth/auth.api';
import {AUTH_PAGE_HOST, AuthIntroComponent} from '../../core/auth/auth-intro.component';
import {OidcSignIn} from '../../core/auth/oidc-sign-in';
import {
  PasskeyCancelledError,
  PasskeyClient,
  PasskeyUnsupportedError,
} from '../../core/auth/passkey.client';
import {SessionStore} from '../../core/auth/session.store';
import {HttpStatus, statusOf} from '../../core/http-status';

/** What the sign-in page says when the server sent the person back with `?info=` or `?error=`. */
function bannerFor(
  info: string | null,
  error: string | null
): {kind: 'info' | 'error'; text: string} | null {
  if (error) {
    return {
      kind: 'error',
      text: 'Signing in with your lifekit account did not finish. Try again.',
    };
  }
  if (info === 'signed_out') {
    return {kind: 'info', text: 'You have signed out.'};
  }
  if (info === 'link_required') {
    return {
      kind: 'info',
      text: 'Sign in with your passkey first, so basket can link your lifekit account.',
    };
  }
  return null;
}

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
  imports: [AlertComponent, AuthIntroComponent, ButtonComponent, PageComponent],
  host: AUTH_PAGE_HOST,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-page title="basket" kind="list">
      <bk-auth-intro lead="The shopping list for the two of you" />
      @if (banner(); as banner) {
        <cmn-alert [title]="banner.text" [variant]="banner.kind" />
      }
      @if (problem()) {
        <cmn-alert [title]="problem()" variant="error" />
      }
      @if (forwarding()) {
        <p class="text-cmn-sm text-text-secondary" role="status">
          Taking you to your lifekit account…
        </p>
      } @else if (checking()) {
        <p class="text-cmn-sm text-text-secondary" role="status">One moment…</p>
      } @else {
        @if (oidcOn()) {
          <cmn-button (clicked)="continueWithLifekit()" variant="primary" icon="User">
            Sign in with your lifekit account
          </cmn-button>
        }
        <!-- Transition only: the passkey stays until the lifekit sign-in is live for everyone. -->
        <cmn-button
          [loading]="busy()"
          [variant]="oidcOn() ? 'secondary' : 'primary'"
          (clicked)="signInWithPasskey()"
          icon="KeyRound"
        >
          Sign in with a passkey
        </cmn-button>
        @if (!oidcOn()) {
          <p class="text-center text-cmn-xs text-text-secondary">
            There is no password. New here? Open the invite link you were sent.
          </p>
        }
      }
    </cmn-page>
  `,
})
export class SignInPage implements OnInit {
  private readonly api = inject(AuthApi);
  private readonly passkeys = inject(PasskeyClient);
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly oidc = inject(OidcSignIn);
  private readonly params = inject(ActivatedRoute).snapshot.queryParamMap;

  protected readonly banner = signal(bannerFor(this.params.get('info'), this.params.get('error')));
  protected readonly oidcOn = signal(false);
  protected readonly checking = signal(true);
  protected readonly forwarding = signal(false);
  protected readonly busy = signal(false);
  protected readonly problem = signal('');

  public ngOnInit(): void {
    void this.decide();
  }

  private async decide(): Promise<void> {
    const on = await this.oidc.isAvailable();
    this.oidcOn.set(on);
    // A page reached with a message (signed out, a failed attempt) stays put; forwarding again would hide it and can loop.
    if (on && this.banner() === null) {
      this.forwarding.set(true);
      this.oidc.start();
    }
    this.checking.set(false);
  }

  protected continueWithLifekit(): void {
    this.oidc.start();
  }

  protected async signInWithPasskey(): Promise<void> {
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
