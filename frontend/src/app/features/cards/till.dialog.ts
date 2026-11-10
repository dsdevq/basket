import {ChangeDetectionStrategy, Component, inject, signal} from '@angular/core';
import {AlertComponent, CMN_DIALOG_DATA} from '@lifekit-hq/ui';

import {BarcodeComponent} from './barcode.component';
import {type Card, FORMAT_LABELS, groupedNumber} from './cards.model';

/** What the till dialog is opened with: a card whose number can be read. */
export interface TillData {
  readonly card: Card & {readonly number: string};
}

/**
 * The code for the till, filling the screen on a phone (the dialog's full-screen presentation) and a near-full
 * dialog on a wide one: the code as large as the width allows on its own white ground, the number under it for a
 * cashier who has to key it in. The dialog supplies the name, the close button, Escape and the focus handling.
 */
@Component({
  selector: 'bk-till',
  imports: [AlertComponent, BarcodeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="mx-auto flex w-full max-w-2xl flex-col gap-cmn-4">
      @if (unreadable()) {
        <cmn-alert title="This number does not fit the code" variant="error" />
      }
      <bk-barcode
        [value]="card.number"
        [format]="card.format"
        [label]="card.name + ' card ' + formatLabel"
        (failed)="unreadable.set(true)"
      />
      <p
        class="break-all text-center font-mono text-cmn-xl text-text-primary"
        data-testid="till-number"
      >
        {{ spoken }}
      </p>
      <p class="text-center text-cmn-sm text-text-secondary">{{ formatLabel }}</p>
    </div>
  `,
})
export class TillDialogComponent {
  protected readonly card = inject<TillData>(CMN_DIALOG_DATA).card;
  protected readonly unreadable = signal(false);
  protected readonly formatLabel = FORMAT_LABELS[this.card.format];
  protected readonly spoken = groupedNumber(this.card.number);
}
