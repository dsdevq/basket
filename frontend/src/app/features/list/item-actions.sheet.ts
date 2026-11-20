import {ChangeDetectionStrategy, Component, computed, inject, signal} from '@angular/core';
import {
  ButtonComponent,
  CMN_DRAWER_DATA,
  CmnDrawerRef,
  ListItemRowComponent,
  ListSectionComponent,
  TagComponent,
} from '@lifekit-hq/ui';

import {STORE_NAMES} from '../prices/price-format';
import type {StoreId} from '../prices/prices.model';
import type {ListItem} from './list.model';
import {ListStore} from './list.store';

const SHOPS = (Object.keys(STORE_NAMES) as StoreId[]).map(id => ({id, name: STORE_NAMES[id]}));

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
  imports: [ButtonComponent, ListItemRowComponent, ListSectionComponent, TagComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-col gap-cmn-3 px-cmn-6 pt-cmn-4 pb-cmn-6">
      <p class="text-cmn-xs text-text-secondary">
        Added by {{ data.item.addedBy.name }}
        @if (data.item.checkedBy) {
          · ticked by {{ data.item.checkedBy.name }}
        }
      </p>
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
          variant="primary"
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

  protected readonly data = inject<ItemActionsData>(CMN_DRAWER_DATA);
  protected readonly ref = inject<CmnDrawerRef<ItemAction>>(CmnDrawerRef);

  protected readonly shops = SHOPS;
  protected readonly busy = signal(false);

  /** The shop on record now, read from the list so a change shows before the sheet closes. */
  protected readonly current = computed(
    () => this.list.items().find(item => item.id === this.data.item.id)?.boughtStore ?? null
  );

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

  protected choose(action: ItemAction): void {
    this.ref.close(action);
  }
}
