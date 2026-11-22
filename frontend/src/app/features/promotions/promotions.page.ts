import {DOCUMENT} from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  type OnInit,
  signal,
} from '@angular/core';
import {takeUntilDestroyed} from '@angular/core/rxjs-interop';
import {
  CmnDrawerService,
  EmptyStateComponent,
  ListItemRowComponent,
  ListSectionComponent,
  PageComponent,
  SegmentedComponent,
  type SegmentedOption,
  SkeletonComponent,
  TagComponent,
} from '@lifekit-hq/ui';
import {fromEvent} from 'rxjs';

import {
  PriceHistorySheetComponent,
  type PriceHistorySheetData,
} from '../prices/price-history.sheet';
import {LOWEST_TAG} from '../prices/price-history-format';
import type {PromoOffer, PromoUnit} from './promotions.model';
import {PromotionsStore} from './promotions.store';
import {byStore, gapText, headline, offerLines, placeText, UNIT_WORDS} from './promotions-format';

const VIEWS: readonly SegmentedOption[] = [
  {value: 'product', label: 'Best buy'},
  {value: 'store', label: 'By store'},
];

/**
 * What is on offer, and which offer is better. Every offer is turned into the price of one kilo, litre or piece (the
 * unit-price shelf label), so a "2 for €4" can be set against "20% off" and a card price. Prices change at most once a
 * night, so the page re-reads only when it comes back into view.
 */
@Component({
  selector: 'bk-promotions-page',
  imports: [
    EmptyStateComponent,
    ListItemRowComponent,
    ListSectionComponent,
    PageComponent,
    SegmentedComponent,
    SkeletonComponent,
    TagComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-page kind="list">
      <cmn-segmented [options]="views" [(value)]="view" label="Group promotions by" />

      <cmn-list-section heading="How offers are ranked" card>
        <p
          class="m-0 max-w-prose px-cmn-4 py-cmn-3 text-cmn-sm text-text-secondary"
          data-testid="rule"
        >
          Each offer becomes the price of one kilo, litre or piece: 2 for €4 is €2 each, the pack
          price is divided by the pack size, and a card price needs the card. Cheapest first.
          Readings over {{ store.staleAfterDays() }} days old, packs with no size and mixed units
          are shown but not ranked.
        </p>
      </cmn-list-section>

      @switch (store.status()) {
        @case ('loading') {
          <cmn-skeleton shape="section" />
        }
        @case ('error') {
          <cmn-empty-state
            (action)="store.load()"
            kind="error"
            message="The promotions did not load"
            subMessage="Check your connection."
            actionLabel="Try again"
          />
        }
        @default {
          @if (!store.products().length) {
            <cmn-empty-state
              icon="Tag"
              message="No promotions right now"
              subMessage="When a shop you track has an offer on one of your products it shows up here, ranked by price per kilo, litre or piece."
            />
          } @else if (view() === 'product') {
            @for (product of store.products(); track product.id) {
              <cmn-list-section
                [heading]="product.name + (product.unit ? ', per ' + unitWord(product.unit) : '')"
                card
              >
                @for (offer of product.offers; track offer.store) {
                  <cmn-list-item-row
                    [label]="offer.storeName"
                    [sublabel]="lines(offer)"
                    [dimmed]="offer.rank === null"
                    [action]="true"
                    (activated)="showHistory(product.id, product.name, offer)"
                    class="[&_.truncate]:overflow-visible [&_.truncate]:whitespace-pre-line [&_.truncate]:break-words"
                  >
                    @if (offer.lowestIn90Days) {
                      <cmn-tag variant="success" meta>{{ lowestTag }}</cmn-tag>
                    }
                    @if (place(offer, product); as text) {
                      <cmn-tag [variant]="offer.rank === 1 ? 'success' : 'neutral'" meta>{{
                        text
                      }}</cmn-tag>
                    }
                    <span amount class="text-cmn-sm font-semibold text-text-primary">{{
                      headline(offer)
                    }}</span>
                  </cmn-list-item-row>
                }
                @for (gap of product.gaps; track gap.store) {
                  <cmn-list-item-row
                    [label]="gap.storeName"
                    [sublabel]="gapText(gap)"
                    [dimmed]="true"
                  />
                }
              </cmn-list-section>
            }
          } @else {
            @for (group of stores(); track group.store) {
              <cmn-list-section [heading]="group.name" card>
                @for (entry of group.entries; track entry.product.id) {
                  <cmn-list-item-row
                    [label]="entry.product.name"
                    [sublabel]="lines(entry.offer)"
                    [dimmed]="entry.offer.rank === null"
                    [action]="true"
                    (activated)="showHistory(entry.product.id, entry.product.name, entry.offer)"
                    class="[&_.truncate]:overflow-visible [&_.truncate]:whitespace-pre-line [&_.truncate]:break-words"
                  >
                    @if (entry.offer.lowestIn90Days) {
                      <cmn-tag variant="success" meta>{{ lowestTag }}</cmn-tag>
                    }
                    @if (place(entry.offer, entry.product); as text) {
                      <cmn-tag [variant]="entry.offer.rank === 1 ? 'success' : 'neutral'" meta>{{
                        text
                      }}</cmn-tag>
                    }
                    <span amount class="text-cmn-sm font-semibold text-text-primary">{{
                      headline(entry.offer)
                    }}</span>
                  </cmn-list-item-row>
                }
              </cmn-list-section>
            }
          }
        }
      }
    </cmn-page>
  `,
})
export class PromotionsPage implements OnInit {
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  private readonly drawer = inject(CmnDrawerService);

  protected readonly store = inject(PromotionsStore);

  protected readonly views = VIEWS;
  protected readonly view = signal('product');
  protected readonly stores = computed(() => byStore(this.store.products()));
  protected readonly headline = headline;
  protected readonly gapText = gapText;
  protected readonly place = placeText;
  protected readonly lowestTag = LOWEST_TAG;

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

  /** One line per fact group; the row keeps the line breaks. */
  protected lines(offer: PromoOffer): string {
    return offerLines(offer).join('\n');
  }

  /** Tapping an offer opens its shop's last 90 days of prices for that product. */
  protected showHistory(productId: string, productName: string, offer: PromoOffer): void {
    this.drawer.open<boolean, PriceHistorySheetData>(PriceHistorySheetComponent, {
      title: `${productName} at ${offer.storeName}`,
      data: {productId, productName, store: offer.store, storeName: offer.storeName},
      mode: 'sheet',
    });
  }

  protected unitWord(unit: PromoUnit): string {
    return UNIT_WORDS[unit];
  }
}
