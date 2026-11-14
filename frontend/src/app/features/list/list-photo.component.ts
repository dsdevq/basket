import {NgOptimizedImage} from '@angular/common';
import {ChangeDetectionStrategy, Component, computed, input, signal} from '@angular/core';

/**
 * The thumbnail on a list row: the product's photo when the shop gave one. With no photo, or one that does not load, the
 * first letter stands in on the surface colour, as on a Shelf tile, so every row keeps the same leading edge.
 */
@Component({
  selector: 'bk-list-photo',
  imports: [NgOptimizedImage],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {class: 'block shrink-0', 'aria-hidden': 'true'},
  template: `
    @if (photo(); as src) {
      <img
        [ngSrc]="src"
        (error)="failed.set(src)"
        class="size-10 rounded-cmn-md bg-surface-raised object-contain"
        alt=""
        width="40"
        height="40"
        referrerpolicy="no-referrer"
      />
    } @else {
      <div
        class="flex size-10 items-center justify-center rounded-cmn-md bg-surface-raised text-cmn-lg font-semibold text-text-secondary"
      >
        {{ initial() }}
      </div>
    }
  `,
})
export class ListPhotoComponent {
  public readonly name = input.required<string>();
  public readonly src = input<string | null>(null);

  /** The address that failed to load, so a changed address is tried again. */
  protected readonly failed = signal<string | null>(null);

  protected readonly photo = computed(() => {
    const src = this.src();
    return src && src !== this.failed() ? src : null;
  });
  protected readonly initial = computed(() => this.name().trim().charAt(0).toUpperCase() || '?');
}
