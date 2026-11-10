import {ChangeDetectionStrategy, Component, inject, signal} from '@angular/core';
import {FormControl, FormGroup, ReactiveFormsModule} from '@angular/forms';
import {
  AlertComponent,
  ButtonComponent,
  CmnDrawerRef,
  FormFieldComponent,
  InputComponent,
} from '@lifekit-hq/ui';

import {HttpStatus, statusOf} from '../../core/http-status';
import {LidlPlusApi} from './lidl-plus.api';
import type {LidlPlusStatus} from './lidl-plus.model';

/** The statuses whose message the server wrote to be read: a malformed token, a token Lidl refused, Lidl unreachable. */
const READABLE = new Set<number>([
  HttpStatus.badRequest,
  HttpStatus.unprocessable,
  HttpStatus.badGateway,
]);

/**
 * Bottom sheet: paste the Lidl Plus refresh token once. basket asks Lidl whether it works before keeping it, stores it
 * encrypted and never shows it again, so the field is masked and the token is never echoed back in a message.
 */
@Component({
  selector: 'bk-lidl-plus-token-sheet',
  imports: [
    ReactiveFormsModule,
    AlertComponent,
    ButtonComponent,
    FormFieldComponent,
    InputComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form
      [formGroup]="form"
      (ngSubmit)="save()"
      class="flex flex-col gap-cmn-3 px-cmn-6 pt-cmn-4 pb-cmn-6"
    >
      @if (problem()) {
        <cmn-alert [title]="problem()" variant="error" />
      }
      <cmn-form-field
        [control]="token"
        label="Lidl Plus refresh token"
        hint="basket checks it with Lidl, keeps it encrypted and never shows it again"
      >
        <cmn-input
          formControlName="token"
          type="password"
          enterkeyhint="done"
          autocomplete="off"
          autocapitalize="off"
          spellcheck="false"
        />
      </cmn-form-field>
      <p class="text-cmn-xs text-text-secondary">
        Use the refresh token from signing in to Lidl Plus once, for example with the lidl-plus
        tool. It is a password for your Lidl Plus account: paste it only here.
      </p>
      <cmn-button
        [loading]="busy()"
        class="block [&_button]:w-full"
        type="submit"
        variant="primary"
        size="lg"
        icon="KeyRound"
      >
        Connect
      </cmn-button>
    </form>
  `,
})
export class LidlPlusTokenSheetComponent {
  private readonly api = inject(LidlPlusApi);
  protected readonly ref = inject<CmnDrawerRef<LidlPlusStatus>>(CmnDrawerRef);

  protected readonly token = new FormControl('', {nonNullable: true});
  protected readonly form = new FormGroup({token: this.token});
  protected readonly busy = signal(false);
  protected readonly problem = signal('');

  protected async save(): Promise<void> {
    const value = this.token.value.trim();
    if (!value) {
      this.problem.set('Paste the refresh token first.');
      return;
    }
    this.busy.set(true);
    this.problem.set('');
    try {
      this.ref.close(await this.api.connect(value));
    } catch (error) {
      const message = (error as {error?: {message?: string}} | null)?.error?.message;
      this.problem.set(
        READABLE.has(statusOf(error)) && message
          ? message
          : 'Could not connect. Check your connection and try again.'
      );
    } finally {
      this.busy.set(false);
    }
  }
}
