import {ChangeDetectionStrategy, Component, inject, signal} from '@angular/core';
import {FormControl, FormGroup, ReactiveFormsModule} from '@angular/forms';
import {
  AlertComponent,
  ButtonComponent,
  CmnDrawerRef,
  FormFieldComponent,
  InputComponent,
} from '@lifekit-hq/ui';

import {AuthApi} from '../../core/auth/auth.api';
import {
  PasskeyCancelledError,
  PasskeyClient,
  PasskeyUnsupportedError,
} from '../../core/auth/passkey.client';
import {HttpStatus, statusOf} from '../../core/http-status';

function describe(error: unknown): string {
  if (error instanceof PasskeyCancelledError) {
    return 'The passkey was not created. Try again when you are ready.';
  }
  if (error instanceof PasskeyUnsupportedError) {
    return 'This browser cannot make passkeys. Open basket in Safari or Chrome.';
  }
  if (statusOf(error) === HttpStatus.conflict) {
    return 'You already have the most passkeys basket keeps. Remove one first.';
  }
  if (statusOf(error) === HttpStatus.tooManyRequests) {
    return 'Too many tries. Wait a minute and try again.';
  }
  if (statusOf(error) === HttpStatus.badRequest) {
    return 'That device already has a passkey here, or the prompt timed out. Try again.';
  }
  return 'Could not add the passkey. Check your connection and try again.';
}

/** Bottom sheet: name the device, then the browser's own passkey prompt makes the credential. */
@Component({
  selector: 'bk-add-passkey-sheet',
  imports: [
    ReactiveFormsModule,
    AlertComponent,
    ButtonComponent,
    FormFieldComponent,
    InputComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form [formGroup]="form" (ngSubmit)="add()" class="flex flex-col gap-cmn-3 px-cmn-4 pb-cmn-4">
      @if (problem()) {
        <cmn-alert [title]="problem()" variant="error" />
      }
      <cmn-form-field
        [control]="name"
        label="Which device is it?"
        hint="Only you see this, to tell your passkeys apart"
      >
        <cmn-input formControlName="name" enterkeyhint="done" placeholder="e.g. New phone" />
      </cmn-form-field>
      <cmn-button
        [loading]="busy()"
        class="block [&_button]:w-full"
        type="submit"
        variant="primary"
        size="lg"
        icon="FingerprintPattern"
      >
        Add passkey
      </cmn-button>
    </form>
  `,
})
export class AddPasskeySheetComponent {
  private readonly api = inject(AuthApi);
  private readonly passkeys = inject(PasskeyClient);
  private readonly ref = inject<CmnDrawerRef<boolean>>(CmnDrawerRef);

  protected readonly name = new FormControl('', {nonNullable: true});
  protected readonly form = new FormGroup({name: this.name});
  protected readonly busy = signal(false);
  protected readonly problem = signal('');

  protected async add(): Promise<void> {
    this.busy.set(true);
    this.problem.set('');
    try {
      const options = await this.api.passkeyOptions();
      const credential = await this.passkeys.create(options);
      await this.api.addPasskey(credential, this.name.value.trim());
      this.ref.close(true);
    } catch (error) {
      this.problem.set(describe(error));
    } finally {
      this.busy.set(false);
    }
  }
}
