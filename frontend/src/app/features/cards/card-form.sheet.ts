import {ChangeDetectionStrategy, Component, inject, signal} from '@angular/core';
import {FormControl, FormGroup, ReactiveFormsModule} from '@angular/forms';
import {
  AlertComponent,
  ButtonComponent,
  CMN_DRAWER_DATA,
  CmnDrawerRef,
  FormFieldComponent,
  InputComponent,
  SegmentedComponent,
  type SegmentedOption,
} from '@lifekit-hq/ui';

import {HttpStatus, statusOf} from '../../core/http-status';
import {CardsApi} from './cards.api';
import {type Card, type CardFormat, type Chain, FORMAT_LABELS} from './cards.model';

export interface CardFormData {
  readonly chain: Chain;
  /** The card being changed, or null when the shop has none yet. */
  readonly existing: Card | null;
}

const FORMATS: readonly SegmentedOption[] = (Object.keys(FORMAT_LABELS) as CardFormat[]).map(
  value => ({value, label: FORMAT_LABELS[value]})
);

/** Bottom sheet: type a shop's card number once and pick the code it is drawn as (Stocard's "add card"). */
@Component({
  selector: 'bk-card-form-sheet',
  imports: [
    ReactiveFormsModule,
    AlertComponent,
    ButtonComponent,
    FormFieldComponent,
    InputComponent,
    SegmentedComponent,
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
        [control]="number"
        [label]="data.chain.name + ' card'"
        [hint]="data.chain.hint"
      >
        <cmn-input
          formControlName="number"
          enterkeyhint="done"
          autocomplete="off"
          autocapitalize="off"
          spellcheck="false"
        />
      </cmn-form-field>
      <cmn-segmented
        [options]="formats"
        [value]="format()"
        (valueChange)="format.set($event)"
        label="Code"
      />
      <p class="text-cmn-xs text-text-secondary">
        Most shop cards use Code 128. Change the code only if the card shows another.
      </p>
      <cmn-button
        [loading]="busy()"
        class="block [&_button]:w-full"
        type="submit"
        variant="primary"
        size="lg"
        icon="WalletCards"
      >
        {{ data.existing ? 'Save card' : 'Add card' }}
      </cmn-button>
    </form>
  `,
})
export class CardFormSheetComponent {
  private readonly api = inject(CardsApi);
  protected readonly data = inject<CardFormData>(CMN_DRAWER_DATA);
  protected readonly ref = inject<CmnDrawerRef<Card>>(CmnDrawerRef);

  protected readonly formats = FORMATS;
  protected readonly number = new FormControl(this.data.existing?.number ?? '', {
    nonNullable: true,
  });
  protected readonly form = new FormGroup({number: this.number});
  protected readonly format = signal<string>(
    this.data.existing?.format ?? this.data.chain.defaultFormat
  );
  protected readonly busy = signal(false);
  protected readonly problem = signal('');

  protected async save(): Promise<void> {
    const value = this.number.value.trim();
    if (!value) {
      this.problem.set(`Type the ${this.data.chain.name} card number first.`);
      return;
    }
    this.busy.set(true);
    this.problem.set('');
    try {
      this.ref.close(
        await this.api.save(this.data.chain.store, value, this.format() as CardFormat)
      );
    } catch (error) {
      const message = (error as {error?: {message?: string}} | null)?.error?.message;
      const refused =
        statusOf(error) === HttpStatus.badRequest || statusOf(error) === HttpStatus.conflict;
      this.problem.set(
        refused && message
          ? message
          : 'Could not save the card. Check your connection and try again.'
      );
    } finally {
      this.busy.set(false);
    }
  }
}
