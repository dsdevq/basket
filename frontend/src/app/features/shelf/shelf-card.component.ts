import {NgOptimizedImage} from '@angular/common';
import {ChangeDetectionStrategy, Component, computed, input, output, signal} from '@angular/core';
import {SelectableCardComponent, TagComponent} from '@lifekit-hq/ui';

import {type CardPrice, cardPrice, type ShelfFilter, type ShelfTile} from './shelf-format';

/**
 * One product on the Shelf, drawn as a card: photo, name and what the shop's latest reading says (price, old price struck
 * through, promo, card price, how long it lasts, when it was read, out of stock). Pressing it puts the product on the list;
 * the card stays pressed while it is there. With no photo, or one that does not load, the initial letter stands in.
 */
@Component({
  selector: 'bk-shelf-card',
  imports: [NgOptimizedImage, SelectableCardComponent, TagComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {class: 'block h-full min-w-0'},
  template: `
    <cmn-selectable-card
      [selected]="onList()"
      [disabled]="busy()"
      (clicked)="picked.emit()"
      class="block h-full"
      orientation="vertical"
    >
      <div leading>
        @if (photo(); as src) {
          <img
            [ngSrc]="src"
            (error)="failed.set(src)"
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
            {{ initial() }}
          </div>
        }
      </div>
      <span class="line-clamp-2 break-words text-cmn-sm font-medium text-text-primary">{{
        tile().product.name
      }}</span>
      @if (price(); as p) {
        <span class="flex flex-wrap items-baseline justify-center gap-x-cmn-2">
          <span class="text-cmn-lg font-semibold text-text-primary">{{ p.main }}</span>
          @if (p.was) {
            <s class="text-cmn-xs text-text-secondary">{{ p.was }}</s>
          }
        </span>
        @if (p.loyalty) {
          <span class="text-cmn-xs text-text-secondary">{{ p.loyalty }}</span>
        }
        @if (p.promo) {
          <cmn-tag variant="success">{{ p.promo }}</cmn-tag>
        }
        @if (p.until) {
          <span class="text-cmn-xs text-text-secondary">{{ p.until }}</span>
        }
        <span class="text-cmn-xs text-text-secondary">{{ p.seen }}</span>
        @if (p.outOfStock) {
          <cmn-tag variant="error">Out of stock</cmn-tag>
        }
      }
      @if (onList()) {
        <cmn-tag variant="info">On the list</cmn-tag>
      }
    </cmn-selectable-card>
  `,
})
export class ShelfCardComponent {
  public readonly tile = input.required<ShelfTile>();
  public readonly filter = input.required<ShelfFilter>();
  public readonly onList = input(false);
  public readonly busy = input(false);
  public readonly picked = output();

  /** The address that failed to load, so a changed address is tried again. */
  protected readonly failed = signal<string | null>(null);

  protected readonly photo = computed(() => {
    const src = this.tile().imageUrl;
    return src && src !== this.failed() ? src : null;
  });
  protected readonly initial = computed(
    () => this.tile().product.name.trim().charAt(0).toUpperCase() || '?'
  );
  protected readonly price = computed<CardPrice | null>(() => {
    const entry = this.tile().entry;
    return entry ? cardPrice(entry, this.filter()) : null;
  });
}
