import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  type OnInit,
  signal,
} from '@angular/core';
import {Router} from '@angular/router';
import {
  AlertComponent,
  ButtonComponent,
  CmnDialogService,
  CmnDrawerService,
  EmptyStateComponent,
  PageComponent,
  SkeletonComponent,
  ToastService,
} from '@lifekit-hq/ui';
import {firstValueFrom} from 'rxjs';

import {HttpStatus, statusOf} from '../../core/http-status';
import {BarcodeComponent} from './barcode.component';
import {CardFormSheetComponent} from './card-form.sheet';
import {CardsApi} from './cards.api';
import {type Card, CHAINS, FORMAT_LABELS, groupedNumber} from './cards.model';
import {TillDialogComponent} from './till.dialog';

/**
 * One card: the code and the number as a preview, "Show at till" to put the code on the whole screen, and the
 * buttons to change the number or remove the card (the Lidl Plus and Stocard card screens).
 */
@Component({
  selector: 'bk-card-page',
  imports: [
    AlertComponent,
    BarcodeComponent,
    ButtonComponent,
    EmptyStateComponent,
    PageComponent,
    SkeletonComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-page kind="detail">
      @if (loading()) {
        <cmn-skeleton shape="section" />
      } @else if (missing()) {
        <cmn-empty-state
          (action)="back()"
          kind="no-results"
          message="No card here"
          subMessage="This card was removed or never added."
          actionLabel="Back to cards"
        />
      } @else if (failed()) {
        <cmn-empty-state
          (action)="load()"
          kind="error"
          message="Could not load this card"
          subMessage="Check your connection."
          actionLabel="Try again"
        />
      } @else if (card(); as shown) {
        <section
          [attr.aria-label]="shown.name + ' card'"
          class="flex w-full max-w-md flex-col gap-cmn-3"
        >
          <h2 class="font-headline text-cmn-xl font-semibold text-text-primary">
            {{ shown.name }}
          </h2>
          @if (shown.number; as number) {
            @if (unreadable()) {
              <cmn-alert title="This number does not fit the code" variant="error" />
            }
            <bk-barcode
              [value]="number"
              [format]="shown.format"
              [label]="shown.name + ' card ' + formatLabel()"
              (failed)="unreadable.set(true)"
            />
            <p
              class="break-all text-center font-mono text-cmn-lg text-text-primary"
              data-testid="number"
            >
              {{ spoken() }}
            </p>
            <p class="text-center text-cmn-xs text-text-secondary">{{ formatLabel() }}</p>
          } @else {
            <cmn-alert title="basket can no longer read this card number" variant="error" />
          }
        </section>
        <div class="flex w-full max-w-md flex-col gap-cmn-3">
          @if (shown.number) {
            <cmn-button
              (clicked)="showAtTill(shown)"
              class="block [&_button]:w-full"
              variant="primary"
              size="lg"
              icon="Maximize2"
              >Show at till</cmn-button
            >
          }
          <cmn-button
            (clicked)="change(shown)"
            class="block [&_button]:w-full"
            variant="secondary"
            size="lg"
            icon="Pencil"
            >Change number or code</cmn-button
          >
          <cmn-button
            (clicked)="remove(shown)"
            class="block [&_button]:w-full"
            variant="destructive"
            size="lg"
            icon="Trash2"
            >Remove card</cmn-button
          >
        </div>
      }
    </cmn-page>
  `,
})
export class CardPage implements OnInit {
  private readonly api = inject(CardsApi);
  private readonly dialog = inject(CmnDialogService);
  private readonly drawer = inject(CmnDrawerService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  /** The shop in the address (`/cards/:store`), bound by the router. */
  public readonly store = input.required<string>();

  protected readonly card = signal<Card | null>(null);
  protected readonly loading = signal(true);
  protected readonly missing = signal(false);
  protected readonly failed = signal(false);
  protected readonly unreadable = signal(false);

  protected readonly formatLabel = computed(() => {
    const card = this.card();
    return card ? FORMAT_LABELS[card.format] : '';
  });

  protected readonly spoken = computed(() => groupedNumber(this.card()?.number ?? ''));

  public ngOnInit(): void {
    void this.load();
  }

  protected async load(): Promise<void> {
    const chain = CHAINS.find(candidate => candidate.store === this.store());
    if (!chain) {
      this.missing.set(true);
      this.loading.set(false);
      return;
    }
    this.loading.set(true);
    try {
      this.card.set(await this.api.get(chain.store));
      this.failed.set(false);
      this.missing.set(false);
    } catch (error) {
      this.missing.set(statusOf(error) === HttpStatus.notFound);
      this.failed.set(statusOf(error) !== HttpStatus.notFound);
    } finally {
      this.loading.set(false);
    }
  }

  protected async back(): Promise<void> {
    await this.router.navigate(['/cards']);
  }

  protected showAtTill(card: Card): void {
    if (card.number === null) {
      return;
    }
    this.dialog.open(TillDialogComponent, {
      data: {card: {...card, number: card.number}},
      title: `${card.name} card`,
      ariaLabel: `${card.name} card at the till`,
      size: 'full',
      phone: 'full-screen',
    });
  }

  protected async change(card: Card): Promise<void> {
    const chain = CHAINS.find(candidate => candidate.store === card.store);
    if (!chain) {
      return;
    }
    const ref = this.drawer.open<Card>(CardFormSheetComponent, {
      title: `${card.name} card`,
      mode: 'sheet',
      data: {chain, existing: card},
    });
    const saved = await firstValueFrom(ref.afterClosed(), {defaultValue: undefined});
    if (saved) {
      this.unreadable.set(false);
      this.card.set(saved);
    }
  }

  protected async remove(card: Card): Promise<void> {
    const confirmed = await firstValueFrom(
      this.dialog.confirm({
        title: `Remove the ${card.name} card?`,
        message: 'The number is deleted from basket. You can add it again from the card itself.',
        confirmLabel: 'Remove',
        confirmVariant: 'destructive',
      }),
      {defaultValue: false}
    );
    if (!confirmed) {
      return;
    }
    try {
      await this.api.remove(card.store);
    } catch (error) {
      if (statusOf(error) !== HttpStatus.notFound) {
        this.toast.error('Could not remove that card.');
        return;
      }
    }
    await this.back();
  }
}
