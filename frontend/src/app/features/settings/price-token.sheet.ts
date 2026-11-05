import {Clipboard} from '@angular/cdk/clipboard';
import {ChangeDetectionStrategy, Component, inject, signal} from '@angular/core';
import {FormControl, FormGroup, ReactiveFormsModule} from '@angular/forms';
import {
  AlertComponent,
  ButtonComponent,
  CmnDrawerRef,
  FormFieldComponent,
  InputComponent,
  ToastService,
} from '@lifekit-hq/ui';

import type {CreatedIngestToken} from '../../core/auth/session.model';
import {HttpStatus, statusOf} from '../../core/http-status';
import {PriceRelayApi} from './price-relay.api';

/** Bottom sheet: name the reader, get a token to paste into it. The token is shown this once. */
@Component({
  selector: 'bk-price-token-sheet',
  imports: [
    ReactiveFormsModule,
    AlertComponent,
    ButtonComponent,
    FormFieldComponent,
    InputComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (created(); as made) {
      <div class="flex flex-col gap-cmn-3 px-cmn-4 pb-cmn-4">
        <p class="text-cmn-sm text-text-primary">
          Paste this into the price reader on {{ made.label || 'your PC' }}. basket will not show it
          again, so copy it now. It can only send Tesco and Aldi prices; it can read nothing.
        </p>
        <p
          class="break-all rounded-cmn-md bg-surface-raised p-cmn-3 text-cmn-xs text-text-secondary"
          data-testid="token"
        >
          {{ made.token }}
        </p>
        <cmn-button
          (clicked)="copy(made)"
          class="block [&_button]:w-full"
          variant="primary"
          size="lg"
          icon="Copy"
          >Copy token</cmn-button
        >
        <cmn-button
          (clicked)="ref.close(true)"
          class="block [&_button]:w-full"
          variant="secondary"
          size="lg"
          >Done</cmn-button
        >
      </div>
    } @else {
      <form
        [formGroup]="form"
        (ngSubmit)="create()"
        class="flex flex-col gap-cmn-3 px-cmn-4 pb-cmn-4"
      >
        @if (problem()) {
          <cmn-alert [title]="problem()" variant="error" />
        }
        <cmn-form-field
          [control]="label"
          label="Which reader is it for?"
          hint="Only you see this, to tell tokens apart"
        >
          <cmn-input formControlName="label" enterkeyhint="done" placeholder="e.g. Home PC" />
        </cmn-form-field>
        <cmn-button
          [loading]="busy()"
          class="block [&_button]:w-full"
          type="submit"
          variant="primary"
          size="lg"
          icon="KeyRound"
        >
          Make token
        </cmn-button>
      </form>
    }
  `,
})
export class PriceTokenSheetComponent {
  private readonly api = inject(PriceRelayApi);
  private readonly clipboard = inject(Clipboard);
  private readonly toast = inject(ToastService);
  protected readonly ref = inject<CmnDrawerRef<boolean>>(CmnDrawerRef);

  protected readonly label = new FormControl('', {nonNullable: true});
  protected readonly form = new FormGroup({label: this.label});
  protected readonly busy = signal(false);
  protected readonly problem = signal('');
  protected readonly created = signal<CreatedIngestToken | null>(null);

  protected async create(): Promise<void> {
    this.busy.set(true);
    this.problem.set('');
    try {
      this.created.set(await this.api.createToken(this.label.value.trim()));
    } catch (error) {
      this.problem.set(
        statusOf(error) === HttpStatus.conflict
          ? 'basket keeps five price tokens at most. Revoke one first.'
          : 'Could not make the token. Try again.'
      );
    } finally {
      this.busy.set(false);
    }
  }

  protected copy(made: CreatedIngestToken): void {
    if (this.clipboard.copy(made.token)) {
      this.toast.success('Token copied');
    } else {
      this.toast.error('Could not copy. Select the token and copy it by hand.');
    }
  }
}
