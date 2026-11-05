import {ChangeDetectionStrategy, Component, inject} from '@angular/core';
import {ButtonComponent, CMN_DRAWER_DATA, CmnDrawerRef} from '@lifekit-hq/ui';

import type {Product} from './prices.model';

export interface ProductActionsData {
  readonly product: Product;
}

export type ProductAction = 'delete';

/** The bottom sheet behind a product's "more" button. */
@Component({
  selector: 'bk-product-actions-sheet',
  imports: [ButtonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-col gap-cmn-3 px-cmn-4 pb-cmn-4">
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
  protected readonly data = inject<ProductActionsData>(CMN_DRAWER_DATA);
  protected readonly ref = inject<CmnDrawerRef<ProductAction>>(CmnDrawerRef);
}
