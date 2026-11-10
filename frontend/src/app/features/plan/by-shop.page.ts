import {DOCUMENT} from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  type OnInit,
} from '@angular/core';
import {takeUntilDestroyed} from '@angular/core/rxjs-interop';
import {
  EmptyStateComponent,
  ListItemRowComponent,
  ListSectionComponent,
  PageComponent,
  SkeletonComponent,
  TagComponent,
  VerdictHeaderComponent,
} from '@lifekit-hq/ui';
import {fromEvent} from 'rxjs';

import {euro, shortDate} from '../prices/price-format';
import {ByShopStore} from './by-shop.store';
import {lineDetail, orderedShops, storeDetail, verdict} from './by-shop-format';
import {flagText, missingTag} from './plan-format';

/**
 * The list priced at each shop side by side: a total per shop, the lines each lacks and the cheapest split. It is the
 * per-store basket total of price-comparison grocery apps (Trolley, Basket Savings), set on the same prices as the Plan
 * tab. A shop's total adds only what it prices today; a line it cannot price is listed with the reason and never guessed.
 */
@Component({
  selector: 'bk-by-shop-page',
  imports: [
    EmptyStateComponent,
    ListItemRowComponent,
    ListSectionComponent,
    PageComponent,
    SkeletonComponent,
    TagComponent,
    VerdictHeaderComponent,
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
            message="The shop totals did not load"
            subMessage="Check your connection."
            actionLabel="Try again"
          />
        }
        @default {
          @if (store.basket(); as basket) {
            @if (!basket.lineCount) {
              <cmn-empty-state
                icon="Scale"
                message="Nothing to total"
                subMessage="Add things to the list, ideally from the Shelf, and each shop's total shows here."
              />
            } @else {
              @if (answer(); as top) {
                <cmn-verdict-header
                  [status]="top.status"
                  [pills]="top.pills"
                  [caveat]="top.caveat"
                  data-testid="verdict"
                />
              }

              <cmn-list-section heading="The list at each shop" card>
                @for (shop of shops(); track shop.store) {
                  <cmn-list-item-row
                    [label]="shop.storeName"
                    [sublabel]="detail(shop, basket.lineCount)"
                    [dimmed]="!shop.priced"
                  >
                    @if (shop.cheapest) {
                      <cmn-tag variant="success" meta>Cheapest</cmn-tag>
                    } @else if (!shop.priced) {
                      <cmn-tag variant="warning" meta>Nothing priced</cmn-tag>
                    } @else if (!shop.complete) {
                      <cmn-tag variant="warning" meta>{{ shop.missing.length }} missing</cmn-tag>
                    }
                    <span amount class="font-semibold text-text-primary text-cmn-sm">{{
                      shop.priced ? price(shop.total) : '—'
                    }}</span>
                  </cmn-list-item-row>
                }
              </cmn-list-section>

              @for (shop of shops(); track shop.store) {
                @if (shop.priced && shop.missing.length) {
                  <cmn-list-section [heading]="'Not priced at ' + shop.storeName" card>
                    @for (line of shop.missing; track line.itemId) {
                      <cmn-list-item-row [label]="line.text">
                        <cmn-tag variant="warning" meta>{{ missing(line.reason) }}</cmn-tag>
                      </cmn-list-item-row>
                    }
                  </cmn-list-section>
                }
              }

              @for (group of basket.split.groups; track group.store) {
                <cmn-list-section
                  [heading]="'Split: buy at ' + group.storeName + ' · ' + price(group.total)"
                  card
                >
                  @for (line of group.lines; track line.itemId) {
                    <cmn-list-item-row [label]="line.text" [sublabel]="lineText(line)">
                      <span amount class="font-semibold text-text-primary text-cmn-sm">{{
                        price(line.price)
                      }}</span>
                    </cmn-list-item-row>
                  }
                </cmn-list-section>
              }

              @if (basket.split.unplaced.length) {
                <cmn-list-section heading="Split: no best shop" card>
                  @for (line of basket.split.unplaced; track line.itemId) {
                    <cmn-list-item-row [label]="line.text" [sublabel]="unplacedWhy(line.reason)">
                      <cmn-tag variant="warning" meta>{{ unplacedTag(line.reason) }}</cmn-tag>
                    </cmn-list-item-row>
                  }
                </cmn-list-section>
              }

              <cmn-list-section heading="How the totals add up" card>
                <p
                  class="m-0 max-w-prose px-cmn-4 py-cmn-3 text-cmn-sm text-text-secondary"
                  data-testid="rule"
                >
                  Prices as of {{ asOf() }}. A shop's total adds one pack of each line at what the
                  shop charges now, a card price or a multi-buy deal included, using prices read in
                  the last {{ basket.staleAfterDays }} days that are in stock. A line a shop cannot
                  price adds nothing and is listed, so only a shop with none missing can be
                  cheapest. The split buys each line at the shop with the lowest price per kilo,
                  litre or piece, so it can cost more than a shop whose packs are smaller.
                </p>
              </cmn-list-section>
            }
          }
        }
      }
    </cmn-page>
  `,
})
export class ByShopPage implements OnInit {
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly store = inject(ByShopStore);

  protected readonly answer = computed(() => {
    const basket = this.store.basket();
    return basket?.lineCount ? verdict(basket) : null;
  });
  protected readonly shops = computed(() => {
    const basket = this.store.basket();
    return basket ? orderedShops(basket) : [];
  });
  protected readonly asOf = computed(() => shortDate(this.store.basket()?.asOf ?? null));

  protected readonly price = euro;
  protected readonly detail = storeDetail;
  protected readonly lineText = lineDetail;
  protected readonly missing = missingTag;

  /** Prices change at most once a night, so the page re-reads when it opens and when it comes back into view. */
  public ngOnInit(): void {
    void this.store.load();
    fromEvent(this.document, 'visibilitychange')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        if (this.document.visibilityState === 'visible') {
          void this.store.load(true);
        }
      });
  }

  protected unplacedTag(reason: Parameters<typeof flagText>[0]): string {
    return flagText(reason, this.store.basket()?.staleAfterDays ?? 0).tag;
  }

  protected unplacedWhy(reason: Parameters<typeof flagText>[0]): string {
    return flagText(reason, this.store.basket()?.staleAfterDays ?? 0).detail;
  }
}
