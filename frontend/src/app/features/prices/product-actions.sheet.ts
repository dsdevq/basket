import {ChangeDetectionStrategy, Component, computed, inject, signal} from '@angular/core';
import {FormControl, FormGroup, ReactiveFormsModule} from '@angular/forms';
import {
  ButtonComponent,
  CMN_DRAWER_DATA,
  CmnDrawerRef,
  InputComponent,
  ListItemRowComponent,
  ListSectionComponent,
} from '@lifekit-hq/ui';

import type {Alias, Product} from './prices.model';
import {PricesStore} from './prices.store';

export interface ProductActionsData {
  readonly product: Product;
}

export type ProductAction = 'delete';

/**
 * The bottom sheet behind a product's "more" button: its other names (what the household calls it, added, renamed or deleted here)
 * and Delete. A name links a typed list line to the product, and an English one also helps the shop matcher.
 */
@Component({
  selector: 'bk-product-actions-sheet',
  imports: [
    ReactiveFormsModule,
    ButtonComponent,
    InputComponent,
    ListItemRowComponent,
    ListSectionComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-col gap-cmn-3 px-cmn-6 pt-cmn-4 pb-cmn-6">
      <cmn-list-section heading="Other names" card>
        @for (alias of aliases(); track alias.id) {
          <cmn-list-item-row
            [label]="alias.text"
            [action]="!busy()"
            (activated)="edit(alias)"
            class="[&_.truncate]:overflow-visible [&_.truncate]:whitespace-normal [&_.truncate]:break-words"
          >
            <cmn-button
              [aria-label]="'Delete the name ' + alias.text"
              [disabled]="busy()"
              (clicked)="remove(alias)"
              actions
              variant="ghost"
              icon="Trash2"
            />
          </cmn-list-item-row>
        }
      </cmn-list-section>
      @if (!aliases().length) {
        <p class="text-cmn-xs text-text-secondary">
          No other names yet. A name you add links a typed line to {{ data.product.name }}.
        </p>
      }
      <form [formGroup]="form" (ngSubmit)="save()" class="flex items-center gap-cmn-2">
        <cmn-input
          [aria-label]="editing() ? 'Rename ' + editing()?.text : 'Add another name'"
          [placeholder]="editing() ? 'New name' : 'Add another name'"
          formControlName="text"
          class="min-w-0 flex-1"
          enterkeyhint="done"
          autocomplete="off"
        />
        <cmn-button [loading]="busy()" type="submit" variant="primary">{{
          editing() ? 'Save' : 'Add'
        }}</cmn-button>
      </form>
      @if (editing()) {
        <cmn-button (clicked)="cancel()" class="block [&_button]:w-full" variant="ghost"
          >Keep “{{ editing()?.text }}”</cmn-button
        >
      }
      @if (error(); as message) {
        <p class="text-cmn-xs text-text-error" role="alert">{{ message }}</p>
      }
      <p class="text-cmn-xs text-text-secondary">
        Deleting {{ data.product.name }} also removes its shop links and price history.
      </p>
      <cmn-button
        (clicked)="ref.close('delete')"
        class="block [&_button]:w-full"
        variant="destructive"
        size="lg"
        icon="Trash2"
      >
        Delete product
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
export class ProductActionsSheetComponent {
  private readonly prices = inject(PricesStore);

  protected readonly data = inject<ProductActionsData>(CMN_DRAWER_DATA);
  protected readonly ref = inject<CmnDrawerRef<ProductAction>>(CmnDrawerRef);

  protected readonly text = new FormControl('', {nonNullable: true});
  protected readonly form = new FormGroup({text: this.text});
  protected readonly editing = signal<Alias | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  /** The names on record now, read from the store so an add, rename or delete shows before anything closes. */
  protected readonly aliases = computed(
    () => this.prices.products().find(product => product.id === this.data.product.id)?.aliases ?? []
  );

  protected edit(alias: Alias): void {
    this.editing.set(alias);
    this.text.setValue(alias.text);
    this.error.set(null);
  }

  protected cancel(): void {
    this.editing.set(null);
    this.text.reset('');
    this.error.set(null);
  }

  protected async save(): Promise<void> {
    const value = this.text.value.trim();
    if (!value || this.busy()) {
      return;
    }
    this.busy.set(true);
    const editing = this.editing();
    const refusal = editing
      ? await this.prices.renameAlias(this.data.product, editing, value)
      : await this.prices.addAlias(this.data.product, value);
    this.busy.set(false);
    this.error.set(refusal);
    if (!refusal) {
      this.cancel();
    }
  }

  protected async remove(alias: Alias): Promise<void> {
    this.busy.set(true);
    await this.prices.removeAlias(this.data.product, alias);
    this.busy.set(false);
    if (this.editing()?.id === alias.id) {
      this.cancel();
    }
  }
}
