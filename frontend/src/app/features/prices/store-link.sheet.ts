import {ChangeDetectionStrategy, Component, inject, signal} from '@angular/core';
import {FormControl, FormGroup, ReactiveFormsModule} from '@angular/forms';
import {
  AlertComponent,
  ButtonComponent,
  CMN_DRAWER_DATA,
  CmnDrawerRef,
  FormFieldComponent,
  InputComponent,
} from '@lifekit-hq/ui';

import type {Product, ProductStore} from './prices.model';
import {PricesStore} from './prices.store';

export interface StoreLinkData {
  readonly product: Product;
  readonly entry: ProductStore;
}

const EXAMPLES = {
  lidl: 'https://www.lidl.ie/p/aubergine/p11214436',
  aldi: 'https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541',
  tesco: 'https://www.tesco.ie/groceries/en-IE/products/254317390',
  dunnes: 'https://www.dunnesstoresgrocery.com/product/dunnes-stores-swede-100317581',
} as const;

/** Bottom sheet: paste the address of the product's page in one shop (or remove the link). */
@Component({
  selector: 'bk-store-link-sheet',
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
        [control]="url"
        [label]="data.entry.name + ' page'"
        hint="Open the product on the shop's website, copy the address, paste it here"
      >
        <cmn-input
          [placeholder]="example"
          formControlName="url"
          inputmode="url"
          enterkeyhint="done"
          autocomplete="off"
        />
      </cmn-form-field>
      @if (data.entry.listing?.title; as title) {
        <p class="text-cmn-xs text-text-secondary">Now linked to {{ title }}.</p>
      }
      <cmn-button
        [loading]="busy()"
        class="block [&_button]:w-full"
        type="submit"
        variant="primary"
        size="lg"
        icon="Link"
      >
        {{ data.entry.listing ? 'Save page' : 'Link page' }}
      </cmn-button>
      @if (data.entry.listing) {
        <cmn-button
          (clicked)="remove()"
          class="block [&_button]:w-full"
          variant="destructive"
          size="lg"
          icon="Unlink"
          >Remove link</cmn-button
        >
      }
    </form>
  `,
})
export class StoreLinkSheetComponent {
  private readonly prices = inject(PricesStore);
  protected readonly data = inject<StoreLinkData>(CMN_DRAWER_DATA);
  protected readonly ref = inject<CmnDrawerRef<boolean>>(CmnDrawerRef);

  protected readonly example = EXAMPLES[this.data.entry.store];
  protected readonly url = new FormControl(this.data.entry.listing?.url ?? '', {nonNullable: true});
  protected readonly form = new FormGroup({url: this.url});
  protected readonly busy = signal(false);
  protected readonly problem = signal('');

  protected async save(): Promise<void> {
    const value = this.url.value.trim();
    if (!value) {
      this.problem.set(`Paste the address of the ${this.data.entry.name} page first.`);
      return;
    }
    this.busy.set(true);
    this.problem.set('');
    const refusal = await this.prices.link(this.data.product, this.data.entry.store, value);
    this.busy.set(false);
    if (refusal) {
      this.problem.set(refusal);
    } else {
      this.ref.close(true);
    }
  }

  protected async remove(): Promise<void> {
    await this.prices.unlink(this.data.product, this.data.entry.store);
    this.ref.close(true);
  }
}
