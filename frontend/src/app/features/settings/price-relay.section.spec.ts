import {TestBed} from '@angular/core/testing';
import {CmnDialogService, CmnDrawerService, ToastService} from '@lifekit-hq/ui';
import {of} from 'rxjs';

import type {IngestToken} from '../../core/auth/session.model';
import {PriceRelayApi} from './price-relay.api';
import {PriceRelaySectionComponent} from './price-relay.section';

const HOME: IngestToken = {
  id: 't1',
  label: 'Home PC',
  createdAt: '2026-11-05T09:00:00Z',
  lastUsedAt: '2026-11-06T03:10:00Z',
};
const FRESH: IngestToken = {
  id: 't2',
  label: null,
  createdAt: '2026-11-06T09:00:00Z',
  lastUsedAt: null,
};

async function render(
  api: Partial<Record<keyof PriceRelayApi, ReturnType<typeof vi.fn>>>,
  options: {confirm?: boolean; sheetSaved?: boolean} = {}
) {
  const toast = {success: vi.fn(), error: vi.fn()};
  const confirm = vi.fn().mockReturnValue(of(options.confirm ?? true));
  const open = vi.fn().mockReturnValue({afterClosed: () => of(options.sheetSaved ?? false)});
  TestBed.configureTestingModule({
    providers: [
      {provide: PriceRelayApi, useValue: api},
      {provide: ToastService, useValue: toast},
      {provide: CmnDialogService, useValue: {confirm}},
      {provide: CmnDrawerService, useValue: {open}},
    ],
  });
  const fixture = TestBed.createComponent(PriceRelaySectionComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const settle = async (): Promise<void> => {
    await fixture.whenStable();
    fixture.detectChanges();
  };
  return {host, toast, confirm, open, settle};
}

function buttons(host: HTMLElement): HTMLButtonElement[] {
  return Array.from(host.querySelectorAll<HTMLButtonElement>('button'));
}

describe('PriceRelaySectionComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('lists each token by label with when it was made and last used, never the token itself', async () => {
    const {host} = await render({tokens: vi.fn().mockResolvedValue([HOME, FRESH])});

    const text = host.textContent ?? '';
    expect(text).toContain('Home PC');
    expect(text).toContain('Made 5 Nov 2026');
    expect(text).toContain('used 6 Nov 2026');
    expect(text).toContain('Price reader');
    expect(text).toContain('never used');
  });

  it('explains itself when there are no tokens yet', async () => {
    const {host} = await render({tokens: vi.fn().mockResolvedValue([])});

    expect(host.textContent).toContain('No tokens yet');
    expect(host.textContent).toContain('Make a token');
  });

  it('offers a retry when the tokens cannot be loaded', async () => {
    const tokens = vi.fn().mockRejectedValueOnce(new Error('offline')).mockResolvedValue([HOME]);
    const {host, settle} = await render({tokens});

    expect(host.textContent).toContain('Could not load the price tokens');
    buttons(host)
      .find(b => b.textContent?.includes('Try again'))
      ?.click();
    await settle();

    expect(host.textContent).toContain('Home PC');
  });

  it('revokes a token after the owner confirms, then reloads the list', async () => {
    const tokens = vi.fn().mockResolvedValueOnce([HOME]).mockResolvedValue([]);
    const revokeToken = vi.fn().mockResolvedValue(undefined);
    const {host, toast, confirm, settle} = await render({tokens, revokeToken});

    buttons(host)
      .find(b => b.getAttribute('aria-label') === 'Revoke token Home PC')
      ?.click();
    await settle();

    expect(confirm).toHaveBeenCalledOnce();
    expect(revokeToken).toHaveBeenCalledWith('t1');
    expect(toast.success).toHaveBeenCalledWith('Token revoked');
    expect(host.textContent).toContain('No tokens yet');
  });

  it('keeps the token when the owner does not confirm', async () => {
    const revokeToken = vi.fn();
    const {host, settle} = await render(
      {tokens: vi.fn().mockResolvedValue([HOME]), revokeToken},
      {confirm: false}
    );

    buttons(host)
      .find(b => b.getAttribute('aria-label') === 'Revoke token Home PC')
      ?.click();
    await settle();

    expect(revokeToken).not.toHaveBeenCalled();
    expect(host.textContent).toContain('Home PC');
  });

  it('says so when a token could not be revoked', async () => {
    const revokeToken = vi.fn().mockRejectedValue({status: 500});
    const {host, toast, settle} = await render({
      tokens: vi.fn().mockResolvedValue([HOME]),
      revokeToken,
    });

    buttons(host)
      .find(b => b.getAttribute('aria-label') === 'Revoke token Home PC')
      ?.click();
    await settle();

    expect(toast.error).toHaveBeenCalledWith('Could not revoke that token.');
  });

  it('reloads after a token was made in the sheet', async () => {
    const tokens = vi.fn().mockResolvedValueOnce([]).mockResolvedValue([HOME]);
    const {host, open, settle} = await render({tokens}, {sheetSaved: true});

    buttons(host)
      .find(b => b.textContent?.includes('Make a token'))
      ?.click();
    await settle();

    expect(open).toHaveBeenCalledOnce();
    expect(host.textContent).toContain('Home PC');
  });
});
