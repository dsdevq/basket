import {DOCUMENT} from '@angular/common';
import {ChangeDetectionStrategy, Component, DestroyRef, inject, type OnInit} from '@angular/core';
import {takeUntilDestroyed} from '@angular/core/rxjs-interop';
import {FormControl, FormGroup, ReactiveFormsModule} from '@angular/forms';
import {
  AlertComponent,
  ButtonComponent,
  CmnDrawerService,
  EmptyStateComponent,
  InputComponent,
  ListItemRowComponent,
  ListSectionComponent,
  PageComponent,
  SkeletonComponent,
} from '@lifekit-hq/ui';
import {firstValueFrom, fromEvent} from 'rxjs';

import {ComposerBarComponent} from '../../core/shell/composer-bar.component';
import {detailLines, headline, storeProblem} from './price-format';
import type {Product, ProductStore} from './prices.model';
import {PricesStore} from './prices.store';
import {type ProductAction, ProductActionsSheetComponent} from './product-actions.sheet';
import {StoreLinkSheetComponent} from './store-link.sheet';

/** Prices change once a night, so the page re-reads only when it comes back into view. */
@Component({
  selector: 'bk-prices-page',
  imports: [
    ReactiveFormsModule,
    ComposerBarComponent,
    AlertComponent,
    ButtonComponent,
    EmptyStateComponent,
    InputComponent,
    ListItemRowComponent,
    ListSectionComponent,
    PageComponent,
    SkeletonComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-page [reload]="reload" kind="list" fill>
      @for (problem of problems(); track problem) {
        <cmn-alert [title]="problem" variant="warning" />
      }

      @switch (store.status()) {
        @case ('loading') {
          <cmn-skeleton shape="section" />
        }
        @case ('error') {
          <cmn-empty-state
            (action)="store.load()"
            kind="error"
            message="The prices did not load"
            subMessage="Check your connection."
            actionLabel="Try again"
          />
        }
        @default {
          @if (!store.products().length) {
            <cmn-empty-state
              icon="Tag"
              message="No products yet"
              subMessage="Add what you buy, then link its page in each shop. basket reads the prices every night."
            />
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
                  (activated)="link(product, entry)"
                  class="[&_.truncate]:overflow-visible [&_.truncate]:whitespace-pre-line [&_.truncate]:break-words"
                >
                  <span amount class="text-cmn-sm font-semibold text-text-primary">{{
                    headline(entry)
                  }}</span>
                </cmn-list-item-row>
              }
            </cmn-list-section>
          }
        }
      }
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

  protected readonly store = inject(PricesStore);

  protected readonly name = new FormControl('', {nonNullable: true});
  protected readonly form = new FormGroup({name: this.name});
  protected readonly headline = headline;

  /** Pull to refresh on this tab root: the shell's pull gesture calls it and waits for the promise. */
  protected readonly reload = (): Promise<void> => this.store.load();

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
      .filter(Boolean);
  }

  /** One line per fact group; the row keeps the line breaks. */
  protected describe(entry: ProductStore): string {
    return detailLines(entry).join('\n');
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
