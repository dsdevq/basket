import {ChangeDetectionStrategy, Component, inject, type OnInit, signal} from '@angular/core';
import {Router} from '@angular/router';
import {
  CmnDrawerService,
  EmptyStateComponent,
  ListItemRowComponent,
  ListSectionComponent,
  PageComponent,
  SkeletonComponent,
} from '@lifekit-hq/ui';
import {firstValueFrom} from 'rxjs';

import {CardFormSheetComponent} from './card-form.sheet';
import {CardsApi} from './cards.api';
import {type Card, type CardSummary, type Chain, CHAINS, FORMAT_LABELS} from './cards.model';

interface Row {
  readonly chain: Chain;
  readonly card: CardSummary | null;
}

/** The four shops' loyalty cards: tap one to show its code at the till, or to add it the first time. */
@Component({
  selector: 'bk-cards-page',
  imports: [
    EmptyStateComponent,
    ListItemRowComponent,
    ListSectionComponent,
    PageComponent,
    SkeletonComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-page kind="list">
      @if (loading()) {
        <cmn-skeleton shape="section" />
      } @else if (failed()) {
        <cmn-empty-state
          (action)="load()"
          kind="error"
          message="Could not load your cards"
          subMessage="Check your connection."
          actionLabel="Try again"
        />
      } @else {
        <cmn-list-section heading="Your cards" card>
          @for (row of rows(); track row.chain.store) {
            <cmn-list-item-row
              [label]="row.chain.name"
              [sublabel]="sublabel(row)"
              [action]="true"
              (activated)="open(row)"
            />
          }
        </cmn-list-section>
      }
    </cmn-page>
  `,
})
export class CardsPage implements OnInit {
  private readonly api = inject(CardsApi);
  private readonly drawer = inject(CmnDrawerService);
  private readonly router = inject(Router);

  protected readonly rows = signal<readonly Row[]>([]);
  protected readonly loading = signal(true);
  protected readonly failed = signal(false);

  public ngOnInit(): void {
    void this.load();
  }

  protected async load(): Promise<void> {
    try {
      const cards = await this.api.list();
      this.rows.set(
        CHAINS.map(chain => ({chain, card: cards.find(card => card.store === chain.store) ?? null}))
      );
      this.failed.set(false);
    } catch {
      this.failed.set(true);
    } finally {
      this.loading.set(false);
    }
  }

  protected sublabel(row: Row): string {
    if (!row.card) {
      return 'Not added yet';
    }
    return row.card.readable
      ? `${row.card.masked} · ${FORMAT_LABELS[row.card.format]}`
      : 'Cannot be read. Add it again.';
  }

  protected async open(row: Row): Promise<void> {
    if (row.card?.readable) {
      await this.router.navigate(['/cards', row.chain.store]);
      return;
    }
    const ref = this.drawer.open<Card>(CardFormSheetComponent, {
      title: `${row.chain.name} card`,
      mode: 'sheet',
      data: {chain: row.chain, existing: null},
    });
    const saved = await firstValueFrom(ref.afterClosed(), {defaultValue: undefined});
    if (saved) {
      await this.router.navigate(['/cards', row.chain.store]);
    }
  }
}
