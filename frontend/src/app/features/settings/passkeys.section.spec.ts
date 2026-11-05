import {TestBed} from '@angular/core/testing';
import {CmnDialogService, CmnDrawerService, ToastService} from '@lifekit-hq/ui';
import {of} from 'rxjs';

import {AuthApi} from '../../core/auth/auth.api';
import type {Passkey} from '../../core/auth/session.model';
import {PasskeysSectionComponent} from './passkeys.section';

const PHONE: Passkey = {id: 'p1', name: 'Phone', createdAt: '2026-11-03T09:00:00Z', backedUp: true};
const LAPTOP: Passkey = {
  id: 'p2',
  name: 'Laptop',
  createdAt: '2026-11-04T09:00:00Z',
  backedUp: false,
};

interface Mocks {
  readonly api: Partial<Record<keyof AuthApi, ReturnType<typeof vi.fn>>>;
  readonly toast: {success: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn>};
  readonly confirm: ReturnType<typeof vi.fn>;
  readonly open: ReturnType<typeof vi.fn>;
}

async function render(mocks: Partial<Mocks> = {}) {
  const toast = mocks.toast ?? {success: vi.fn(), error: vi.fn()};
  const confirm = mocks.confirm ?? vi.fn().mockReturnValue(of(true));
  const open = mocks.open ?? vi.fn().mockReturnValue({afterClosed: () => of(false)});
  TestBed.configureTestingModule({
    providers: [
      {provide: AuthApi, useValue: mocks.api ?? {}},
      {provide: ToastService, useValue: toast},
      {provide: CmnDialogService, useValue: {confirm}},
      {provide: CmnDrawerService, useValue: {open}},
    ],
  });
  const fixture = TestBed.createComponent(PasskeysSectionComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const settle = async (): Promise<void> => {
    await fixture.whenStable();
    fixture.detectChanges();
  };
  return {fixture, host, toast, confirm, open, settle};
}

function removeButtons(host: HTMLElement): HTMLButtonElement[] {
  return Array.from(host.querySelectorAll<HTMLButtonElement>('button')).filter(button =>
    (button.getAttribute('aria-label') ?? '').startsWith('Remove passkey')
  );
}

describe('PasskeysSectionComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('lists the passkeys with the day each was added', async () => {
    const {host} = await render({api: {passkeys: vi.fn().mockResolvedValue([PHONE, LAPTOP])}});

    const text = host.textContent ?? '';
    expect(text).toContain('Phone');
    expect(text).toContain('Laptop');
    expect(text).toContain('Added 3 Nov 2026');
    expect(removeButtons(host)).toHaveLength(2);
  });

  it('offers no remove button for the only passkey and says why', async () => {
    const {host} = await render({api: {passkeys: vi.fn().mockResolvedValue([PHONE])}});

    expect(removeButtons(host)).toHaveLength(0);
    expect(host.textContent).toContain('This is your only passkey');
    expect(host.textContent).toContain('Add a passkey');
  });

  it('says so when the passkeys cannot be loaded, and loads again on request', async () => {
    const passkeys = vi.fn().mockRejectedValueOnce(new Error('offline')).mockResolvedValue([PHONE]);
    const {host, fixture, settle} = await render({api: {passkeys}});
    expect(host.textContent).toContain('Could not load your passkeys');

    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button')?.click();
    await settle();

    expect(host.textContent).toContain('Phone');
  });

  it('removes a passkey after the person confirms, then reloads the list', async () => {
    const passkeys = vi.fn().mockResolvedValueOnce([PHONE, LAPTOP]).mockResolvedValue([PHONE]);
    const removePasskey = vi.fn().mockResolvedValue(undefined);
    const {host, toast, confirm, settle} = await render({api: {passkeys, removePasskey}});

    removeButtons(host)[1]?.click();
    await settle();

    expect(confirm).toHaveBeenCalledOnce();
    expect(removePasskey).toHaveBeenCalledWith('p2');
    expect(toast.success).toHaveBeenCalledWith('Passkey removed');
    expect(host.textContent).not.toContain('Laptop');
    expect(removeButtons(host)).toHaveLength(0);
  });

  it('keeps the passkey when the person does not confirm', async () => {
    const removePasskey = vi.fn();
    const {host, settle} = await render({
      api: {passkeys: vi.fn().mockResolvedValue([PHONE, LAPTOP]), removePasskey},
      confirm: vi.fn().mockReturnValue(of(false)),
    });

    removeButtons(host)[0]?.click();
    await settle();

    expect(removePasskey).not.toHaveBeenCalled();
  });

  it('explains a refusal from the server and refreshes the list', async () => {
    const passkeys = vi.fn().mockResolvedValue([PHONE, LAPTOP]);
    const removePasskey = vi.fn().mockRejectedValue({status: 409});
    const {host, toast, settle} = await render({api: {passkeys, removePasskey}});

    removeButtons(host)[0]?.click();
    await settle();

    expect(toast.error).toHaveBeenCalledWith('That is your only passkey. Add another one first.');
    expect(passkeys).toHaveBeenCalledTimes(2);
  });

  it('reloads the list after a passkey was added in the sheet', async () => {
    const passkeys = vi.fn().mockResolvedValueOnce([PHONE]).mockResolvedValue([PHONE, LAPTOP]);
    const open = vi.fn().mockReturnValue({afterClosed: () => of(true)});
    const {host, toast, settle} = await render({api: {passkeys}, open});

    const add = Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find(button =>
      (button.textContent ?? '').includes('Add a passkey')
    );
    add?.click();
    await settle();

    expect(open).toHaveBeenCalledOnce();
    expect(toast.success).toHaveBeenCalledWith('Passkey added');
    expect(host.textContent).toContain('Laptop');
  });
});
