import {ChangeDetectionStrategy, Component, inject, signal} from '@angular/core';
import {Router} from '@angular/router';
import {ButtonComponent} from '@lifekit-hq/ui';

import {AuthCardComponent} from './auth-card.component';
import {SessionStore} from './session.store';

/** Shown instead of a blank screen when the first `/api/session` call does not get an answer. */
@Component({
  selector: 'bk-unreachable-page',
  imports: [AuthCardComponent, ButtonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <bk-auth-card
      heading="Cannot reach basket"
      lead="Your browser could not get an answer from the server."
    >
      <p class="text-cmn-sm text-text-secondary">
        Check your connection. A content blocker or a private-relay setting can also stop it.
      </p>
      @if (session.loadFailure(); as failure) {
        <p class="text-cmn-xs text-text-secondary">Detail: {{ failure }}</p>
      }
      <cmn-button [loading]="busy()" (clicked)="retry()" variant="primary" icon="RefreshCw">
        Try again
      </cmn-button>
    </bk-auth-card>
  `,
})
export class UnreachablePage {
  private readonly router = inject(Router);
  protected readonly session = inject(SessionStore);

  protected readonly busy = signal(false);

  protected async retry(): Promise<void> {
    this.busy.set(true);
    try {
      await this.session.ensureLoaded();
      if (!this.session.loadFailure()) {
        // The guards decide: the list for a signed-in person, sign-in for everyone else.
        await this.router.navigateByUrl('/list');
      }
    } finally {
      this.busy.set(false);
    }
  }
}
