import {TestBed} from '@angular/core/testing';
import {provideRouter, Router} from '@angular/router';

import {AuthApi} from '../../core/auth/auth.api';
import {PasskeyCancelledError, PasskeyClient} from '../../core/auth/passkey.client';
import {SessionStore} from '../../core/auth/session.store';
import {SignInPage} from './sign-in.page';

function setup(passkeys: Partial<PasskeyClient>, api: Partial<AuthApi>) {
  const refresh = vi.fn().mockResolvedValue(undefined);
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      {provide: PasskeyClient, useValue: passkeys},
      {provide: AuthApi, useValue: api},
      {provide: SessionStore, useValue: {refresh}},
    ],
  });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(SignInPage);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const button = () => host.querySelector('cmn-button button') as HTMLButtonElement;
  return {fixture, host, button, refresh, navigate};
}

describe('SignInPage', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('asks the server for options, the phone for a passkey, then signs in and opens the list', async () => {
    const api = {
      signInOptions: vi.fn().mockResolvedValue({challenge: 'c'}),
      signIn: vi.fn().mockResolvedValue(undefined),
    };
    const passkeys = {get: vi.fn().mockResolvedValue('{"id":"x"}')};
    const {fixture, button, refresh, navigate} = setup(passkeys, api);

    button().click();
    await fixture.whenStable();

    expect(passkeys.get).toHaveBeenCalledWith({challenge: 'c'});
    expect(api.signIn).toHaveBeenCalledWith('{"id":"x"}');
    expect(refresh).toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith('/list');
  });

  it('says so, without alarm, when the person dismisses the prompt', async () => {
    const api = {signInOptions: vi.fn().mockResolvedValue({}), signIn: vi.fn()};
    const passkeys = {get: vi.fn().mockRejectedValue(new PasskeyCancelledError())};
    const {fixture, host, button, navigate} = setup(passkeys, api);

    button().click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host.textContent).toContain('Sign-in was cancelled');
    expect(api.signIn).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
  });

  it('explains an unknown passkey and a rate limit in plain words', async () => {
    const api = {
      signInOptions: vi.fn().mockResolvedValue({}),
      signIn: vi.fn().mockRejectedValue({status: 401}),
    };
    const passkeys = {get: vi.fn().mockResolvedValue('{}')};
    const {fixture, host, button} = setup(passkeys, api);

    button().click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(host.textContent).toContain('not registered here');

    api.signIn.mockRejectedValue({status: 429});
    button().click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(host.textContent).toContain('Too many tries');
  });
});
