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
  AsyncStateComponent,
  type AsyncStateStatus,
  ButtonComponent,
  CmnDrawerService,
  ListItemRowComponent,
  ListSectionComponent,
  PageComponent,
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
    AsyncStateComponent,
    ButtonComponent,
    ListItemRowComponent,
    ListSectionComponent,
    PageComponent,
    SkeletonComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-page kind="list">
      <cmn-button (clicked)="invite()" variant="primary" icon="UserPlus">
        Invite someone
      </cmn-button>

      <cmn-async-state
        [status]="asyncStatus()"
        [keepContent]="false"
        [crossfade]="false"
        (retry)="load()"
        emptySubMessage="Invite someone to share this list."
        errorMessage="Could not load people. Check your connection."
        class="contents"
        retryable
      >
        <cmn-skeleton shape="section" skeleton />
        <cmn-list-section heading="In basket" card>
          @for (member of members(); track member.id) {
            <cmn-list-item-row
              [label]="member.displayName"
              [sublabel]="member.role === 'owner' ? 'Owner' : 'Member'"
            />
          }
        </cmn-list-section>

        @if (invites().length) {
          <cmn-list-section heading="Open invite links" card>
            @for (pending of invites(); track pending.id) {
              <cmn-list-item-row
                [label]="pending.label || 'Invite link'"
                [sublabel]="'Stops ' + (pending.expiresAt | date: 'EEE d MMM, HH:mm')"
              >
                <cmn-button
                  [aria-label]="'Cancel link ' + (pending.label || 'Invite link')"
                  (clicked)="revoke(pending)"
                  actions
                  variant="ghost"
                  icon="X"
                />
              </cmn-list-item-row>
            }
          </cmn-list-section>
        }
      </cmn-async-state>
    </cmn-page>
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

  protected readonly asyncStatus = computed<AsyncStateStatus>(() => {
    if (this.loading()) {
      return 'loading';
    }
    return this.failed() ? 'error' : 'success';
  });

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
