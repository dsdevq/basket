import {ChangeDetectionStrategy, Component, computed, inject, signal} from '@angular/core';
import {takeUntilDestroyed} from '@angular/core/rxjs-interop';
import {FormControl, ReactiveFormsModule} from '@angular/forms';
import {
  ButtonComponent,
  CheckboxComponent,
  CMN_DRAWER_DATA,
  CmnDrawerRef,
  InputComponent,
  ListItemRowComponent,
  ListSectionComponent,
  SkeletonComponent,
} from '@lifekit-hq/ui';

import type {Product} from '../prices/prices.model';
import {PricesStore} from '../prices/prices.store';
import {shelfTiles} from '../shelf/shelf-format';
import {matchedAlias, productMatches, wouldAddName} from './link-format';
import type {ListItem} from './list.model';
import {ListStore} from './list.store';
import {ListPhotoComponent} from './list-photo.component';

export interface ProductLinkData {
  readonly item: ListItem;
}

/**
 * Bottom sheet: link a typed line to a product on the Shelf. Search over the products' names and their other names, with photos,
 * then tap the one meant. When the line's words are not yet a name of that product, the next step asks whether to remember them,
 * pre-ticked: the next time the words are typed they link by themselves. Unticked, only this line is linked.
 */
@Component({
  selector: 'bk-product-link-sheet',
  imports: [
    ReactiveFormsModule,
    ButtonComponent,
    CheckboxComponent,
    InputComponent,
    ListItemRowComponent,
    ListPhotoComponent,
    ListSectionComponent,
    SkeletonComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-col gap-cmn-3 px-cmn-6 pt-cmn-4 pb-cmn-6">
      @if (picked(); as product) {
        <p class="text-cmn-sm text-text-primary">
          Link “{{ data.item.text }}” to {{ product.name }}.
        </p>
        <div class="flex items-center gap-cmn-3">
          <cmn-checkbox
            [checked]="remember()"
            [label]="'Remember “' + data.item.text + '” for ' + product.name"
            (changed)="remember.set($event)"
          />
          <span class="text-cmn-sm text-text-primary"
            >Remember “{{ data.item.text }}” for {{ product.name }}</span
          >
        </div>
        <cmn-button
          [loading]="linking()"
          (clicked)="link(product)"
          class="block [&_button]:w-full"
          variant="primary"
          size="lg"
          icon="Link"
          >Link</cmn-button
        >
        <cmn-button
          [disabled]="linking()"
          (clicked)="picked.set(null)"
          class="block [&_button]:w-full"
          variant="secondary"
          size="lg"
          >Back</cmn-button
        >
      } @else {
        <cmn-input
          [formControl]="query"
          aria-label="Search the Shelf"
          placeholder="Search the Shelf"
          enterkeyhint="search"
          autocomplete="off"
        />
        @if (loading()) {
          <cmn-skeleton shape="section" />
        } @else if (tiles().length) {
          <cmn-list-section heading="Tap the product you mean" card>
            @for (tile of tiles(); track tile.product.id) {
              <cmn-list-item-row
                [label]="tile.product.name"
                [sublabel]="alsoCalled(tile.product)"
                [action]="!linking()"
                (activated)="pick(tile.product)"
                class="[&_.truncate]:overflow-visible [&_.truncate]:whitespace-normal [&_.truncate]:break-words"
              >
                <bk-list-photo [name]="tile.product.name" [src]="tile.imageUrl" avatar />
              </cmn-list-item-row>
            }
          </cmn-list-section>
        } @else {
          <p class="text-cmn-sm text-text-secondary">
            Nothing on the Shelf matches. Add the product on the Prices tab, then link this line.
          </p>
        }
      }
    </div>
  `,
})
export class ProductLinkSheetComponent {
  private readonly prices = inject(PricesStore);
  private readonly list = inject(ListStore);
  private readonly typed = signal('');

  protected readonly data = inject<ProductLinkData>(CMN_DRAWER_DATA);
  protected readonly ref = inject<CmnDrawerRef<boolean>>(CmnDrawerRef);

  protected readonly query = new FormControl('', {nonNullable: true});

  protected readonly picked = signal<Product | null>(null);
  protected readonly remember = signal(true);
  protected readonly linking = signal(false);

  protected readonly loading = computed(
    () => this.prices.status() === 'loading' && !this.prices.products().length
  );

  protected readonly tiles = computed(() => {
    const query = this.typed();
    return shelfTiles(
      this.prices.products().filter(product => productMatches(product, query)),
      'all'
    );
  });

  constructor() {
    this.query.valueChanges.pipe(takeUntilDestroyed()).subscribe(value => this.typed.set(value));
    if (this.prices.status() !== 'ready') {
      void this.prices.load(true);
    }
  }

  /** "Also: …" under a product that is listed for another name of it, so it is clear why it matched. */
  protected alsoCalled(product: Product): string | null {
    const name = matchedAlias(product, this.typed());
    return name ? `Also called “${name}”` : null;
  }

  /** A product whose name or other names are already these words links at once; otherwise the next step asks whether to keep them. */
  protected async pick(product: Product): Promise<void> {
    if (wouldAddName(product, this.data.item.text)) {
      this.remember.set(true);
      this.picked.set(product);
      return;
    }
    await this.link(product);
  }

  protected async link(product: Product): Promise<void> {
    if (this.linking()) {
      return;
    }
    this.linking.set(true);
    const remember = this.picked() !== null && this.remember();
    const linked = await this.list.linkProduct(this.data.item, product.id, remember);
    this.linking.set(false);
    if (linked) {
      if (remember) {
        void this.prices.load(true);
      }
      this.ref.close(true);
    }
  }
}
