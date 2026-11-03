import {TestBed} from '@angular/core/testing';
import {provideRouter, Router} from '@angular/router';

import {AuthApi} from '../../core/auth/auth.api';
import {PasskeyClient} from '../../core/auth/passkey.client';
import {SessionStore} from '../../core/auth/session.store';
import {JoinPage} from './join.page';

const USER = {id: 'u2', displayName: 'Anna', role: 'member' as const};

function setup(hash: string, api: Partial<AuthApi>, passkeys: Partial<PasskeyClient> = {}) {
  window.history.replaceState(null, '', `/join${hash}`);
  const setUser = vi.fn();
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      {provide: AuthApi, useValue: api},
      {provide: PasskeyClient, useValue: passkeys},
      {provide: SessionStore, useValue: {setUser}},
    ],
  });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(JoinPage);
  fixture.detectChanges();
  return {fixture, host: fixture.nativeElement as HTMLElement, setUser, navigate};
}

describe('JoinPage', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('reads the token from the fragment, then wipes it from the address bar', async () => {
    const api = {checkInvite: vi.fn().mockResolvedValue({role: 'member', label: null})};
    const {fixture, host} = setup('#abc123', api);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(api.checkInvite).toHaveBeenCalledWith('abc123');
    expect(window.location.hash).toBe('');
    expect(host.textContent).toContain('Create passkey');
  });

  it('says the link is no use when there is no token or the server refuses it', async () => {
    const none = setup('', {checkInvite: vi.fn()});
    await none.fixture.whenStable();
    none.fixture.detectChanges();
    expect(none.host.textContent).toContain('not valid any more');
    TestBed.resetTestingModule();

    const refused = setup('#dead', {checkInvite: vi.fn().mockRejectedValue({status: 404})});
    await refused.fixture.whenStable();
    refused.fixture.detectChanges();
    expect(refused.host.textContent).toContain('not valid any more');
  });

  it('makes the passkey with the name given and opens the list', async () => {
    const api = {
      checkInvite: vi.fn().mockResolvedValue({role: 'member', label: null}),
      registerOptions: vi.fn().mockResolvedValue({challenge: 'c'}),
      register: vi.fn().mockResolvedValue({authenticated: true, user: USER}),
    };
    const passkeys = {create: vi.fn().mockResolvedValue('{"id":"p"}')};
    const {fixture, host, setUser, navigate} = setup('#tok', api, passkeys);
    await fixture.whenStable();
    fixture.detectChanges();

    const input = host.querySelector('input') as HTMLInputElement;
    input.value = '  Anna ';
    input.dispatchEvent(new Event('input'));
    (host.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await fixture.whenStable();

    expect(api.registerOptions).toHaveBeenCalledWith('tok', 'Anna');
    expect(passkeys.create).toHaveBeenCalledWith({challenge: 'c'});
    expect(api.register).toHaveBeenCalledWith('tok', '{"id":"p"}');
    expect(setUser).toHaveBeenCalledWith(USER);
    expect(navigate).toHaveBeenCalledWith('/list');
  });

  it('asks for a name before it bothers the phone', async () => {
    const api = {
      checkInvite: vi.fn().mockResolvedValue({role: 'member', label: null}),
      registerOptions: vi.fn(),
    };
    const {fixture, host} = setup('#tok', api);
    await fixture.whenStable();
    fixture.detectChanges();

    (host.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(api.registerOptions).not.toHaveBeenCalled();
    expect(host.textContent).toContain('Enter the name');
  });

  it('turns to the dead-link page when the link was used in the meantime', async () => {
    const api = {
      checkInvite: vi.fn().mockResolvedValue({role: 'member', label: null}),
      registerOptions: vi.fn().mockRejectedValue({status: 404}),
    };
    const {fixture, host} = setup('#tok', api);
    await fixture.whenStable();
    fixture.detectChanges();
    const input = host.querySelector('input') as HTMLInputElement;
    input.value = 'Anna';
    input.dispatchEvent(new Event('input'));

    (host.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host.textContent).toContain('not valid any more');
  });
});
