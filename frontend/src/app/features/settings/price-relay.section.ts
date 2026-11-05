import {DatePipe} from '@angular/common';
import {ChangeDetectionStrategy, Component, inject, type OnInit, signal} from '@angular/core';
import {
  ButtonComponent,
  CmnDialogService,
  CmnDrawerService,
  EmptyStateComponent,
  ListItemRowComponent,
  SkeletonComponent,
  ToastService,
} from '@lifekit-hq/ui';
import {firstValueFrom} from 'rxjs';

import type {IngestToken} from '../../core/auth/session.model';
import {PriceRelayApi} from './price-relay.api';
import {PriceTokenSheetComponent} from './price-token.sheet';

/** Owner only: the tokens the PC price reader posts Tesco and Aldi prices with. Make one, revoke one. */
@Component({
  selector: 'bk-price-relay-section',
  imports: [ButtonComponent, EmptyStateComponent, ListItemRowComponent, SkeletonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading()) {
      <cmn-skeleton shape="section" />
    } @else if (failed()) {
      <cmn-empty-state
        (action)="load()"
        kind="error"
        message="Could not load the price tokens"
        subMessage="Check your connection."
        actionLabel="Try again"
      />
    } @else {
      <div class="flex flex-col gap-cmn-3">
        @for (token of tokens(); track token.id) {
          <cmn-list-item-row
            [label]="token.label || 'Price reader'"
            [sublabel]="describe(token)"
            [trailingLabel]="'Revoke token ' + (token.label || 'Price reader')"
            (trailingAction)="revoke(token)"
            trailingIcon="Trash2"
          />
        } @empty {
          <p class="text-cmn-sm text-text-secondary">
            No tokens yet. Tesco and Aldi block basket's server, so your PC reads their prices and
            sends them here with a token.
          </p>
        }
        <cmn-button (clicked)="add()" variant="secondary" icon="Plus">Make a token</cmn-button>
      </div>
    }
  `,
  providers: [DatePipe],
})
export class PriceRelaySectionComponent implements OnInit {
  private readonly api = inject(PriceRelayApi);
  private readonly dialog = inject(CmnDialogService);
  private readonly drawer = inject(CmnDrawerService);
  private readonly toast = inject(ToastService);
  private readonly date = inject(DatePipe);

  protected readonly tokens = signal<readonly IngestToken[]>([]);
  protected readonly loading = signal(true);
  protected readonly failed = signal(false);

  public ngOnInit(): void {
    void this.load();
  }

  protected describe(token: IngestToken): string {
    return `Made ${this.date.transform(token.createdAt, 'd MMM y') ?? ''}`;
  }

  protected async load(): Promise<void> {
    try {
      this.tokens.set(await this.api.tokens());
      this.failed.set(false);
    } catch {
      this.failed.set(true);
    } finally {
      this.loading.set(false);
    }
  }

  protected async add(): Promise<void> {
    const ref = this.drawer.open<boolean>(PriceTokenSheetComponent, {
      title: 'Make a price token',
      mode: 'sheet',
    });
    if (await firstValueFrom(ref.afterClosed(), {defaultValue: false})) {
      await this.load();
    }
  }

  protected async revoke(token: IngestToken): Promise<void> {
    const confirmed = await firstValueFrom(
      this.dialog.confirm({
        title: 'Revoke this token?',
        message: `"${token.label || 'Price reader'}" stops working at once. The reader will need a new token to send prices again.`,
        confirmLabel: 'Revoke',
        confirmVariant: 'destructive',
      }),
      {defaultValue: false}
    );
    if (!confirmed) {
      return;
    }
    try {
      await this.api.revokeToken(token.id);
      this.toast.success('Token revoked');
    } catch {
      this.toast.error('Could not revoke that token.');
    }
    await this.load();
  }
}
