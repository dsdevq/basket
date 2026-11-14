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
import {ScorecardStore} from './scorecard.store';
import {
  monthDetail,
  monthLabel,
  newestFirst,
  orderedStores,
  storeDetail,
  verdict,
} from './scorecard-format';

/**
 * Which shop wins most of what the household really buys, what the winners saved against the dearest shop, and how that
 * went month by month. It is the savings summary of price-comparison and receipt apps (Trolley's savings, Basket Savings),
 * built on the household's own closed trips and the daily prices. A view opened on demand: nothing is pushed or scheduled.
 */
@Component({
  selector: 'bk-scorecard-page',
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
            message="The scorecard did not load"
            subMessage="Check your connection."
            actionLabel="Try again"
          />
        }
        @default {
          @if (store.card(); as card) {
            @if (!card.trips) {
              <cmn-empty-state
                icon="Trophy"
                message="No trips to score yet"
                subMessage="Pick products from the Shelf, tick them and tap Done shopping. Your closed trips show here."
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

              <cmn-list-section heading="Wins by shop" card>
                @for (shop of shops(); track shop.store) {
                  <cmn-list-item-row
                    [label]="shop.storeName"
                    [sublabel]="storeText(shop)"
                    [dimmed]="!shop.wins"
                  >
                    @if (shop.store === card.topStore) {
                      <cmn-tag variant="success" meta>Most wins</cmn-tag>
                    }
                    <span amount class="font-semibold text-text-primary text-cmn-sm">{{
                      shop.wins
                    }}</span>
                  </cmn-list-item-row>
                }
              </cmn-list-section>

              <cmn-list-section heading="Month by month" card>
                @for (month of months(); track month.month) {
                  <cmn-list-item-row
                    [label]="label(month.month)"
                    [sublabel]="monthText(month)"
                    [dimmed]="!month.compared"
                  >
                    <span amount class="font-semibold text-text-primary text-cmn-sm">{{
                      month.compared ? price(month.saved) : '—'
                    }}</span>
                  </cmn-list-item-row>
                }
              </cmn-list-section>

              <cmn-list-section heading="How the scorecard adds up" card>
                <p
                  class="m-0 max-w-prose px-cmn-4 py-cmn-3 text-cmn-sm text-text-secondary"
                  data-testid="rule"
                >
                  As of {{ asOf() }}, from your closed trips of the last {{ card.months }} months.
                  Each Shelf line is compared across the shops that priced it, on the day of the
                  trip, using prices read in the last {{ card.staleAfterDays }} days that were in
                  stock and comparable by price per kilo, litre or piece. The shop with the lowest
                  wins the line; saved is what its pack would have cost more at the dearest shop. A
                  typed line, or one without two such prices, is left out and counted, never
                  guessed.
                </p>
              </cmn-list-section>
            }
          }
        }
      }
    </cmn-page>
  `,
})
export class ScorecardPage implements OnInit {
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly store = inject(ScorecardStore);

  protected readonly answer = computed(() => {
    const card = this.store.card();
    return card?.trips ? verdict(card) : null;
  });
  protected readonly shops = computed(() => {
    const card = this.store.card();
    return card ? orderedStores(card) : [];
  });
  protected readonly months = computed(() => {
    const card = this.store.card();
    return card ? newestFirst(card) : [];
  });
  protected readonly asOf = computed(() => shortDate(this.store.card()?.asOf ?? null));

  protected readonly price = euro;
  protected readonly label = monthLabel;
  protected readonly storeText = storeDetail;
  protected readonly monthText = monthDetail;

  /** Re-reads when it opens and when it comes back into view, so it never shows a stale answer. */
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
}
