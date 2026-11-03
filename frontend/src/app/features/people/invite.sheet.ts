import {Clipboard} from '@angular/cdk/clipboard';
import {DatePipe} from '@angular/common';
import {ChangeDetectionStrategy, Component, inject, signal} from '@angular/core';
import {FormControl, FormGroup, ReactiveFormsModule} from '@angular/forms';
import {
  AlertComponent,
  ButtonComponent,
  CmnDrawerRef,
  FormFieldComponent,
  InputComponent,
  ToastService,
} from '@lifekit-hq/ui';

import {AuthApi} from '../../core/auth/auth.api';
import type {CreatedInvite} from '../../core/auth/session.model';
import {HttpStatus, statusOf} from '../../core/http-status';

/** Bottom sheet: name the person, get a 24-hour one-time link, copy or share it. */
@Component({
  selector: 'bk-invite-sheet',
  imports: [
    ReactiveFormsModule,
    DatePipe,
    AlertComponent,
    ButtonComponent,
    FormFieldComponent,
    InputComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (created(); as invite) {
      <div class="flex flex-col gap-cmn-3 px-cmn-4 pb-cmn-4">
        <p class="text-cmn-sm text-text-primary">
          Send this link to {{ invite.label || 'them' }}. It works once and stops on
          {{ invite.expiresAt | date: 'EEE d MMM, HH:mm' }}.
        </p>
        <p
          class="break-all rounded-cmn-md bg-surface-raised p-cmn-3 text-cmn-xs text-text-secondary"
        >
          {{ invite.link }}
        </p>
        <cmn-button
          (clicked)="copy(invite)"
          class="block [&_button]:w-full"
          variant="primary"
          size="lg"
          icon="Copy"
          >Copy link</cmn-button
        >
        @if (canShare) {
          <cmn-button
            (clicked)="share(invite)"
            class="block [&_button]:w-full"
            variant="secondary"
            size="lg"
            icon="Share"
            >Share…</cmn-button
          >
        }
        <cmn-button
          (clicked)="ref.close(true)"
          class="block [&_button]:w-full"
          variant="secondary"
          size="lg"
          >Done</cmn-button
        >
      </div>
    } @else {
      <form
        [formGroup]="form"
        (ngSubmit)="create()"
        class="flex flex-col gap-cmn-3 px-cmn-4 pb-cmn-4"
      >
        @if (problem()) {
          <cmn-alert [title]="problem()" variant="error" />
        }
        <cmn-form-field
          [control]="label"
          label="Who is it for?"
          hint="Only you see this, to tell links apart"
        >
          <cmn-input formControlName="label" enterkeyhint="done" placeholder="e.g. Anna" />
        </cmn-form-field>
        <cmn-button
          [loading]="busy()"
          class="block [&_button]:w-full"
          type="submit"
          variant="primary"
          size="lg"
          icon="UserPlus"
        >
          Make invite link
        </cmn-button>
      </form>
    }
  `,
})
export class InviteSheetComponent {
  private readonly api = inject(AuthApi);
  private readonly clipboard = inject(Clipboard);
  private readonly toast = inject(ToastService);
  protected readonly ref = inject<CmnDrawerRef<boolean>>(CmnDrawerRef);

  protected readonly label = new FormControl('', {nonNullable: true});
  protected readonly form = new FormGroup({label: this.label});
  protected readonly busy = signal(false);
  protected readonly problem = signal('');
  protected readonly created = signal<CreatedInvite | null>(null);
  protected readonly canShare =
    typeof navigator !== 'undefined' && typeof navigator.share === 'function';

  protected async create(): Promise<void> {
    this.busy.set(true);
    this.problem.set('');
    try {
      this.created.set(await this.api.createInvite(this.label.value.trim()));
    } catch (error) {
      this.problem.set(
        statusOf(error) === HttpStatus.tooManyRequests
          ? 'That is a lot of invites for one hour. Try again later.'
          : 'Could not make the link. Try again.'
      );
    } finally {
      this.busy.set(false);
    }
  }

  protected copy(invite: CreatedInvite): void {
    if (this.clipboard.copy(invite.link)) {
      this.toast.success('Link copied');
    } else {
      this.toast.error('Could not copy. Select the link and copy it by hand.');
    }
  }

  protected async share(invite: CreatedInvite): Promise<void> {
    try {
      await navigator.share({title: 'Join basket', url: invite.link});
    } catch {
      /* dismissed */
    }
  }
}
