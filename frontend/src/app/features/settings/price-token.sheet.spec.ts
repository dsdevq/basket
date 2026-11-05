import {Clipboard} from '@angular/cdk/clipboard';
import {TestBed} from '@angular/core/testing';
import {CmnDrawerRef, ToastService} from '@lifekit-hq/ui';

import {PriceRelayApi} from './price-relay.api';
import {PriceTokenSheetComponent} from './price-token.sheet';

const MADE = {
  id: 't1',
  label: 'Home PC',
  createdAt: '2026-11-05T09:00:00Z',
  token: 'bsk_abcDEF123',
};

async function render(api: Partial<PriceRelayApi>, copy = vi.fn().mockReturnValue(true)) {
  const close = vi.fn();
  const toast = {success: vi.fn(), error: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      {provide: PriceRelayApi, useValue: api},
      {provide: Clipboard, useValue: {copy}},
      {provide: ToastService, useValue: toast},
      {provide: CmnDrawerRef, useValue: {close}},
    ],
  });
  const fixture = TestBed.createComponent(PriceTokenSheetComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  const host = fixture.nativeElement as HTMLElement;
  const settle = async (): Promise<void> => {
    await fixture.whenStable();
    fixture.detectChanges();
  };
  const submit = async (label: string): Promise<void> => {
    const input = host.querySelector<HTMLInputElement>('input');
    if (input) {
      input.value = label;
      input.dispatchEvent(new Event('input'));
    }
    host.querySelector<HTMLFormElement>('form')?.dispatchEvent(new Event('submit'));
    await settle();
  };
  return {host, close, toast, copy, submit, settle};
}

describe('PriceTokenSheetComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('makes a token for the typed label and shows it, once, with what it can do', async () => {
    const createToken = vi.fn().mockResolvedValue(MADE);
    const {host, submit} = await render({createToken});

    await submit('  Home PC ');

    expect(createToken).toHaveBeenCalledWith('Home PC');
    expect(host.querySelector('[data-testid="token"]')?.textContent?.trim()).toBe('bsk_abcDEF123');
    expect(host.textContent).toContain('will not show it again');
    expect(host.textContent).toContain('can read nothing');
  });

  it('copies the token and closes with true when done', async () => {
    const {host, copy, toast, close, submit, settle} = await render({
      createToken: vi.fn().mockResolvedValue(MADE),
    });
    await submit('Home PC');

    const buttons = Array.from(host.querySelectorAll<HTMLButtonElement>('button'));
    buttons.find(b => b.textContent?.includes('Copy token'))?.click();
    buttons.find(b => b.textContent?.includes('Done'))?.click();
    await settle();

    expect(copy).toHaveBeenCalledWith('bsk_abcDEF123');
    expect(toast.success).toHaveBeenCalledWith('Token copied');
    expect(close).toHaveBeenCalledWith(true);
  });

  it('says to copy by hand when the clipboard is refused', async () => {
    const {host, toast, submit, settle} = await render(
      {createToken: vi.fn().mockResolvedValue(MADE)},
      vi.fn().mockReturnValue(false)
    );
    await submit('Home PC');

    Array.from(host.querySelectorAll<HTMLButtonElement>('button'))
      .find(b => b.textContent?.includes('Copy token'))
      ?.click();
    await settle();

    expect(toast.error).toHaveBeenCalled();
  });

  it('shows a plain message when making the token fails', async () => {
    const {host, submit} = await render({createToken: vi.fn().mockRejectedValue({status: 500})});

    await submit('Another');

    expect(host.textContent).toContain('Could not make the token');
  });
});
