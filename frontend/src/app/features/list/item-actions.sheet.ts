import {ChangeDetectionStrategy, Component, computed, inject, signal} from '@angular/core';
import {takeUntilDestroyed} from '@angular/core/rxjs-interop';
import {FormControl, FormGroup, ReactiveFormsModule, Validators} from '@angular/forms';
import {
  AlertComponent,
  ButtonComponent,
  CMN_DRAWER_DATA,
  CmnDrawerRef,
  FormFieldComponent,
  InputComponent,
  ListItemRowComponent,
  ListSectionComponent,
  TagComponent,
} from '@lifekit-hq/ui';

import {STORE_NAMES} from '../prices/price-format';
import type {StoreId} from '../prices/prices.model';
import {type ListItem, MAX_NOTE_LENGTH, MAX_QUANTITY} from './list.model';
import {ListStore} from './list.store';

const SHOPS = (Object.keys(STORE_NAMES) as StoreId[]).map(id => ({id, name: STORE_NAMES[id]}));

/** A blank quantity (one pack) or a whole number from 1 to 99. */
const QUANTITY = /^([1-9]\d?)?$/;

export interface ItemActionsData {
  readonly item: ListItem;
}

export type ItemAction = 'delete' | 'link';

/**
 * The bottom sheet behind an item's "more" button. HIG: actions on a thing live in a sheet, not a dialog. For a ticked item it also
 * says where it was bought (recorded at the tick: the shop it was picked from, else the one the plan suggested) and lets the
 * person change it, as a list of the four shops like the compare sheet's.
 */
@Component({
  selector: 'bk-item-actions-sheet',
  imports: [
    ReactiveFormsModule,
    AlertComponent,
    ButtonComponent,
    FormFieldComponent,
    InputComponent,
    ListItemRowComponent,
    ListSectionComponent,
    TagComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-col gap-cmn-3 px-cmn-6 pt-cmn-4 pb-cmn-6">
      <p class="text-cmn-xs text-text-secondary">
        Added by {{ data.item.addedBy.name }}
        @if (data.item.checkedBy) {
          · ticked by {{ data.item.checkedBy.name }}
        }
      </p>
      <form [formGroup]="form" (ngSubmit)="save()" class="flex flex-col gap-cmn-3">
        <cmn-form-field
          [control]="quantity"
          label="Quantity"
          hint="How many packs. The Plan prices them all."
        >
          <cmn-input
            formControlName="quantity"
            type="number"
            inputmode="numeric"
            enterkeyhint="next"
            placeholder="1"
            autocomplete="off"
          />
        </cmn-form-field>
        @if (quantityProblem(); as problem) {
          <cmn-alert [title]="problem" variant="error" />
        }
        <cmn-form-field [control]="note" label="Note">
          <cmn-input
            formControlName="note"
            enterkeyhint="done"
            placeholder="e.g. the green ones"
            autocomplete="off"
          />
        </cmn-form-field>
        <cmn-button
          [disabled]="!!quantityProblem() || noteTooLong()"
          [loading]="saving()"
          class="block [&_button]:w-full"
          type="submit"
          variant="primary"
          size="lg"
          icon="Check"
        >
          Save
        </cmn-button>
      </form>
      @if (data.item.checked) {
        <cmn-list-section heading="Bought at" card>
          @for (shop of shops; track shop.id) {
            <cmn-list-item-row
              [label]="shop.name"
              [action]="!busy()"
              (activated)="boughtAt(shop.id)"
            >
              @if (shop.id === current()) {
                <cmn-tag variant="info" meta>Bought here</cmn-tag>
              }
            </cmn-list-item-row>
          }
        </cmn-list-section>
      }
      @if (!data.item.productId) {
        <cmn-button
          (clicked)="choose('link')"
          class="block [&_button]:w-full"
          variant="secondary"
          size="lg"
          icon="Link"
        >
          Link to a product
        </cmn-button>
      }
      <cmn-button
        (clicked)="choose('delete')"
        class="block [&_button]:w-full"
        variant="destructive"
        size="lg"
        icon="Trash2"
      >
        Delete from list
      </cmn-button>
      <cmn-button
        (clicked)="ref.close()"
        class="block [&_button]:w-full"
        variant="secondary"
        size="lg"
        >Cancel</cmn-button
      >
    </div>
  `,
})
export class ItemActionsSheetComponent {
  private readonly list = inject(ListStore);
  private readonly line = inject<ItemActionsData>(CMN_DRAWER_DATA).item;
  private readonly quantityText = signal(this.line.quantity?.toString() ?? '');
  private readonly noteText = signal(this.line.note ?? '');

  protected readonly data = inject<ItemActionsData>(CMN_DRAWER_DATA);
  protected readonly ref = inject<CmnDrawerRef<ItemAction>>(CmnDrawerRef);

  protected readonly quantity = new FormControl(this.data.item.quantity?.toString() ?? '', {
    nonNullable: true,
    validators: Validators.pattern(QUANTITY),
  });
  protected readonly note = new FormControl(this.data.item.note ?? '', {nonNullable: true});
  protected readonly form = new FormGroup({quantity: this.quantity, note: this.note});
  protected readonly saving = signal(false);

  /** Why the quantity cannot be saved, or null: blank is one pack, otherwise a whole number from 1 to the cap. */
  protected readonly quantityProblem = computed(() => {
    const text = this.quantityText().trim();
    if (!text) {
      return null;
    }
    return QUANTITY.test(text) ? null : `Enter a whole number from 1 to ${MAX_QUANTITY}.`;
  });
  protected readonly noteTooLong = computed(() => this.noteText().trim().length > MAX_NOTE_LENGTH);

  protected readonly shops = SHOPS;
  protected readonly busy = signal(false);

  /** The shop on record now, read from the list so a change shows before the sheet closes. */
  protected readonly current = computed(
    () => this.list.items().find(item => item.id === this.data.item.id)?.boughtStore ?? null
  );

  constructor() {
    this.quantity.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe(value => this.quantityText.set(String(value ?? '')));
    this.note.valueChanges.pipe(takeUntilDestroyed()).subscribe(value => this.noteText.set(value));
  }

  protected async boughtAt(shop: StoreId): Promise<void> {
    if (this.busy() || shop === this.current()) {
      return;
    }
    this.busy.set(true);
    const saved = await this.list.setBoughtStore(this.data.item, shop);
    this.busy.set(false);
    if (saved) {
      this.ref.close();
    }
  }

  protected async save(): Promise<void> {
    if (this.saving() || this.quantityProblem() || this.noteTooLong()) {
      return;
    }
    const text = String(this.quantity.value ?? '').trim();
    this.saving.set(true);
    const saved = await this.list.setDetails(
      this.data.item,
      text ? Number(text) : null,
      this.note.value.trim() || null
    );
    this.saving.set(false);
    if (saved) {
      this.ref.close();
    }
  }

  protected choose(action: ItemAction): void {
    this.ref.close(action);
  }
}
