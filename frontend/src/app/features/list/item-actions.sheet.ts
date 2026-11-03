import {ChangeDetectionStrategy, Component, inject} from '@angular/core';
import {ButtonComponent, CMN_DRAWER_DATA, CmnDrawerRef} from '@lifekit-hq/ui';

import type {ListItem} from './list.model';

export interface ItemActionsData {
  readonly item: ListItem;
}

export type ItemAction = 'delete';

/** The bottom sheet behind an item's "more" button. HIG: actions on a thing live in a sheet, not a dialog. */
@Component({
  selector: 'bk-item-actions-sheet',
  imports: [ButtonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-col gap-cmn-3 px-cmn-4 pb-cmn-4">
      <p class="text-cmn-xs text-text-secondary">
        Added by {{ data.item.addedBy.name }}
        @if (data.item.checkedBy) {
          · ticked by {{ data.item.checkedBy.name }}
        }
      </p>
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
  protected readonly data = inject<ItemActionsData>(CMN_DRAWER_DATA);
  protected readonly ref = inject<CmnDrawerRef<ItemAction>>(CmnDrawerRef);

  protected choose(action: ItemAction): void {
    this.ref.close(action);
  }
}
