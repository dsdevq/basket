import {DatePipe} from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  type OnInit,
  signal,
} from '@angular/core';
import {ButtonComponent, ToastService} from '@lifekit-hq/ui';

import {HttpStatus, statusOf} from '../../core/http-status';
import {LidlCouponsApi} from './lidl-coupons.api';
import type {LidlCoupon, LidlCoupons} from './lidl-coupons.model';

/**
 * The Lidl Plus coupons, shown under a working connection. In Ireland a coupon only counts once it is switched on, and basket prices a
 * Lidl line with the switched-on ones. Lidl's coupon service is unofficial, so a list that cannot be read shows nothing at all.
 */
@Component({
  selector: 'bk-lidl-coupons',
  imports: [ButtonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [DatePipe],
  template: `
    @if (coupons().length > 0) {
      <div class="flex flex-col gap-cmn-3" data-testid="lidl-coupons">
        <h3 class="text-cmn-base font-semibold text-text-primary">Coupons</h3>
        @if (!fresh()) {
          <p class="text-cmn-sm text-text-secondary">
            Lidl did not answer just now. This is the last list basket kept.
          </p>
        }
        <ul class="flex flex-col gap-cmn-2">
          @for (coupon of coupons(); track coupon.id) {
            <li class="flex items-center justify-between gap-cmn-3" data-testid="lidl-coupon">
              <div class="flex min-w-0 flex-col">
                <span class="text-cmn-base text-text-primary">{{ coupon.title }}</span>
                <span class="text-cmn-sm text-text-secondary">{{ detail(coupon) }}</span>
              </div>
              @if (canSwitchOn(coupon)) {
                <cmn-button
                  [loading]="busy() === coupon.id"
                  (clicked)="activate(coupon)"
                  variant="secondary"
                  >Switch on</cmn-button
                >
              }
            </li>
          }
        </ul>
        @if (pending() > 0) {
          <cmn-button [loading]="busy() === 'all'" (clicked)="activateAll()" icon="Ticket"
            >Switch on all</cmn-button
          >
        }
      </div>
    }
  `,
})
export class LidlCouponsSectionComponent implements OnInit {
  private readonly api = inject(LidlCouponsApi);
  private readonly toast = inject(ToastService);
  private readonly date = inject(DatePipe);

  protected readonly coupons = signal<readonly LidlCoupon[]>([]);
  protected readonly fresh = signal(true);
  protected readonly busy = signal<string | null>(null);
  protected readonly pending = computed(
    () => this.coupons().filter(c => this.canSwitchOn(c)).length
  );

  public ngOnInit(): void {
    void this.load();
  }

  /** Lidl only takes a coupon on while it is in date, so a coupon that has not started (or has ended) has nothing to switch on. */
  protected canSwitchOn(coupon: LidlCoupon): boolean {
    const today = this.today();
    return (
      !coupon.activated &&
      (!coupon.validFrom || coupon.validFrom <= today) &&
      (!coupon.validUntil || coupon.validUntil >= today)
    );
  }

  private today(): string {
    return new Date().toLocaleDateString('en-CA');
  }

  protected detail(coupon: LidlCoupon): string {
    const parts = [coupon.discount];
    if (coupon.validUntil) {
      parts.push(`until ${this.date.transform(coupon.validUntil, 'd MMM') ?? coupon.validUntil}`);
    }
    parts.push(coupon.counts ? 'on' : coupon.activated ? 'on, not in date' : 'off');
    return parts.filter(Boolean).join(' · ');
  }

  protected async activate(coupon: LidlCoupon): Promise<void> {
    await this.run(coupon.id, () => this.api.activate(coupon.id));
  }

  protected async activateAll(): Promise<void> {
    await this.run('all', () => this.api.activateAll());
  }

  private async load(): Promise<void> {
    try {
      this.show(await this.api.list());
    } catch {
      this.coupons.set([]);
    }
  }

  private async run(key: string, action: () => Promise<LidlCoupons>): Promise<void> {
    this.busy.set(key);
    try {
      const answer = await action();
      this.show(answer);
      if (answer.activated > 0) {
        this.toast.success(`Switched on ${answer.activated}`);
      } else if (answer.refused > 0) {
        this.toast.error('Lidl refused some coupons. They are still off.');
      } else {
        this.toast.success('Nothing to switch on');
      }
    } catch (error) {
      this.toast.error(
        statusOf(error) === HttpStatus.badGateway
          ? 'Could not reach Lidl just now. Nothing was changed.'
          : 'Could not switch the coupon on.'
      );
    } finally {
      this.busy.set(null);
    }
  }

  private show(answer: LidlCoupons): void {
    this.coupons.set(answer.state === 'connected' ? answer.coupons : []);
    this.fresh.set(answer.fresh);
  }
}
