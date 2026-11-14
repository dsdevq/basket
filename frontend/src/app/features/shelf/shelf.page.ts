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
  EmptyStateComponent,
  PageComponent,
  SegmentedComponent,
  type SegmentedOption,
  SkeletonComponent,
} from '@lifekit-hq/ui';
import {fromEvent} from 'rxjs';

import {HabitsStore} from '../habits/habits.store';
import {ListStore} from '../list/list.store';
import {STORE_NAMES} from '../prices/price-format';
import {PricesStore} from '../prices/prices.store';
import {ShelfCardComponent} from './shelf-card.component';
import {
  pickStore,
  SHELF_OPTIONS,
  SHELF_VIEWS,
  type ShelfFilter,
  type ShelfTile,
  shelfTiles,
  type ShelfView,
} from './shelf-format';

const key = (productId: string, store: string | null): string => `${productId}|${store ?? ''}`;

/**
 * Pick a shop, tap the products you need, and they are on the shared list: no typing. The product grid is the pattern of
 * Bring! (a tile per product, tap to add) and the shop filter and the "on your list" state are the favourites list of the
 * Tesco app. "Most bought" and "Usually bought" are the frequent-items view of AnyList and OurGroceries, here as a sort and a
 * filter over the same grid, worked out from closed trips.
 */
@Component({
  selector: 'bk-shelf-page',
  imports: [
    EmptyStateComponent,
    PageComponent,
    SegmentedComponent,
    ShelfCardComponent,
    SkeletonComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-page kind="overview">
      <div class="mb-cmn-4 md:max-w-md">
        <cmn-segmented
          [options]="options"
          [value]="filter()"
          (valueChange)="select($event)"
          label="Shop"
        />
      </div>
      <div class="mb-cmn-4 md:max-w-md">
        <cmn-segmented
          [options]="viewOptions"
          [value]="view()"
          (valueChange)="selectView($event)"
          label="Show"
        />
      </div>
      @switch (prices.status()) {
        @case ('loading') {
          <cmn-skeleton shape="section" />
        }
        @case ('error') {
          <cmn-empty-state
            (action)="reload()"
            kind="error"
            message="The shelf did not load"
            subMessage="Check your connection."
            actionLabel="Try again"
          />
        }
        @default {
          @if (!tiles().length) {
            <cmn-empty-state
              [message]="empty().message"
              [subMessage]="empty().detail"
              icon="Store"
            />
          } @else {
            <div
              class="grid grid-cols-2 gap-cmn-3 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5"
              aria-label="Products"
              role="group"
            >
              @for (tile of tiles(); track tile.product.id) {
                <bk-shelf-card
                  [tile]="tile"
                  [filter]="filter()"
                  [onList]="onList().has(rowKey(tile))"
                  [busy]="pending().has(rowKey(tile))"
                  (picked)="pick(tile)"
                />
              }
            </div>
          }
        }
      }
    </cmn-page>
  `,
})
export class ShelfPage implements OnInit {
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  private readonly list = inject(ListStore);

  protected readonly prices = inject(PricesStore);
  protected readonly habits = inject(HabitsStore);
  protected readonly options: SegmentedOption[] = [...SHELF_OPTIONS];
  protected readonly viewOptions: SegmentedOption[] = [...SHELF_VIEWS];
  protected readonly filter = signal<ShelfFilter>('all');
  protected readonly view = signal<ShelfView>('all');
  protected readonly pending = signal<ReadonlySet<string>>(new Set());

  protected readonly tiles = computed(() =>
    shelfTiles(this.prices.products(), this.filter(), this.view(), this.habits.byProduct())
  );
  /** The products already open on the list, per shop, so a tile shows it and a second tap does nothing. */
  protected readonly onList = computed(
    () =>
      new Set(
        this.list.toBuy().flatMap(item => (item.productId ? [key(item.productId, item.store)] : []))
      )
  );
  protected readonly empty = computed(() => {
    const filter = this.filter();
    if (this.view() === 'usual') {
      return {
        message: 'Nothing usual yet',
        detail:
          'Products you buy on two or more trips show up here. Tap Done shopping on the list to count a trip.',
      };
    }
    if (filter === 'all') {
      return {
        message: 'The shelf is empty',
        detail: 'Products appear here once they are added on the Prices tab.',
      };
    }
    const name = STORE_NAMES[filter];
    return {
      message: `Nothing at ${name} yet`,
      detail: `No product has a ${name} page linked. Link one on the Prices tab.`,
    };
  });

  public ngOnInit(): void {
    this.reload();
    fromEvent(this.document, 'visibilitychange')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        if (this.document.visibilityState === 'visible') {
          this.reload(true);
        }
      });
  }

  protected select(value: string): void {
    this.filter.set(SHELF_OPTIONS.find(option => option.value === value)?.value ?? 'all');
  }

  protected selectView(value: string): void {
    this.view.set(SHELF_VIEWS.find(option => option.value === value)?.value ?? 'all');
  }

  protected reload(quiet = false): void {
    void this.habits.load();
    void this.prices.load(quiet);
    void this.list.load(quiet);
  }

  protected rowKey(tile: ShelfTile): string {
    return key(tile.product.id, pickStore(this.filter()));
  }

  protected async pick(tile: ShelfTile): Promise<void> {
    const row = this.rowKey(tile);
    if (this.onList().has(row) || this.pending().has(row)) {
      return;
    }
    this.pending.update(set => new Set(set).add(row));
    try {
      await this.list.addProduct(tile.product.id, pickStore(this.filter()));
    } finally {
      this.pending.update(set => {
        const next = new Set(set);
        next.delete(row);
        return next;
      });
    }
  }
}
