import {HttpErrorResponse} from '@angular/common/http';
import {TestBed} from '@angular/core/testing';
import {CmnDialogService, CmnDrawerService, ToastService} from '@lifekit-hq/ui';
import {of} from 'rxjs';

import {LidlPlusApi} from './lidl-plus.api';
import type {LidlPlusStatus} from './lidl-plus.model';
import {LidlPlusSectionComponent} from './lidl-plus.section';

const NONE: LidlPlusStatus = {state: 'not_connected', connectedAt: null, verifiedAt: null};
const CONNECTED: LidlPlusStatus = {
  state: 'connected',
  connectedAt: '2026-11-05T09:00:00Z',
  verifiedAt: '2026-11-09T18:30:00Z',
};
const REFUSED: LidlPlusStatus = {...CONNECTED, state: 'needs_new_token'};

function buttons(host: HTMLElement): HTMLButtonElement[] {
  return Array.from(host.querySelectorAll<HTMLButtonElement>('button'));
}

async function render(
  api: Partial<Record<keyof LidlPlusApi, ReturnType<typeof vi.fn>>>,
  options: {confirm?: boolean; sheetResult?: LidlPlusStatus} = {}
) {
  const toast = {success: vi.fn(), error: vi.fn()};
  const confirm = vi.fn().mockReturnValue(of(options.confirm ?? true));
  const open = vi.fn().mockReturnValue({afterClosed: () => of(options.sheetResult)});
  TestBed.configureTestingModule({
    providers: [
      {provide: LidlPlusApi, useValue: api},
      {provide: ToastService, useValue: toast},
      {provide: CmnDialogService, useValue: {confirm}},
      {provide: CmnDrawerService, useValue: {open}},
    ],
  });
  const fixture = TestBed.createComponent(LidlPlusSectionComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const settle = async (): Promise<void> => {
    await fixture.whenStable();
    fixture.detectChanges();
  };
  const press = async (label: string): Promise<void> => {
    buttons(host)
      .find(b => b.textContent?.includes(label))
      ?.click();
    await settle();
  };
  return {host, toast, confirm, open, press};
}

function labels(host: HTMLElement): string[] {
  return buttons(host).map(b => b.textContent?.trim() ?? '');
}

describe('LidlPlusSectionComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('says it is not connected and offers to connect', async () => {
    const {host} = await render({status: vi.fn().mockResolvedValue(NONE)});

    expect(host.textContent).toContain('Not connected');
    expect(labels(host)).toEqual(['Connect Lidl Plus']);
  });

  it('shows a working connection with the day Lidl last accepted the token', async () => {
    const {host} = await render({status: vi.fn().mockResolvedValue(CONNECTED)});

    expect(host.textContent).toContain('Connected. Lidl accepted the token on 9 Nov 2026');
    expect(labels(host).join('|')).toContain('Paste a new token');
    expect(labels(host).join('|')).toContain('Check now');
    expect(labels(host).join('|')).toContain('Disconnect');
  });

  it('says a new token is needed when Lidl refused the saved one, without a check button', async () => {
    const {host} = await render({status: vi.fn().mockResolvedValue(REFUSED)});

    expect(host.textContent).toContain('Lidl Plus needs a new token');
    expect(host.textContent).toContain('Paste a new one to reconnect');
    expect(labels(host).join('|')).not.toContain('Check now');
  });

  it('says the saved token cannot be read when the key ring was lost', async () => {
    const {host} = await render({
      status: vi.fn().mockResolvedValue({...CONNECTED, state: 'unreadable'}),
    });

    expect(host.textContent).toContain('cannot be read');
    expect(host.textContent).toContain('Paste it again');
  });

  it('offers a retry when the status cannot be loaded', async () => {
    const status = vi.fn().mockRejectedValueOnce(new Error('offline')).mockResolvedValue(NONE);
    const {host, press} = await render({status});

    expect(host.textContent).toContain('Could not load the Lidl Plus connection');
    await press('Try again');

    expect(host.textContent).toContain('Not connected');
  });

  it('opens the paste sheet and shows the connection it returns', async () => {
    const {host, open, toast, press} = await render(
      {status: vi.fn().mockResolvedValue(NONE)},
      {sheetResult: CONNECTED}
    );

    await press('Connect Lidl Plus');

    expect(open).toHaveBeenCalledTimes(1);
    expect(toast.success).toHaveBeenCalledWith('Lidl Plus connected');
    expect(host.textContent).toContain('Connected. Lidl accepted the token');
  });

  it('leaves the status alone when the sheet is closed without a token', async () => {
    const {host, toast, press} = await render({status: vi.fn().mockResolvedValue(NONE)});

    await press('Connect Lidl Plus');

    expect(toast.success).not.toHaveBeenCalled();
    expect(host.textContent).toContain('Not connected');
  });

  it('asks Lidl again on "Check now" and shows the new state', async () => {
    const check = vi.fn().mockResolvedValue(REFUSED);
    const {host, press} = await render({status: vi.fn().mockResolvedValue(CONNECTED), check});

    await press('Check now');

    expect(check).toHaveBeenCalledTimes(1);
    expect(host.textContent).toContain('Lidl Plus needs a new token');
  });

  it('says Lidl could not be reached, and keeps the status, when a check gets a 502', async () => {
    const check = vi.fn().mockRejectedValue(new HttpErrorResponse({status: 502}));
    const {host, toast, press} = await render({
      status: vi.fn().mockResolvedValue(CONNECTED),
      check,
    });

    await press('Check now');

    expect(toast.error).toHaveBeenCalledWith(
      'Could not reach Lidl just now. The token was left as it is.'
    );
    expect(host.textContent).toContain('Connected. Lidl accepted the token');
  });

  it('disconnects only after confirmation', async () => {
    const disconnect = vi.fn().mockResolvedValue(undefined);
    const status = vi.fn().mockResolvedValueOnce(CONNECTED).mockResolvedValue(NONE);
    const declined = await render({status, disconnect}, {confirm: false});
    await declined.press('Disconnect');
    expect(disconnect).not.toHaveBeenCalled();
    TestBed.resetTestingModule();

    const {host, toast, press} = await render(
      {status: vi.fn().mockResolvedValueOnce(CONNECTED).mockResolvedValue(NONE), disconnect},
      {confirm: true}
    );
    await press('Disconnect');

    expect(disconnect).toHaveBeenCalledTimes(1);
    expect(toast.success).toHaveBeenCalledWith('Lidl Plus disconnected');
    expect(host.textContent).toContain('Not connected');
  });
});
