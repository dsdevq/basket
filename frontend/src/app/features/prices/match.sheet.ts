import {ChangeDetectionStrategy, Component, inject, type OnInit, signal} from '@angular/core';
import {
  ButtonComponent,
  CMN_DRAWER_DATA,
  CmnDrawerRef,
  EmptyStateComponent,
  SkeletonComponent,
} from '@lifekit-hq/ui';

import type {Candidate, MatchBasis, Product, ProductStore} from './prices.model';
import {PricesStore} from './prices.store';

export interface MatchSheetData {
  readonly product: Product;
  readonly entry: ProductStore;
}

/** `paste`: the person would rather paste the page's address than answer the suggestions. */
export type MatchSheetResult = 'paste';

function basisText(basis: MatchBasis): string {
  switch (basis) {
    case 'barcode':
      return 'Same barcode';
    case 'name_size':
      return 'Same name and size';
    default:
      return 'Close name, size not stated';
  }
}

/** The shop's own address shortened to what is worth reading on a phone: "lidl.ie/p/milbona-fresh-whole-milk-2-l". */
export function shortUrl(url: string): string {
  try {
    const parsed = new URL(url);
    return `${parsed.hostname.replace(/^www\./, '')}${parsed.pathname}`.replace(/\/$/, '');
  } catch {
    return url;
  }
}

/**
 * The match sheet: a shop's suggested pages for one product, one card each, with a big Yes and No. The yes/no card stack is
 * the pattern of Tinder-style review and of the "is this the same product?" prompts in price trackers, without the swipe: two
 * buttons, because a swipe is hard to find and impossible with one hand on a small phone. Answers are saved on the server,
 * so the other person sees them on the next load.
 */
@Component({
  selector: 'bk-match-sheet',
  imports: [ButtonComponent, EmptyStateComponent, SkeletonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-col gap-cmn-3 px-cmn-6 pt-cmn-4 pb-cmn-6">
      @if (candidates() === null) {
        <cmn-skeleton shape="section" />
      } @else if (!candidates()?.length) {
        <cmn-empty-state
          [subMessage]="'No more suggestions for ' + data.entry.name + '.'"
          icon="Check"
          message="Nothing left to check"
        />
      } @else {
        <p class="text-cmn-xs text-text-secondary">
          Is this the same as {{ data.product.name }}? {{ remaining() }} to check at
          {{ data.entry.name }}.
        </p>
        @let candidate = current();
        @if (candidate) {
          <section
            class="flex flex-col gap-cmn-2 rounded-cmn-lg border border-border-subtle p-cmn-4"
            aria-label="Suggested page"
          >
            <p class="break-words text-cmn-base font-semibold text-text-primary">
              {{ candidate.title || shortUrl(candidate.url) }}
            </p>
            <p class="text-cmn-xs text-text-secondary">{{ basis(candidate) }}</p>
            <a
              [href]="candidate.url"
              class="break-all text-cmn-xs text-text-link underline"
              target="_blank"
              rel="noopener noreferrer"
              >{{ shortUrl(candidate.url) }}</a
            >
          </section>
          <cmn-button
            [loading]="busy()"
            (clicked)="answer(candidate, true)"
            class="block [&_button]:w-full"
            variant="primary"
            size="lg"
            icon="Check"
            >Yes, same product</cmn-button
          >
          <cmn-button
            [disabled]="busy()"
            (clicked)="answer(candidate, false)"
            class="block [&_button]:w-full"
            variant="secondary"
            size="lg"
            icon="X"
            >No, not this one</cmn-button
          >
        }
      }
      <cmn-button
        (clicked)="ref.close('paste')"
        class="block [&_button]:w-full"
        variant="ghost"
        size="lg"
        icon="Link"
        >Paste the page address instead</cmn-button
      >
    </div>
  `,
})
export class MatchSheetComponent implements OnInit {
  private readonly prices = inject(PricesStore);
  protected readonly data = inject<MatchSheetData>(CMN_DRAWER_DATA);
  protected readonly ref = inject<CmnDrawerRef<MatchSheetResult>>(CmnDrawerRef);

  protected readonly candidates = signal<readonly Candidate[] | null>(null);
  protected readonly busy = signal(false);
  protected readonly shortUrl = shortUrl;

  protected remaining(): string {
    const count = this.candidates()?.length ?? 0;
    return count === 1 ? '1 suggestion' : `${count} suggestions`;
  }

  protected current(): Candidate | undefined {
    return this.candidates()?.[0];
  }

  protected basis(candidate: Candidate): string {
    return basisText(candidate.basis);
  }

  public ngOnInit(): void {
    void this.load();
  }

  private async load(): Promise<void> {
    const found = await this.prices.candidates(this.data.product, this.data.entry.store);
    if (found === null) {
      this.ref.close();
      return;
    }
    this.candidates.set(found);
  }

  protected async answer(candidate: Candidate, yes: boolean): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    const saved = yes
      ? await this.prices.confirm(this.data.product, candidate)
      : await this.prices.reject(this.data.product, candidate);
    this.busy.set(false);
    if (!saved) {
      if (yes) {
        await this.load();
      }
      return;
    }
    if (yes) {
      // The page is the product's equivalent now and the shop's other suggestions are settled.
      this.ref.close();
      return;
    }
    this.candidates.update(list => (list ?? []).filter(other => other.id !== candidate.id));
  }
}
