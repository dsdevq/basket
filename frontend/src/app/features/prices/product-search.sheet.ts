import {ChangeDetectionStrategy, Component, inject, signal} from '@angular/core';
import {FormControl, FormGroup, ReactiveFormsModule} from '@angular/forms';
import {
  AlertComponent,
  ButtonComponent,
  CMN_DRAWER_DATA,
  CmnDrawerRef,
  InputComponent,
  ListItemRowComponent,
  ListSectionComponent,
  SegmentedComponent,
  type SegmentedOption,
  SkeletonComponent,
  TagComponent,
} from '@lifekit-hq/ui';

import {CatalogApi} from '../catalog/catalog.api';
import type {CatalogItem, CatalogSearchResponse} from '../catalog/catalog.model';
import {catalogDetail, catalogPrice} from '../catalog/catalog-format';
import {ListPhotoComponent} from '../list/list-photo.component';
import {STORE_NAMES} from './price-format';
import type {StoreId} from './prices.model';
import {PricesStore} from './prices.store';

/** The server refuses a search shorter than this. */
const MIN_QUERY = 2;

type ShopFilter = 'all' | StoreId;

const SHOP_OPTIONS: readonly SegmentedOption[] = [
  {value: 'all', label: 'All'},
  ...(Object.entries(STORE_NAMES) as [StoreId, string][]).map(([value, label]) => ({value, label})),
];

export interface ProductSearchData {
  /** What was already typed in the add box; it becomes the first search. */
  readonly query: string;
}

/**
 * Bottom sheet: search the combined store catalog by name and tap the right product, with its photo, shop and price. It is the
 * add-item search of Bring! and the Tesco app, where the picture settles which product you mean. Tapping a row puts the product on
 * the shelf, named and barcoded from the row and linked to that shop's page; the server then looks for the other shops' equivalents.
 * A shop's pictures are its own, so each row says which shop it is.
 */
@Component({
  selector: 'bk-product-search-sheet',
  imports: [
    ReactiveFormsModule,
    AlertComponent,
    ButtonComponent,
    InputComponent,
    ListItemRowComponent,
    ListPhotoComponent,
    ListSectionComponent,
    SegmentedComponent,
    SkeletonComponent,
    TagComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-col gap-cmn-3 px-cmn-6 pt-cmn-4 pb-cmn-6">
      <form [formGroup]="form" (ngSubmit)="search()" class="flex items-center gap-cmn-2">
        <cmn-input
          formControlName="query"
          class="min-w-0 flex-1"
          aria-label="Search the shops"
          placeholder="Search by name, e.g. oat drink"
          enterkeyhint="search"
          autocomplete="off"
        />
        <cmn-button [loading]="searching()" type="submit" variant="primary" icon="Search"
          >Search</cmn-button
        >
      </form>
      <cmn-segmented
        [options]="shops"
        [value]="shop()"
        (valueChange)="selectShop($event)"
        label="Shop"
      />
      @if (failed()) {
        <cmn-alert title="The search did not work. Try again." variant="error" />
      }
      @if (searching()) {
        <cmn-skeleton shape="section" />
      } @else if (response(); as found) {
        @if (found.items.length) {
          <cmn-list-section heading="Tap the one you buy" card>
            @for (item of found.items; track item.id) {
              <cmn-list-item-row
                [label]="item.name"
                [sublabel]="detail(item)"
                [action]="!store.adding()"
                (activated)="pick(item)"
                class="[&_.truncate]:overflow-visible [&_.truncate]:whitespace-pre-line [&_.truncate]:break-words"
              >
                <bk-list-photo [name]="item.name" [src]="photo(item.imageUrl)" avatar />
                <cmn-tag variant="neutral" meta>{{ item.storeName }}</cmn-tag>
                <span amount class="text-cmn-sm font-semibold text-text-primary">{{
                  price(item)
                }}</span>
              </cmn-list-item-row>
            }
          </cmn-list-section>
        } @else {
          <p class="text-cmn-sm text-text-secondary">
            Nothing found for “{{ found.query }}”. Try another word, or type the name in the add box
            and link its page from the Prices tab.
          </p>
        }
      }
    </div>
  `,
})
export class ProductSearchSheetComponent {
  private readonly api = inject(CatalogApi);
  private readonly data = inject<ProductSearchData>(CMN_DRAWER_DATA);
  /** Counts searches; an answer that is no longer the latest is dropped, so a slow one never overwrites a newer one. */
  private searches = 0;
  protected readonly ref = inject<CmnDrawerRef<boolean>>(CmnDrawerRef);
  protected readonly store = inject(PricesStore);

  protected readonly shops: SegmentedOption[] = [...SHOP_OPTIONS];
  protected readonly query = new FormControl(this.data.query, {nonNullable: true});
  protected readonly form = new FormGroup({query: this.query});
  protected readonly shop = signal<ShopFilter>('all');
  protected readonly searching = signal(false);
  protected readonly failed = signal(false);
  protected readonly response = signal<CatalogSearchResponse | null>(null);
  protected readonly detail = catalogDetail;
  protected readonly price = catalogPrice;

  constructor() {
    if (this.data.query.trim().length >= MIN_QUERY) {
      void this.search();
    }
  }

  protected selectShop(value: string): void {
    this.shop.set(value as ShopFilter);
    if (this.response() || this.query.value.trim().length >= MIN_QUERY) {
      void this.search();
    }
  }

  protected async search(): Promise<void> {
    const value = this.query.value.trim();
    if (value.length < MIN_QUERY) {
      return;
    }
    const seen = ++this.searches;
    const shop = this.shop();
    this.searching.set(true);
    this.failed.set(false);
    try {
      const found = await this.api.search(value, shop === 'all' ? undefined : shop);
      if (seen === this.searches) {
        this.response.set(found);
      }
    } catch {
      if (seen === this.searches) {
        this.failed.set(true);
      }
    } finally {
      if (seen === this.searches) {
        this.searching.set(false);
      }
    }
  }

  /**
   * The shops' photos are cross-origin. The service worker would fetch them itself, under
   * its own connect-src 'self', and answer 504; ngsw-bypass lets the browser load them directly.
   */
  protected photo(src: string | null): string | null {
    return src ? `${src}${src.includes('?') ? '&' : '?'}ngsw-bypass=true` : null;
  }

  protected async pick(item: CatalogItem): Promise<void> {
    if (this.store.adding()) {
      return;
    }
    if (await this.store.addFromCatalog(item)) {
      this.ref.close(true);
    }
  }
}
