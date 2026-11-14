import {ChangeDetectionStrategy, Component, computed, inject, signal} from '@angular/core';
import {
  CMN_DRAWER_DATA,
  CmnDrawerRef,
  IconComponent,
  ListItemRowComponent,
  ListSectionComponent,
  TagComponent,
} from '@lifekit-hq/ui';

import {ListStore} from '../list/list.store';
import {ListPhotoComponent} from '../list/list-photo.component';
import type {Product, StoreId} from '../prices/prices.model';
import {type CompareRow, compareRows, dublinToday} from './compare-format';

export interface CompareSheetData {
  readonly product: Product;
}

/**
 * The compare sheet: one product across the four shops while the list is being formed. Each shop is a row with its photo of the
 * product, the price and the day it was read, the per-store price row of price-comparison grocery apps. A stale or missing price
 * carries a flag and is never filled in. Tapping a shop puts the product on the list there (the shop shows as the line's chip);
 * "Leave it to the plan" puts it on with no shop, and the Plan tab names the shop. It is a bottom sheet, like the match sheet,
 * and one list of rows, so that leaving the choice to the plan sits first, not below the fold.
 */
@Component({
  selector: 'bk-compare-sheet',
  imports: [
    IconComponent,
    ListItemRowComponent,
    ListPhotoComponent,
    ListSectionComponent,
    TagComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-col gap-cmn-3 px-cmn-6 pt-cmn-4 pb-cmn-6">
      <p class="text-cmn-xs text-text-secondary">
        Prices as last read. Tap a shop to put {{ data.product.name }} on the list there.
      </p>
      <cmn-list-section heading="Choose a shop" card>
        <cmn-list-item-row
          [action]="!busy()"
          (activated)="choose(null)"
          label="Leave it to the plan"
          sublabel="The Plan tab names the shop"
        >
          <div
            avatar
            class="flex size-10 shrink-0 items-center justify-center rounded-cmn-md bg-surface-raised text-text-secondary"
            aria-hidden="true"
          >
            <cmn-icon name="ListChecks" />
          </div>
          @if (onListWithoutShop()) {
            <cmn-tag variant="info" meta>On the list</cmn-tag>
          }
        </cmn-list-item-row>
        @for (row of rows; track row.store) {
          <cmn-list-item-row
            [label]="row.name"
            [sublabel]="row.lines"
            [dimmed]="!row.choosable"
            [action]="row.choosable && !busy()"
            (activated)="choose(row.store)"
            class="[&_.truncate]:overflow-visible [&_.truncate]:whitespace-pre-line [&_.truncate]:break-words"
          >
            <bk-list-photo [name]="row.name" [src]="row.imageUrl" avatar />
            @if (row.tag; as text) {
              <cmn-tag [variant]="row.tagVariant" meta>{{ text }}</cmn-tag>
            }
            @if (onListAt().has(row.store)) {
              <cmn-tag variant="info" meta>On the list</cmn-tag>
            }
            <span amount class="flex flex-col items-end text-cmn-sm">
              <span class="font-semibold text-text-primary">{{ row.price }}</span>
              @if (row.was; as old) {
                <s class="text-cmn-xs text-text-secondary"
                  ><span class="sr-only">was </span>{{ old }}</s
                >
              }
            </span>
          </cmn-list-item-row>
        }
      </cmn-list-section>
    </div>
  `,
})
export class CompareSheetComponent {
  private readonly list = inject(ListStore);
  protected readonly data = inject<CompareSheetData>(CMN_DRAWER_DATA);
  protected readonly ref = inject<CmnDrawerRef<boolean>>(CmnDrawerRef);

  protected readonly rows: CompareRow[] = compareRows(this.data.product, dublinToday());
  protected readonly busy = signal(false);

  /** The shops the product already waits at on the list, so a row says so. */
  protected readonly onListAt = computed<ReadonlySet<StoreId>>(
    () =>
      new Set(
        this.list
          .toBuy()
          .flatMap(item =>
            item.productId === this.data.product.id && item.store ? [item.store] : []
          )
      )
  );

  /** The product already waits on the list with no shop, which is where "Leave it to the plan" leaves it. */
  protected readonly onListWithoutShop = computed(() =>
    this.list.toBuy().some(item => item.productId === this.data.product.id && !item.store)
  );

  protected async choose(shop: StoreId | null): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    const saved = await this.list.setProductStore(this.data.product.id, shop);
    this.busy.set(false);
    if (saved) {
      this.ref.close(true);
    }
  }
}
