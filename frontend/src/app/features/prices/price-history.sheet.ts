import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  type OnInit,
  signal,
} from '@angular/core';
import {
  CMN_DRAWER_DATA,
  ListItemRowComponent,
  ListSectionComponent,
  SkeletonComponent,
  TagComponent,
} from '@lifekit-hq/ui';

import type {PriceHistory} from './price-history.model';
import {changes, chart, LOWEST_TAG, summary} from './price-history-format';
import {PricesApi} from './prices.api';
import type {StoreId} from './prices.model';

export interface PriceHistorySheetData {
  readonly productId: string;
  readonly productName: string;
  readonly store: StoreId;
  readonly storeName: string;
}

/**
 * The last 90 days of one product in one shop: a step line of what a pack cost (the offer and the card price counted, as the
 * ranking counts them), one sentence on where today's price sits, and the days it moved. Read only, and it says plainly when
 * there is nothing to show rather than drawing a line through one point.
 */
@Component({
  selector: 'bk-price-history-sheet',
  imports: [ListItemRowComponent, ListSectionComponent, SkeletonComponent, TagComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-col gap-cmn-3 px-cmn-6 pt-cmn-4 pb-cmn-6" data-testid="price-history">
      @if (failed()) {
        <p class="text-cmn-sm text-text-secondary" data-testid="history-error">
          The price history did not load. Close this and try again.
        </p>
      } @else if (history(); as h) {
        <p class="m-0 text-cmn-sm text-text-primary" data-testid="history-summary">
          {{ summary() }}
          @if (h.lowestIn90Days) {
            <cmn-tag variant="success">{{ lowestTag }}</cmn-tag>
          }
        </p>
        @if (geometry(); as g) {
          <svg
            [attr.viewBox]="'0 0 ' + g.width + ' ' + g.height"
            [attr.aria-label]="
              'Price of ' + data.productName + ' at ' + data.storeName + ' over 90 days'
            "
            class="h-24 w-full text-text-secondary"
            role="img"
          >
            <path [attr.d]="g.path" fill="none" stroke="currentColor" stroke-width="2" />
          </svg>
          <p class="m-0 flex justify-between text-cmn-xs text-text-secondary">
            <span>90 days ago</span><span>Today</span>
          </p>
        }
        @if (rows().length) {
          <cmn-list-section heading="Price changes" card>
            @for (row of rows(); track $index) {
              <cmn-list-item-row [label]="row.day" [sublabel]="row.note">
                <span amount class="text-cmn-sm font-semibold text-text-primary">{{
                  row.price
                }}</span>
              </cmn-list-item-row>
            }
          </cmn-list-section>
        }
      } @else {
        <cmn-skeleton shape="section" />
      }
    </div>
  `,
})
export class PriceHistorySheetComponent implements OnInit {
  private readonly api = inject(PricesApi);
  protected readonly data = inject<PriceHistorySheetData>(CMN_DRAWER_DATA);

  protected readonly lowestTag = LOWEST_TAG;
  protected readonly history = signal<PriceHistory | null>(null);
  protected readonly failed = signal(false);
  protected readonly summary = computed(() => {
    const h = this.history();
    return h ? summary(h) : '';
  });
  protected readonly geometry = computed(() => {
    const h = this.history();
    return h ? chart(h) : null;
  });
  protected readonly rows = computed(() => {
    const h = this.history();
    return h ? changes(h) : [];
  });

  public ngOnInit(): void {
    this.api
      .history(this.data.productId, this.data.store)
      .then(history => this.history.set(history))
      .catch(() => this.failed.set(true));
  }
}
