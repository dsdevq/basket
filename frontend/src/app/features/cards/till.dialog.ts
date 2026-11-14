import {DialogRef} from '@angular/cdk/dialog';
import {ChangeDetectionStrategy, Component, inject, signal} from '@angular/core';
import {AlertComponent, ButtonComponent, CMN_DIALOG_DATA} from '@lifekit-hq/ui';

import {BarcodeComponent} from './barcode.component';
import {type Card, FORMAT_LABELS, groupedNumber} from './cards.model';

/** What the till dialog is opened with: a card whose number can be read. */
export interface TillData {
  readonly card: Card & {readonly number: string};
}

/**
 * The code for the till, covering the whole viewport at every width (it is opened in the bare dialog container, so
 * there is no shell to size it): a header with the shop's name and a close button, then the code as large as the
 * space allows (it is fitted to the smaller of the width and the height left, so the number under it never scrolls
 * out of sight on a short screen), and the number in groups for a cashier who has to key it in. The dialog supplies
 * the name, Escape and the focus handling; the close button is the first tabbable element, so focus lands on it.
 */
@Component({
  selector: 'bk-till',
  imports: [AlertComponent, BarcodeComponent, ButtonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {class: 'block h-full w-full'},
  template: `
    <div class="flex h-full w-full flex-col gap-cmn-3 bg-surface-card p-cmn-4 text-text-primary">
      <header class="flex shrink-0 items-center justify-between gap-cmn-3">
        <h2 class="m-0 text-cmn-lg font-medium">{{ card.name }} card</h2>
        <cmn-button (clicked)="close()" aria-label="Close" icon="X" variant="secondary" />
      </header>
      @if (unreadable()) {
        <cmn-alert title="This number does not fit the code" variant="error" />
      }
      <div class="min-h-0 flex-1">
        <bk-barcode
          [value]="card.number"
          [format]="card.format"
          [label]="card.name + ' card ' + formatLabel"
          (failed)="unreadable.set(true)"
          class="block h-full w-full [&_canvas]:h-full [&_canvas]:object-contain"
        />
      </div>
      <p
        class="m-0 shrink-0 break-all text-center font-mono text-cmn-lg text-text-primary"
        data-testid="till-number"
      >
        {{ spoken }}
      </p>
      <p class="m-0 shrink-0 text-center text-cmn-sm text-text-secondary">{{ formatLabel }}</p>
    </div>
  `,
})
export class TillDialogComponent {
  private readonly ref = inject(DialogRef);
  protected readonly card = inject<TillData>(CMN_DIALOG_DATA).card;
  protected readonly unreadable = signal(false);
  protected readonly formatLabel = FORMAT_LABELS[this.card.format];
  protected readonly spoken = groupedNumber(this.card.number);

  protected close(): void {
    this.ref.close();
  }
}
