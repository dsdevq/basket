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

import {AuthApi} from '../../core/auth/auth.api';
import type {Passkey} from '../../core/auth/session.model';
import {HttpStatus, statusOf} from '../../core/http-status';
import {AddPasskeySheetComponent} from './add-passkey.sheet';

/** The signed-in person's passkeys: see them, add another device, remove one - never the last. */
@Component({
  selector: 'bk-passkeys-section',
  imports: [
    DatePipe,
    ButtonComponent,
    EmptyStateComponent,
    ListItemRowComponent,
    SkeletonComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading()) {
      <cmn-skeleton shape="section" />
    } @else if (failed()) {
      <cmn-empty-state
        (action)="load()"
        kind="error"
        message="Could not load your passkeys"
        subMessage="Check your connection."
        actionLabel="Try again"
      />
    } @else {
      <div class="flex flex-col gap-cmn-3">
        @for (passkey of passkeys(); track passkey.id) {
          <cmn-list-item-row
            [label]="passkey.name"
            [sublabel]="'Added ' + (passkey.createdAt | date: 'd MMM y')"
            [trailingIcon]="removable() ? 'Trash2' : null"
            [trailingLabel]="'Remove passkey ' + passkey.name"
            (trailingAction)="remove(passkey)"
          />
        }
        @if (!removable()) {
          <p class="text-cmn-sm text-text-secondary">
            This is your only passkey. Add another one to be able to remove this one.
          </p>
        }
        <cmn-button (clicked)="add()" variant="secondary" icon="Plus">Add a passkey</cmn-button>
      </div>
    }
  `,
})
export class PasskeysSectionComponent implements OnInit {
  private readonly api = inject(AuthApi);
  private readonly dialog = inject(CmnDialogService);
  private readonly drawer = inject(CmnDrawerService);
  private readonly toast = inject(ToastService);

  protected readonly passkeys = signal<readonly Passkey[]>([]);
  protected readonly loading = signal(true);
  protected readonly failed = signal(false);

  public ngOnInit(): void {
    void this.load();
  }

  /** Removing is only offered while another passkey stays; the server refuses the last one regardless. */
  protected removable(): boolean {
    return this.passkeys().length > 1;
  }

  protected async load(): Promise<void> {
    try {
      this.passkeys.set(await this.api.passkeys());
      this.failed.set(false);
    } catch {
      this.failed.set(true);
    } finally {
      this.loading.set(false);
    }
  }

  protected async add(): Promise<void> {
    const ref = this.drawer.open<boolean>(AddPasskeySheetComponent, {
      title: 'Add a passkey',
      mode: 'sheet',
    });
    if (await firstValueFrom(ref.afterClosed(), {defaultValue: false})) {
      this.toast.success('Passkey added');
      await this.load();
    }
  }

  protected async remove(passkey: Passkey): Promise<void> {
    const confirmed = await firstValueFrom(
      this.dialog.confirm({
        title: 'Remove this passkey?',
        message: `"${passkey.name}" will no longer sign in to basket. The device keeps its copy, but it stops working here.`,
        confirmLabel: 'Remove',
        confirmVariant: 'destructive',
      }),
      {defaultValue: false}
    );
    if (!confirmed) {
      return;
    }
    try {
      await this.api.removePasskey(passkey.id);
      this.toast.success('Passkey removed');
    } catch (error) {
      this.toast.error(
        statusOf(error) === HttpStatus.conflict
          ? 'That is your only passkey. Add another one first.'
          : 'Could not remove that passkey.'
      );
    }
    await this.load();
  }
}
