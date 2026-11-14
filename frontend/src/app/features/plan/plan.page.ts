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

import {euro, shortDate, STORE_NAMES} from '../prices/price-format';
import {lineDetail, walkSections} from './by-shop-format';
import type {PlanBlocker, PlanItem} from './plan.model';
import {PlanStore} from './plan.store';
import {
  blockerTag,
  type FlagText,
  flagText,
  planRows,
  priceText,
  rowLines,
  rowTag,
  tagVariant,
  verdict,
  wasText,
} from './plan-format';

/**
 * Where to buy what is on the list. Each open line gets the shop with the lowest price per kilo, litre or piece (the
 * promotions rule), and the top says whether one shop can price the whole list. The price per shop is the per-store
 * comparison row of price-comparison grocery apps: shop, price, the day it was read, and beside it what the household
 * last paid there and when (the Shelf's "Last paid" wording). A line that cannot be priced says why and is never guessed.
 */
@Component({
  selector: 'bk-plan-page',
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
            message="The plan did not load"
            subMessage="Check your connection."
            actionLabel="Try again"
          />
        }
        @default {
          @if (!store.items().length) {
            <cmn-empty-state
              icon="ListChecks"
              message="Nothing to plan"
              subMessage="Add things to the list, ideally from the Shelf, and the plan shows the best shop for each."
            />
          } @else {
            @if (summary(); as answer) {
              <cmn-verdict-header
                [status]="answer.status"
                [pills]="answer.pills"
                [caveat]="answer.caveat"
                data-testid="coverage"
              />
            }

            @if (blockers().length) {
              <cmn-list-section [heading]="blockersHeading()" card>
                @for (blocker of blockers(); track blocker.itemId) {
                  <cmn-list-item-row [label]="blocker.text">
                    <cmn-tag variant="warning" meta>{{ blockerReason(blocker) }}</cmn-tag>
                  </cmn-list-item-row>
                }
              </cmn-list-section>
            }

            @for (group of splitGroups(); track group.store) {
              @for (walk of sections(group.lines); track walk.section; let first = $first) {
                <cmn-list-section [heading]="group.storeName + ' · ' + walk.section" card>
                  @if (first) {
                    <span
                      cmnListSectionAside
                      class="text-cmn-sm text-text-secondary"
                      data-testid="shop-total"
                      >Shop total {{ money(group.total) }}</span
                    >
                  }
                  @for (bought of walk.lines; track bought.itemId) {
                    <cmn-list-item-row [label]="bought.text" [sublabel]="detail(bought)">
                      <span amount class="font-semibold text-text-primary text-cmn-sm">{{
                        money(bought.price)
                      }}</span>
                    </cmn-list-item-row>
                  }
                </cmn-list-section>
              }
            }

            @for (item of store.items(); track item.id) {
              <cmn-list-section [heading]="item.text" card>
                @if (flag(item); as why) {
                  <div class="flex flex-wrap items-center gap-cmn-2 px-cmn-4 py-cmn-3">
                    <cmn-tag variant="warning">{{ why.tag }}</cmn-tag>
                    <span class="text-cmn-sm text-text-secondary">{{ why.detail }}</span>
                  </div>
                }
                @for (row of rows(item); track row.entry.store) {
                  <cmn-list-item-row
                    [label]="row.entry.storeName"
                    [sublabel]="lines(row)"
                    [dimmed]="!row.entry.best && row.entry.state !== 'priced'"
                    class="[&_.truncate]:overflow-visible [&_.truncate]:whitespace-pre-line [&_.truncate]:break-words"
                  >
                    @if (tag(row.entry); as text) {
                      <cmn-tag [variant]="variant(row.entry)" meta>{{ text }}</cmn-tag>
                    }
                    <span amount class="flex flex-col items-end text-cmn-sm">
                      <span class="font-semibold text-text-primary">{{ price(row.entry) }}</span>
                      @if (was(row.entry); as old) {
                        <s class="text-cmn-xs text-text-secondary"
                          ><span class="sr-only">was </span>{{ old }}</s
                        >
                      }
                    </span>
                  </cmn-list-item-row>
                }
              </cmn-list-section>
            }

            <cmn-list-section heading="How the plan picks" card>
              <p
                class="m-0 max-w-prose px-cmn-4 py-cmn-3 text-cmn-sm text-text-secondary"
                data-testid="rule"
              >
                Prices as of {{ asOf() }}. The best shop for a line has the lowest price per kilo,
                litre or piece, a card price included, among prices read in the last
                {{ store.staleAfterDays() }} days that are in stock. Older prices and shops that are
                out of stock are shown but never picked, and a line is never guessed.
                @if (splitGroups().length) {
                  When no one shop prices every line, the two shops that together do for the lowest
                  total are proposed: each line is bought at the shop of the two with the lower
                  price per kilo, litre or piece, a card price or a multi-buy included. A shop's
                  lines come in the order its sections are usually walked.
                }
              </p>
            </cmn-list-section>
          }
        }
      }
    </cmn-page>
  `,
})
export class PlanPage implements OnInit {
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly store = inject(PlanStore);

  protected readonly summary = computed(() => {
    const coverage = this.store.coverage();
    return coverage ? verdict(coverage, this.store.items().length) : null;
  });
  protected readonly blockers = computed(() => this.store.coverage()?.blockers ?? []);
  protected readonly blockersHeading = computed(() => {
    const closest = this.store.coverage()?.closestStore;
    return closest ? `Not priced at ${STORE_NAMES[closest]}` : 'Not priced at any shop';
  });
  protected readonly splitGroups = computed(() => this.store.coverage()?.split?.groups ?? []);
  protected readonly asOf = computed(() => shortDate(this.store.asOf()));

  protected readonly money = euro;
  protected readonly detail = lineDetail;
  protected readonly sections = walkSections;
  protected readonly rows = planRows;
  protected readonly lines = rowLines;
  protected readonly price = priceText;
  protected readonly was = wasText;
  protected readonly tag = rowTag;
  protected readonly variant = tagVariant;

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

  protected flag(item: PlanItem): FlagText | null {
    return item.flag ? flagText(item.flag, this.store.staleAfterDays()) : null;
  }

  protected blockerReason(blocker: PlanBlocker): string {
    const item = this.store.items().find(candidate => candidate.id === blocker.itemId);
    return blockerTag(blocker, item, this.store.staleAfterDays());
  }
}
