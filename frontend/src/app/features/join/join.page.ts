import {Clipboard} from '@angular/cdk/clipboard';
import {DOCUMENT} from '@angular/common';
import {ChangeDetectionStrategy, Component, inject, type OnInit, signal} from '@angular/core';
import {FormControl, FormGroup, ReactiveFormsModule, Validators} from '@angular/forms';
import {Router} from '@angular/router';
import {
  AlertComponent,
  ButtonComponent,
  FormFieldComponent,
  InputComponent,
  ToastService,
} from '@lifekit-hq/ui';

import {AuthApi, type PasskeyFailureReport} from '../../core/auth/auth.api';
import {AuthCardComponent} from '../../core/auth/auth-card.component';
import {
  INSTANT_REFUSAL_MS,
  PasskeyCancelledError,
  PasskeyClient,
  PasskeyUnsupportedError,
} from '../../core/auth/passkey.client';
import {SessionStore} from '../../core/auth/session.store';
import {describeFailure, HttpStatus, MAX_NAME_LENGTH, statusOf} from '../../core/http-status';

/** The notice for everything except "this browser cannot make passkeys", which has a page of its own. */
function describe(error: unknown): string {
  if (error instanceof PasskeyCancelledError) {
    return 'The passkey was not created. Try again when you are ready.';
  }
  if (statusOf(error) === HttpStatus.tooManyRequests) {
    return 'Too many tries. Wait a minute and try again.';
  }
  return 'Could not create the passkey. Check your connection and try again.';
}

/**
 * `missing`: the address has no token (the part after `#` was lost on the way). `used`: the server does not
 * know the token (used, expired or mistyped). `unreachable`: the server could not be asked.
 */
type Step = 'checking' | 'ready' | 'missing' | 'used' | 'unreachable' | 'unsupported';

/** The failure kind and labels a browser-side error is reported under; see `PasskeyFailureReport` on the server. */
function labelsOf(
  error: unknown
): Pick<
  PasskeyFailureReport,
  'kind' | 'errorName' | 'elapsedMs' | 'elapsedBucket' | 'credentialNull'
> {
  if (error instanceof PasskeyCancelledError || error instanceof PasskeyUnsupportedError) {
    const {errorName, elapsedMs, credentialNull} = error.detail;
    return {
      kind: error.kind,
      errorName,
      elapsedMs,
      elapsedBucket:
        elapsedMs === null ? null : elapsedMs < INSTANT_REFUSAL_MS ? 'instant' : 'later',
      credentialNull: credentialNull === true,
    };
  }
  if (error instanceof DOMException) {
    return {
      kind: 'other',
      errorName: error.name,
      elapsedMs: null,
      elapsedBucket: null,
      credentialNull: false,
    };
  }
  return {
    kind: statusOf(error) > 0 ? 'http' : 'other',
    errorName: 'PasskeyClientError',
    elapsedMs: null,
    elapsedBucket: null,
    credentialNull: false,
  };
}

/**
 * Where an invite link lands: `/join#<token>`. The token sits in the URL fragment, which the browser never
 * sends to the server; it is read once, kept in memory, and wiped from the address bar.
 */
@Component({
  selector: 'bk-join-page',
  imports: [
    ReactiveFormsModule,
    AuthCardComponent,
    AlertComponent,
    ButtonComponent,
    FormFieldComponent,
    InputComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (step()) {
      @case ('checking') {
        <bk-auth-card heading="Checking your invite" />
      }
      @case ('missing') {
        <bk-auth-card
          heading="This link is incomplete"
          lead="The part after the # sign did not arrive."
        >
          <p class="text-cmn-sm text-text-secondary">
            Open the whole link again from the message you were sent. Some apps cut the end off a
            link: copy all of it and paste it into Safari or Chrome.
          </p>
        </bk-auth-card>
      }
      @case ('used') {
        <bk-auth-card
          heading="This link was used or has expired"
          lead="Invite links work once and last 24 hours."
        >
          <p class="text-cmn-sm text-text-secondary">Ask for a new one.</p>
        </bk-auth-card>
      }
      @case ('unreachable') {
        <bk-auth-card
          heading="Cannot reach basket"
          lead="Your browser could not get an answer from the server."
        >
          <p class="text-cmn-sm text-text-secondary">
            Your link is fine and has not been used. Check your connection. A content blocker can
            also stop it.
          </p>
          <p class="text-cmn-xs text-text-secondary">Detail: {{ failure() }}</p>
          <cmn-button (clicked)="check()" variant="primary" icon="RefreshCw">
            Try again
          </cmn-button>
        </bk-auth-card>
      }
      @case ('unsupported') {
        <bk-auth-card
          heading="Open this link in your browser"
          lead="This app's built-in browser cannot make a passkey."
        >
          <p class="text-cmn-sm text-text-secondary">
            Your link is fine and has not been used. Copy it, then paste it into Safari or Chrome.
            Or open this page there from the app's menu, often a compass or "Open in browser".
          </p>
          <cmn-button (clicked)="copyLink()" variant="primary" icon="Copy">Copy link</cmn-button>
        </bk-auth-card>
      }
      @default {
        <bk-auth-card heading="Join basket" lead="Make a passkey and you are in. No password.">
          @if (problem()) {
            <cmn-alert [title]="problem()" variant="error" />
          }
          @if (cancelled()) {
            <p class="text-cmn-sm text-text-secondary">
              Still not working? Open this page in Safari or Chrome.
            </p>
            <cmn-button (clicked)="copyLink()" variant="secondary" icon="Copy">
              Copy link
            </cmn-button>
          }
          <form [formGroup]="form" (ngSubmit)="register()" class="flex flex-col gap-cmn-group-gap">
            <cmn-form-field
              [control]="name"
              label="Your name"
              hint="Shown next to the things you add"
            >
              <cmn-input
                formControlName="name"
                autocomplete="given-name"
                enterkeyhint="done"
                placeholder="e.g. Anna"
              />
            </cmn-form-field>
            <cmn-button [loading]="busy()" type="submit" variant="primary" icon="KeyRound">
              Create passkey
            </cmn-button>
          </form>
        </bk-auth-card>
      }
    }
  `,
})
export class JoinPage implements OnInit {
  private readonly document = inject(DOCUMENT);
  private readonly api = inject(AuthApi);
  private readonly passkeys = inject(PasskeyClient);
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly clipboard = inject(Clipboard);
  private readonly toast = inject(ToastService);

  private token = '';

  protected readonly step = signal<Step>('checking');
  protected readonly busy = signal(false);
  protected readonly problem = signal('');
  protected readonly cancelled = signal(false);
  protected readonly failure = signal('');
  protected readonly name = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required, Validators.maxLength(MAX_NAME_LENGTH)],
  });
  protected readonly form = new FormGroup({name: this.name});

  public ngOnInit(): void {
    void this.start();
  }

  protected async start(): Promise<void> {
    const {location, defaultView} = this.document;
    this.token = location.hash.replace(/^#/, '');
    defaultView?.history.replaceState(null, '', location.pathname);
    if (!this.token) {
      this.step.set('missing');
      return;
    }
    await this.check();
  }

  /** Asks the server whether the link is good; also the "Try again" of the unreachable screen. */
  protected async check(): Promise<void> {
    this.step.set('checking');
    try {
      await this.api.checkInvite(this.token);
      // Before the button: a browser that has no passkeys at all gets the instruction, not a button that cannot work.
      const reason = this.passkeys.unsupportedReason();
      if (reason) {
        this.report('page', new PasskeyUnsupportedError(reason));
        this.step.set('unsupported');
      } else {
        this.step.set('ready');
      }
    } catch (error) {
      if (statusOf(error) === HttpStatus.notFound) {
        this.step.set('used');
      } else {
        this.failure.set(describeFailure(error));
        this.step.set('unreachable');
      }
    }
  }

  protected async register(): Promise<void> {
    const displayName = this.name.value.trim();
    if (!displayName) {
      this.name.markAsTouched();
      this.problem.set('Enter the name to show next to your items.');
      return;
    }
    this.busy.set(true);
    this.problem.set('');
    this.cancelled.set(false);
    let stage: PasskeyFailureReport['stage'] = 'options';
    try {
      const options = await this.api.registerOptions(this.token, displayName);
      stage = 'create';
      const credential = await this.passkeys.create(options);
      stage = 'register';
      const session = await this.api.register(this.token, credential);
      this.session.setUser(session.user);
      await this.router.navigateByUrl('/list');
    } catch (error) {
      if (statusOf(error) === HttpStatus.notFound) {
        this.step.set('used');
      } else {
        this.report(stage, error);
        if (error instanceof PasskeyUnsupportedError) {
          this.step.set('unsupported');
        } else {
          this.problem.set(describe(error));
          this.cancelled.set(error instanceof PasskeyCancelledError);
        }
      }
    } finally {
      this.busy.set(false);
    }
  }

  /** The link as it was sent: the address bar was wiped, so it is put back together from the token held here. */
  protected copyLink(): void {
    const {location} = this.document;
    if (this.clipboard.copy(`${location.origin}/join#${this.token}`)) {
      this.toast.success('Link copied');
    } else {
      this.toast.error('Could not copy. Open the message with the link in Safari or Chrome.');
    }
  }

  /** Writes the failure to the server's log; never blocks the screen and never throws. */
  private report(stage: PasskeyFailureReport['stage'], error: unknown): void {
    void (async () => {
      try {
        await this.api.reportPasskeyFailure({
          ...labelsOf(error),
          stage,
          httpStatus: statusOf(error) || null,
          platformAuthenticator: await this.passkeys.platformAuthenticatorAvailable(),
        });
      } catch {
        /* the report is best effort */
      }
    })();
  }
}
