import {DatePipe} from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  type OnInit,
  signal,
} from '@angular/core';
import {
  AlertComponent,
  ButtonComponent,
  CmnDialogService,
  CmnDrawerService,
  EmptyStateComponent,
  SkeletonComponent,
  ToastService,
} from '@lifekit-hq/ui';
import {firstValueFrom} from 'rxjs';

import {HttpStatus, statusOf} from '../../core/http-status';
import {LidlPlusApi} from './lidl-plus.api';
import type {LidlPlusStatus} from './lidl-plus.model';
import {LidlPlusTokenSheetComponent} from './lidl-plus-token.sheet';

/**
 * Owner only: whether basket can sign in to Lidl Plus. Paste the refresh token once; the section says whether Lidl accepts it,
 * lets the owner ask again, replace the token or remove it. The token itself is never shown.
 */
@Component({
  selector: 'bk-lidl-plus-section',
  imports: [AlertComponent, ButtonComponent, EmptyStateComponent, SkeletonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading()) {
      <cmn-skeleton shape="section" />
    } @else if (failed()) {
      <cmn-empty-state
        (action)="load()"
        kind="error"
        message="Could not load the Lidl Plus connection"
        subMessage="Check your connection."
        actionLabel="Try again"
      />
    } @else if (status(); as current) {
      <div class="flex flex-col gap-cmn-3">
        @switch (current.state) {
          @case ('connected') {
            <cmn-alert [title]="summary()" variant="success" />
          }
          @case ('needs_new_token') {
            <cmn-alert [title]="summary()" variant="warning" />
            <p class="text-cmn-sm text-text-secondary">
              Lidl no longer accepts the saved token. Paste a new one to reconnect.
            </p>
          }
          @case ('unreadable') {
            <cmn-alert [title]="summary()" variant="warning" />
            <p class="text-cmn-sm text-text-secondary">
              basket cannot read the saved token any more. Paste it again to reconnect.
            </p>
          }
          @default {
            <p class="text-cmn-sm text-text-secondary">
              Not connected. Paste your Lidl Plus token once and basket keeps it encrypted to read
              your Lidl Plus data later.
            </p>
          }
        }
        <cmn-button
          [variant]="current.state === 'connected' ? 'secondary' : 'primary'"
          (clicked)="paste()"
          icon="KeyRound"
        >
          {{ current.state === 'not_connected' ? 'Connect Lidl Plus' : 'Paste a new token' }}
        </cmn-button>
        @if (current.state !== 'not_connected') {
          @if (current.state === 'connected') {
            <cmn-button
              [loading]="checking()"
              (clicked)="check()"
              variant="secondary"
              icon="RefreshCw"
              >Check now</cmn-button
            >
          }
          <cmn-button (clicked)="disconnect()" variant="secondary" icon="Unplug"
            >Disconnect</cmn-button
          >
        }
      </div>
    }
  `,
  providers: [DatePipe],
})
export class LidlPlusSectionComponent implements OnInit {
  private readonly api = inject(LidlPlusApi);
  private readonly dialog = inject(CmnDialogService);
  private readonly drawer = inject(CmnDrawerService);
  private readonly toast = inject(ToastService);
  private readonly date = inject(DatePipe);

  protected readonly status = signal<LidlPlusStatus | null>(null);
  protected readonly loading = signal(true);
  protected readonly failed = signal(false);
  protected readonly checking = signal(false);

  protected readonly summary = computed(() => {
    const current = this.status();
    switch (current?.state) {
      case 'connected':
        return `Connected. Lidl accepted the token on ${this.day(current.verifiedAt)}`;
      case 'needs_new_token':
        return 'Lidl Plus needs a new token';
      case 'unreadable':
        return 'The saved Lidl Plus token cannot be read';
      default:
        return 'Not connected';
    }
  });

  public ngOnInit(): void {
    void this.load();
  }

  protected async load(): Promise<void> {
    try {
      this.status.set(await this.api.status());
      this.failed.set(false);
    } catch {
      this.failed.set(true);
    } finally {
      this.loading.set(false);
    }
  }

  protected async paste(): Promise<void> {
    const ref = this.drawer.open<LidlPlusStatus>(LidlPlusTokenSheetComponent, {
      title: 'Connect Lidl Plus',
      mode: 'sheet',
    });
    const saved = await firstValueFrom(ref.afterClosed(), {defaultValue: undefined});
    if (saved) {
      this.status.set(saved);
      this.toast.success('Lidl Plus connected');
    }
  }

  protected async check(): Promise<void> {
    this.checking.set(true);
    try {
      const status = await this.api.check();
      this.status.set(status);
      if (status.state === 'connected') {
        this.toast.success('Lidl accepted the token');
      }
    } catch (error) {
      this.toast.error(
        statusOf(error) === HttpStatus.badGateway
          ? 'Could not reach Lidl just now. The token was left as it is.'
          : 'Could not check the connection.'
      );
    } finally {
      this.checking.set(false);
    }
  }

  protected async disconnect(): Promise<void> {
    const confirmed = await firstValueFrom(
      this.dialog.confirm({
        title: 'Disconnect Lidl Plus?',
        message:
          'basket forgets the saved token. You will need to paste a token again to reconnect.',
        confirmLabel: 'Disconnect',
        confirmVariant: 'destructive',
      }),
      {defaultValue: false}
    );
    if (!confirmed) {
      return;
    }
    try {
      await this.api.disconnect();
      this.toast.success('Lidl Plus disconnected');
    } catch {
      this.toast.error('Could not disconnect.');
    }
    await this.load();
  }

  private day(value: string | null): string {
    return (value && this.date.transform(value, 'd MMM y')) || 'an unknown day';
  }
}
