import {NgOptimizedImage} from '@angular/common';
import {ChangeDetectionStrategy, Component, inject, signal} from '@angular/core';
import {FormControl, FormGroup, ReactiveFormsModule} from '@angular/forms';
import {
  AlertComponent,
  ButtonComponent,
  CMN_DRAWER_DATA,
  CmnDrawerRef,
  InputComponent,
  SelectableCardComponent,
  SkeletonComponent,
} from '@lifekit-hq/ui';

import {PricesApi} from './prices.api';
import type {SearchResponse, SearchResult} from './prices.model';
import {PricesStore} from './prices.store';

/** The server refuses a search shorter than this. */
const MIN_QUERY = 2;

export interface ProductSearchData {
  /** What was already typed in the add box; it becomes the first search. */
  readonly query: string;
}

/**
 * Bottom sheet: search a shop by name and tap the right product, with its photo. It is the add-item search of Bring! and the
 * Tesco app, where the picture settles which product you mean. Only the shops the server can search show results; the others
 * are named with a way in (paste a link), so nothing is silently missing.
 */
@Component({
  selector: 'bk-product-search-sheet',
  imports: [
    NgOptimizedImage,
    ReactiveFormsModule,
    AlertComponent,
    ButtonComponent,
    InputComponent,
    SelectableCardComponent,
    SkeletonComponent,
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
      @if (failed()) {
        <cmn-alert title="The search did not work. Try again." variant="error" />
      }
      @for (note of notes(); track note) {
        <p class="text-cmn-xs text-text-secondary">{{ note }}</p>
      }
      @if (searching()) {
        <cmn-skeleton shape="section" />
      } @else if (response(); as found) {
        @if (found.results.length) {
          <div class="grid grid-cols-2 gap-cmn-3" aria-label="Results" role="group">
            @for (result of found.results; track result.url) {
              <cmn-selectable-card
                [disabled]="store.adding()"
                (clicked)="pick(result)"
                class="block h-full"
                orientation="vertical"
              >
                <div leading>
                  @if (result.imageUrl && !broken().has(result.url)) {
                    <img
                      [ngSrc]="photo(result.imageUrl)"
                      (error)="markBroken(result.url)"
                      class="size-28 rounded-cmn-md bg-surface-raised object-contain"
                      alt=""
                      width="112"
                      height="112"
                      referrerpolicy="no-referrer"
                    />
                  } @else {
                    <div
                      class="flex size-28 items-center justify-center rounded-cmn-md bg-surface-raised text-cmn-3xl font-semibold text-text-secondary"
                      aria-hidden="true"
                    >
                      {{ result.title.charAt(0).toUpperCase() }}
                    </div>
                  }
                </div>
                <span class="line-clamp-3 break-words text-cmn-sm font-medium text-text-primary">{{
                  result.title
                }}</span>
              </cmn-selectable-card>
            }
          </div>
        }
      }
    </div>
  `,
})
export class ProductSearchSheetComponent {
  private readonly api = inject(PricesApi);
  private readonly data = inject<ProductSearchData>(CMN_DRAWER_DATA);
  protected readonly ref = inject<CmnDrawerRef<boolean>>(CmnDrawerRef);
  protected readonly store = inject(PricesStore);

  protected readonly query = new FormControl(this.data.query, {nonNullable: true});
  protected readonly form = new FormGroup({query: this.query});
  protected readonly searching = signal(false);
  protected readonly failed = signal(false);
  protected readonly response = signal<SearchResponse | null>(null);
  protected readonly broken = signal<ReadonlySet<string>>(new Set());

  constructor() {
    if (this.data.query.trim().length >= MIN_QUERY) {
      void this.search();
    }
  }

  /** What the shops that were not searched, or could not be, have to say. */
  protected notes(): string[] {
    return (this.response()?.stores ?? [])
      .filter(shop => !shop.searched && shop.note)
      .map(shop => shop.note as string);
  }

  protected async search(): Promise<void> {
    const value = this.query.value.trim();
    if (value.length < MIN_QUERY || this.searching()) {
      return;
    }
    this.searching.set(true);
    this.failed.set(false);
    try {
      this.response.set(await this.api.search(value));
    } catch {
      this.failed.set(true);
    } finally {
      this.searching.set(false);
    }
  }

  /**
   * The shops' photos are cross-origin. The service worker would fetch them itself, under
   * its own connect-src 'self', and answer 504; ngsw-bypass lets the browser load them directly.
   */
  protected photo(src: string): string {
    return `${src}${src.includes('?') ? '&' : '?'}ngsw-bypass=true`;
  }

  protected markBroken(url: string): void {
    this.broken.update(set => new Set(set).add(url));
  }

  protected async pick(result: SearchResult): Promise<void> {
    if (await this.store.addFromSearch(result)) {
      this.ref.close(true);
    }
  }
}
