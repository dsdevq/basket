import {DatePipe} from '@angular/common';
import {ChangeDetectionStrategy, Component, inject, type OnInit} from '@angular/core';
import {Router} from '@angular/router';
import {
  ButtonComponent,
  EmptyStateComponent,
  ListItemRowComponent,
  ListSectionComponent,
  PageComponent,
  SkeletonComponent,
} from '@lifekit-hq/ui';

import {euro} from '../prices/price-format';
import type {Trip, TripItem} from './history.model';
import {HistoryStore} from './history.store';

/**
 * Past shopping trips, newest first (AnyList and OurGroceries keep the same history): the day each was closed, what
 * was bought and who ticked it. "Same as last time" puts a trip's items back on the list. A typed one-off offers
 * "Add to shelf", the way Bring! lets a custom item you typed be saved into your own catalogue once you have it.
 */
@Component({
  selector: 'bk-history-page',
  imports: [
    DatePipe,
    ButtonComponent,
    EmptyStateComponent,
    ListItemRowComponent,
    ListSectionComponent,
    PageComponent,
    SkeletonComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-page kind="list">
      @switch (store.status()) {
        @case ('loading') {
          <cmn-skeleton shape="section" />
        }
        @case ('error') {
          <cmn-empty-state
            (action)="store.load()"
            kind="error"
            message="The history did not load"
            subMessage="Check your connection."
            actionLabel="Try again"
          />
        }
        @default {
          @if (!store.trips().length) {
            <cmn-empty-state
              icon="History"
              message="No trips yet"
              subMessage="Tick what you bought on the list and tap Done shopping. The trip is kept here."
            />
          }
          @for (trip of store.trips(); track trip.id) {
            <cmn-list-section
              [heading]="
                (trip.closedAt | date: 'EEE d MMM y') + ' · ' + count(trip) + receipt(trip)
              "
              card
            >
              @for (item of trip.items; track item.id) {
                <cmn-list-item-row
                  [label]="item.text"
                  [sublabel]="item.checkedBy ? 'Ticked by ' + item.checkedBy.name : null"
                  class="[&_.truncate]:overflow-visible [&_.truncate]:whitespace-normal [&_.truncate]:break-words"
                >
                  @if (canPromote(item)) {
                    <cmn-button
                      [aria-label]="'Add ' + item.text + ' to the shelf'"
                      (clicked)="promote(item)"
                      actions
                      variant="ghost"
                      icon="PackagePlus"
                      >Add to shelf</cmn-button
                    >
                  }
                </cmn-list-item-row>
              }
              <div class="p-cmn-3">
                <cmn-button
                  [loading]="store.reloading() === trip.id"
                  [disabled]="!!store.reloading()"
                  (clicked)="reload(trip)"
                  class="block [&_button]:w-full"
                  variant="secondary"
                  icon="RotateCcw"
                  >Same as last time</cmn-button
                >
              </div>
            </cmn-list-section>
          }
          @if (store.hasMore()) {
            <cmn-button
              [loading]="store.loadingMore()"
              (clicked)="store.loadMore()"
              class="block [&_button]:w-full"
              variant="secondary"
              >Show older trips</cmn-button
            >
          }
        }
      }
    </cmn-page>
  `,
})
export class HistoryPage implements OnInit {
  private readonly router = inject(Router);

  protected readonly store = inject(HistoryStore);

  public ngOnInit(): void {
    void this.store.load();
  }

  /** A typed one-off: a line with no product behind it. */
  protected canPromote(item: TripItem): boolean {
    return item.productId === null;
  }

  /** Starts the add-to-shelf flow: the Prices tab's add-a-product box with the typed text in it, ready to confirm. */
  protected promote(item: TripItem): void {
    void this.router.navigate(['/prices'], {queryParams: {add: item.text}});
  }

  protected count(trip: Trip): string {
    return trip.items.length === 1 ? '1 item' : `${trip.items.length} items`;
  }

  /** " · Lidl Plus €12.34" for a trip made from a receipt, nothing for one closed on the list. */
  protected receipt(trip: Trip): string {
    return trip.source === 'lidl_plus'
      ? ` · Lidl Plus${trip.total === null ? '' : ' ' + euro(trip.total)}`
      : '';
  }

  protected async reload(trip: Trip): Promise<void> {
    if (await this.store.reload(trip)) {
      await this.router.navigateByUrl('/list');
    }
  }
}
