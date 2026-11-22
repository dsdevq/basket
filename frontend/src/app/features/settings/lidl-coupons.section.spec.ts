import {HttpErrorResponse} from '@angular/common/http';
import {TestBed} from '@angular/core/testing';
import {ToastService} from '@lifekit-hq/ui';

import {LidlCouponsApi} from './lidl-coupons.api';
import type {LidlCoupon, LidlCoupons} from './lidl-coupons.model';
import {LidlCouponsSectionComponent} from './lidl-coupons.section';

const coupon = (id: string, over: Partial<LidlCoupon> = {}): LidlCoupon => ({
  id,
  title: `Coupon ${id}`,
  discount: '20% off',
  description: null,
  validFrom: null,
  validUntil: '2026-11-25',
  activated: false,
  counts: false,
  ...over,
});

const answer = (coupons: LidlCoupon[], over: Partial<LidlCoupons> = {}): LidlCoupons => ({
  state: 'connected',
  fresh: true,
  coupons,
  activated: 0,
  refused: 0,
  ...over,
});

async function render(api: Partial<Record<keyof LidlCouponsApi, ReturnType<typeof vi.fn>>>) {
  const toast = {success: vi.fn(), error: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      {provide: LidlCouponsApi, useValue: api},
      {provide: ToastService, useValue: toast},
    ],
  });
  const fixture = TestBed.createComponent(LidlCouponsSectionComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const press = async (label: string): Promise<void> => {
    Array.from(host.querySelectorAll<HTMLButtonElement>('button'))
      .find(b => b.textContent?.includes(label))
      ?.click();
    await fixture.whenStable();
    fixture.detectChanges();
  };
  return {host, toast, press};
}

describe('LidlCouponsSectionComponent', () => {
  it('lists each coupon with its wording, end day and whether it is on', async () => {
    const {host} = await render({
      list: vi
        .fn()
        .mockResolvedValue(
          answer([coupon('a'), coupon('b', {activated: true, counts: true, discount: '€1 off'})])
        ),
    });

    const rows = Array.from(host.querySelectorAll('[data-testid="lidl-coupon"]')).map(
      r => r.textContent?.replace(/\s+/g, ' ').trim() ?? ''
    );
    expect(rows[0]).toContain('Coupon a');
    expect(rows[0]).toContain('20% off · until 25 Nov · off');
    expect(rows[1]).toContain('€1 off · until 25 Nov · on');
  });

  it('shows nothing when there are no coupons or Lidl cannot be asked', async () => {
    const none = await render({list: vi.fn().mockResolvedValue(answer([]))});
    expect(none.host.querySelector('[data-testid="lidl-coupons"]')).toBeNull();
    TestBed.resetTestingModule();

    const failed = await render({list: vi.fn().mockRejectedValue(new Error('down'))});
    expect(failed.host.querySelector('[data-testid="lidl-coupons"]')).toBeNull();
  });

  it('says so when the list is the last one basket kept', async () => {
    const {host} = await render({
      list: vi.fn().mockResolvedValue(answer([coupon('a')], {fresh: false})),
    });

    expect(host.textContent).toContain('last list basket kept');
  });

  it('switches one coupon on and shows the new state', async () => {
    const activate = vi
      .fn()
      .mockResolvedValue(answer([coupon('a', {activated: true, counts: true})], {activated: 1}));
    const {host, toast, press} = await render({
      list: vi.fn().mockResolvedValue(answer([coupon('a')])),
      activate,
    });

    await press('Switch on');

    expect(activate).toHaveBeenCalledWith('a');
    expect(toast.success).toHaveBeenCalledWith('Switched on 1');
    expect(host.textContent).toContain('· on');
  });

  it('switches every coupon on at once', async () => {
    const activateAll = vi
      .fn()
      .mockResolvedValue(
        answer([coupon('a', {activated: true}), coupon('b', {activated: true})], {activated: 2})
      );
    const {press, toast} = await render({
      list: vi.fn().mockResolvedValue(answer([coupon('a'), coupon('b')])),
      activateAll,
    });

    await press('Switch on all');

    expect(activateAll).toHaveBeenCalled();
    expect(toast.success).toHaveBeenCalledWith('Switched on 2');
  });

  it('keeps the list and says so when Lidl does not answer an activation', async () => {
    const {host, toast, press} = await render({
      list: vi.fn().mockResolvedValue(answer([coupon('a')])),
      activate: vi.fn().mockRejectedValue(new HttpErrorResponse({status: 502})),
    });

    await press('Switch on');

    expect(toast.error).toHaveBeenCalledWith('Could not reach Lidl just now. Nothing was changed.');
    expect(host.textContent).toContain('Coupon a');
  });
});
