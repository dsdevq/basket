import {DatePipe} from '@angular/common';
import {ChangeDetectionStrategy, Component, inject, type OnInit, signal} from '@angular/core';
import {
  ButtonComponent,
  CmnDrawerService,
  EmptyStateComponent,
  ListItemRowComponent,
  ListSectionComponent,
  SkeletonComponent,
  ToastService,
} from '@lifekit-hq/ui';
import {firstValueFrom} from 'rxjs';

import {AuthApi} from '../../core/auth/auth.api';
import type {Member, PendingInvite} from '../../core/auth/session.model';
import {InviteSheetComponent} from './invite.sheet';

/** Owner only: who is in, and the invite links that are still open. */
@Component({
  selector: 'bk-people-page',
  imports: [
    DatePipe,
    ButtonComponent,
    EmptyStateComponent,
    ListItemRowComponent,
    ListSectionComponent,
    SkeletonComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {class: 'block px-cmn-4 pb-cmn-4'},
  template: `
    <cmn-button
      (clicked)="invite()"
      variant="primary"
      size="lg"
      icon="UserPlus"
      class="mb-cmn-4 block [&_button]:w-full"
    >
      Invite someone
    </cmn-button>

    @if (loading()) {
      <cmn-skeleton shape="section" />
    } @else if (failed()) {
      <cmn-empty-state
        (action)="load()"
        kind="error"
        message="Could not load people"
        subMessage="Check your connection."
        actionLabel="Try again"
      />
    } @else {
      <cmn-list-section heading="In basket" card class="mb-cmn-4 block [&_button]:w-full">
        @for (member of members(); track member.id) {
          <cmn-list-item-row
            [label]="member.displayName"
            [sublabel]="member.role === 'owner' ? 'Owner' : 'Member'"
          />
        }
      </cmn-list-section>

      @if (invites().length) {
        <cmn-list-section heading="Open invite links" card class="block">
          @for (pending of invites(); track pending.id) {
            <cmn-list-item-row
              [label]="pending.label || 'Invite link'"
              [sublabel]="'Stops ' + (pending.expiresAt | date: 'EEE d MMM, HH:mm')"
            >
              <cmn-button
                (clicked)="revoke(pending)"
                actions
                variant="secondary"
                size="sm"
                icon="X"
              >
                Cancel link
              </cmn-button>
            </cmn-list-item-row>
          }
        </cmn-list-section>
      }
    }
  `,
})
export class PeoplePage implements OnInit {
  private readonly api = inject(AuthApi);
  private readonly drawer = inject(CmnDrawerService);
  private readonly toast = inject(ToastService);

  protected readonly members = signal<readonly Member[]>([]);
  protected readonly invites = signal<readonly PendingInvite[]>([]);
  protected readonly loading = signal(true);
  protected readonly failed = signal(false);

  public ngOnInit(): void {
    void this.load();
  }

  protected async load(): Promise<void> {
    try {
      const [members, invites] = await Promise.all([this.api.members(), this.api.pendingInvites()]);
      this.members.set(members);
      this.invites.set(invites);
      this.failed.set(false);
    } catch {
      this.failed.set(true);
    } finally {
      this.loading.set(false);
    }
  }

  protected async invite(): Promise<void> {
    const ref = this.drawer.open<boolean>(InviteSheetComponent, {
      title: 'Invite someone',
      mode: 'sheet',
    });
    await firstValueFrom(ref.afterClosed(), {defaultValue: undefined});
    await this.load();
  }

  protected async revoke(invite: PendingInvite): Promise<void> {
    try {
      await this.api.revokeInvite(invite.id);
      this.invites.set(this.invites().filter(candidate => candidate.id !== invite.id));
    } catch {
      this.toast.error('Could not cancel that link.');
    }
  }
}
