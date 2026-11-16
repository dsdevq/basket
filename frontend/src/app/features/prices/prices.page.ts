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
import {FormControl, FormGroup, ReactiveFormsModule} from '@angular/forms';
import {ActivatedRoute} from '@angular/router';
import {
  AlertComponent,
  AsyncStateComponent,
  type AsyncStateStatus,
  ButtonComponent,
  CmnDrawerService,
  EmptyStateComponent,
  InputComponent,
  ListItemRowComponent,
  ListSectionComponent,
  PageComponent,
  SkeletonComponent,
  TagComponent,
} from '@lifekit-hq/ui';
import {firstValueFrom, fromEvent} from 'rxjs';

import {ComposerBarComponent} from '../../core/shell/composer-bar.component';
import {healthLines, healthTag, healthWarnings} from './catalog-health-format';
import {MatchSheetComponent, type MatchSheetData, type MatchSheetResult} from './match.sheet';
import {detailLines, headline, storeProblem, suggestionLine} from './price-format';
import type {Product, ProductStore, StoreStatus} from './prices.model';
import {PricesStore} from './prices.store';
import {type ProductAction, ProductActionsSheetComponent} from './product-actions.sheet';
import {ProductSearchSheetComponent} from './product-search.sheet';
import {StoreLinkSheetComponent} from './store-link.sheet';

/** Prices change once a night, so the page re-reads only when it comes back into view. */
@Component({
  selector: 'bk-prices-page',
  imports: [
    ReactiveFormsModule,
    ComposerBarComponent,
    AlertComponent,
    AsyncStateComponent,
    ButtonComponent,
    EmptyStateComponent,
    InputComponent,
    ListItemRowComponent,
    ListSectionComponent,
    PageComponent,
    SkeletonComponent,
    TagComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-page [reload]="reload" kind="list" fill>
      @for (problem of problems(); track problem) {
        <cmn-alert [title]="problem" variant="warning" />
      }

      <cmn-async-state
        [emptySubMessage]="emptyBody"
        [status]="asyncStatus()"
        (retry)="store.load()"
        errorMessage="The prices did not load. Check your connection."
        retryable
      >
        <cmn-skeleton shape="section" skeleton />
        @if (!store.products().length) {
          <cmn-empty-state [subMessage]="emptyBody" icon="Tag" message="No products yet" />
        }
        @for (product of store.products(); track product.id) {
          <cmn-list-section [heading]="product.name" card>
            <cmn-button
              [aria-label]="'More for ' + product.name"
              (clicked)="more(product)"
              cmnListSectionAside
              variant="ghost"
              icon="Ellipsis"
            />
            @for (entry of product.stores; track entry.store) {
              <cmn-list-item-row
                [label]="entry.name"
                [sublabel]="describe(entry)"
                [dimmed]="!entry.listing"
                [action]="true"
                (activated)="open(product, entry)"
                class="[&_.truncate]:overflow-visible [&_.truncate]:whitespace-pre-line [&_.truncate]:break-words"
              >
                <span amount class="text-cmn-sm font-semibold text-text-primary">{{
                  headline(entry)
                }}</span>
              </cmn-list-item-row>
            }
          </cmn-list-section>
        }
        @if (store.stores().length) {
          <cmn-list-section heading="Shop catalogs" card data-testid="catalog-health">
            @for (status of store.stores(); track status.store) {
              <cmn-list-item-row
                [label]="status.name"
                [sublabel]="healthText(status)"
                class="[&_.truncate]:overflow-visible [&_.truncate]:whitespace-pre-line [&_.truncate]:break-words"
              >
                <cmn-tag [variant]="healthTag(status).variant" meta>{{
                  healthTag(status).text
                }}</cmn-tag>
              </cmn-list-item-row>
            }
          </cmn-list-section>
        }
      </cmn-async-state>
      <bk-composer-bar>
        <form [formGroup]="form" (ngSubmit)="add()" class="flex items-center gap-cmn-2">
          <cmn-input
            formControlName="name"
            class="min-w-0 flex-1"
            aria-label="Add a product"
            placeholder="Add a product, e.g. Whole milk 2L"
            enterkeyhint="done"
            autocomplete="off"
          />
          <cmn-button
            (clicked)="search()"
            aria-label="Search the shops"
            variant="secondary"
            icon="Search"
          />
          <cmn-button [loading]="store.adding()" type="submit" variant="primary" icon="Plus"
            >Add</cmn-button
          >
        </form>
      </bk-composer-bar>
    </cmn-page>
  `,
})
export class PricesPage implements OnInit {
  private readonly drawer = inject(CmnDrawerService);
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  private readonly route = inject(ActivatedRoute);

  protected readonly store = inject(PricesStore);

  protected readonly emptyBody =
    'Search a shop by name and tap the right one, or type a name and link its page in each shop. basket reads the prices every night.';

  /** The store's load state in the words `cmn-async-state` takes. */
  protected readonly asyncStatus = computed<AsyncStateStatus>(() => {
    const status = this.store.status();
    return status === 'ready' ? 'success' : status;
  });

  protected readonly name = new FormControl('', {nonNullable: true});
  protected readonly form = new FormGroup({name: this.name});
  protected readonly headline = headline;

  /** Pull to refresh on this tab root: the shell's pull gesture calls it and waits for the promise. */
  protected readonly reload = (): Promise<void> => this.store.load();

  public ngOnInit(): void {
    // "Add to shelf" on a History item arrives with the typed text; it fills the box, a person still confirms with Add.
    const typed = this.route.snapshot.queryParamMap.get('add')?.trim();
    if (typed) {
      this.name.setValue(typed);
    }
    void this.store.load();
    fromEvent(this.document, 'visibilitychange')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        if (this.document.visibilityState === 'visible') {
          void this.store.load(true);
        }
      });
  }

  /** One warning per shop whose last nightly pull did not fully work (blocked, down, changed). */
  protected problems(): string[] {
    return this.store
      .stores()
      .map(status => {
        const problem = storeProblem(status);
        return problem && status.lastRun?.message
          ? `${problem}. ${status.lastRun.message}`
          : problem;
      })
      .concat(this.store.stores().flatMap(status => healthWarnings(status, new Date())))
      .filter(Boolean);
  }

  /** The tag on a shop's row in the catalog card. */
  protected healthTag(status: StoreStatus): ReturnType<typeof healthTag> {
    return healthTag(status.catalog);
  }

  /** When the shop's catalog was last swept and how it stands, one line per fact group. */
  protected healthText(status: StoreStatus): string {
    return healthLines(status.catalog, new Date()).join('\n');
  }

  /** One line per fact group; the row keeps the line breaks. */
  protected describe(entry: ProductStore): string {
    return suggestionLine(entry).concat(detailLines(entry)).join('\n');
  }

  protected async add(): Promise<void> {
    const value = this.name.value.trim();
    if (!value) {
      return;
    }
    if (await this.store.add(value)) {
      this.name.reset('');
    }
  }

  /** A shop with suggested pages opens the match sheet; a shop without opens the paste-an-address sheet. */
  protected open(product: Product, entry: ProductStore): void {
    if (entry.candidates) {
      void this.match(product, entry);
    } else {
      this.link(product, entry);
    }
  }

  private async match(product: Product, entry: ProductStore): Promise<void> {
    const ref = this.drawer.open<MatchSheetResult, MatchSheetData>(MatchSheetComponent, {
      title: `${product.name} at ${entry.name}`,
      data: {product, entry},
      mode: 'sheet',
    });
    if ((await firstValueFrom(ref.afterClosed(), {defaultValue: undefined})) === 'paste') {
      this.link(product, entry);
    }
  }

  /** Search a shop by name, with photos; what is typed so far is the first search. A tapped result is added. */
  protected search(): void {
    const ref = this.drawer.open<boolean, {query: string}>(ProductSearchSheetComponent, {
      title: 'Search the shops',
      data: {query: this.name.value.trim()},
      mode: 'sheet',
    });
    void firstValueFrom(ref.afterClosed(), {defaultValue: undefined}).then(added => {
      if (added) {
        this.name.reset('');
      }
    });
  }

  protected link(product: Product, entry: ProductStore): void {
    this.drawer.open<boolean, {product: Product; entry: ProductStore}>(StoreLinkSheetComponent, {
      title: `${product.name} at ${entry.name}`,
      data: {product, entry},
      mode: 'sheet',
    });
  }

  protected async more(product: Product): Promise<void> {
    const ref = this.drawer.open<ProductAction, {product: Product}>(ProductActionsSheetComponent, {
      title: product.name,
      data: {product},
      mode: 'sheet',
    });
    if ((await firstValueFrom(ref.afterClosed(), {defaultValue: undefined})) === 'delete') {
      await this.store.remove(product);
    }
  }
}
