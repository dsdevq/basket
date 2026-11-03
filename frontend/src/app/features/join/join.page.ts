import {DOCUMENT} from '@angular/common';
import {ChangeDetectionStrategy, Component, inject, type OnInit, signal} from '@angular/core';
import {FormControl, FormGroup, ReactiveFormsModule, Validators} from '@angular/forms';
import {Router} from '@angular/router';
import {AlertComponent, ButtonComponent, FormFieldComponent, InputComponent} from '@lifekit-hq/ui';

import {AuthApi} from '../../core/auth/auth.api';
import {AuthCardComponent} from '../../core/auth/auth-card.component';
import {
  PasskeyCancelledError,
  PasskeyClient,
  PasskeyUnsupportedError,
} from '../../core/auth/passkey.client';
import {SessionStore} from '../../core/auth/session.store';
import {HttpStatus, MAX_NAME_LENGTH, statusOf} from '../../core/http-status';

function describe(error: unknown): string {
  if (error instanceof PasskeyCancelledError) {
    return 'The passkey was not created. Try again when you are ready.';
  }
  if (error instanceof PasskeyUnsupportedError) {
    return 'This browser cannot make passkeys. Open the link in Safari or Chrome.';
  }
  if (statusOf(error) === HttpStatus.tooManyRequests) {
    return 'Too many tries. Wait a minute and try again.';
  }
  return 'Could not create the passkey. Check your connection and try again.';
}

type Step = 'checking' | 'ready' | 'invalid';

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
      @case ('invalid') {
        <bk-auth-card
          heading="This link is not valid any more"
          lead="Invite links work once and last 24 hours."
        >
          <p class="text-center text-cmn-sm text-text-secondary">Ask for a new one.</p>
        </bk-auth-card>
      }
      @default {
        <bk-auth-card heading="Join basket" lead="Make a passkey and you are in. No password.">
          @if (problem()) {
            <cmn-alert [title]="problem()" variant="error" />
          }
          <form [formGroup]="form" (ngSubmit)="register()" class="flex flex-col gap-cmn-4">
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
            <cmn-button
              [loading]="busy()"
              class="block [&_button]:w-full"
              type="submit"
              variant="primary"
              size="lg"
              icon="KeyRound"
            >
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

  private token = '';

  protected readonly step = signal<Step>('checking');
  protected readonly busy = signal(false);
  protected readonly problem = signal('');
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
      this.step.set('invalid');
      return;
    }
    try {
      await this.api.checkInvite(this.token);
      this.step.set('ready');
    } catch {
      this.step.set('invalid');
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
    try {
      const options = await this.api.registerOptions(this.token, displayName);
      const credential = await this.passkeys.create(options);
      const session = await this.api.register(this.token, credential);
      this.session.setUser(session.user);
      await this.router.navigateByUrl('/list');
    } catch (error) {
      if (statusOf(error) === HttpStatus.notFound) {
        this.step.set('invalid');
      } else {
        this.problem.set(describe(error));
      }
    } finally {
      this.busy.set(false);
    }
  }
}
