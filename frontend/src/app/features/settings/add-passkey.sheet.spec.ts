import {TestBed} from '@angular/core/testing';
import {CmnDrawerRef} from '@lifekit-hq/ui';

import {AuthApi} from '../../core/auth/auth.api';
import {PasskeyCancelledError, PasskeyClient} from '../../core/auth/passkey.client';
import {AddPasskeySheetComponent} from './add-passkey.sheet';

async function render(api: Partial<AuthApi>, passkeys: Partial<PasskeyClient>) {
  const close = vi.fn();
  TestBed.configureTestingModule({
    providers: [
      {provide: AuthApi, useValue: api},
      {provide: PasskeyClient, useValue: passkeys},
      {provide: CmnDrawerRef, useValue: {close}},
    ],
  });
  const fixture = TestBed.createComponent(AddPasskeySheetComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  const host = fixture.nativeElement as HTMLElement;
  const submit = async (name: string): Promise<void> => {
    const input = host.querySelector<HTMLInputElement>('input');
    if (input) {
      input.value = name;
      input.dispatchEvent(new Event('input'));
    }
    host.querySelector<HTMLFormElement>('form')?.dispatchEvent(new Event('submit'));
    await fixture.whenStable();
    fixture.detectChanges();
  };
  return {host, close, submit};
}

describe('AddPasskeySheetComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('runs the browser prompt on the server options and sends the credential with the name', async () => {
    const options = {challenge: 'abc'};
    const api = {
      passkeyOptions: vi.fn().mockResolvedValue(options),
      addPasskey: vi.fn().mockResolvedValue({}),
    };
    const create = vi.fn().mockResolvedValue('{"id":"cred"}');
    const {close, submit} = await render(api, {create});

    await submit('  New phone ');

    expect(create).toHaveBeenCalledWith(options);
    expect(api.addPasskey).toHaveBeenCalledWith('{"id":"cred"}', 'New phone');
    expect(close).toHaveBeenCalledWith(true);
  });

  it('stays open with a calm message when the prompt is dismissed', async () => {
    const api = {passkeyOptions: vi.fn().mockResolvedValue({}), addPasskey: vi.fn()};
    const create = vi.fn().mockRejectedValue(new PasskeyCancelledError());
    const {host, close, submit} = await render(api, {create});

    await submit('');

    expect(api.addPasskey).not.toHaveBeenCalled();
    expect(close).not.toHaveBeenCalled();
    expect(host.textContent).toContain('The passkey was not created');
  });

  it('tells the person when the server refuses the credential', async () => {
    const api = {
      passkeyOptions: vi.fn().mockResolvedValue({}),
      addPasskey: vi.fn().mockRejectedValue({status: 400}),
    };
    const {host, close, submit} = await render(api, {create: vi.fn().mockResolvedValue('{}')});

    await submit('Tablet');

    expect(close).not.toHaveBeenCalled();
    expect(host.textContent).toContain('already has a passkey here');
  });
});
