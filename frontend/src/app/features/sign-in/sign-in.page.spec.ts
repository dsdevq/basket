import {TestBed} from '@angular/core/testing';
import {ActivatedRoute, convertToParamMap, provideRouter, Router} from '@angular/router';

import {AuthApi} from '../../core/auth/auth.api';
import {OidcSignIn} from '../../core/auth/oidc-sign-in';
import {PasskeyCancelledError, PasskeyClient} from '../../core/auth/passkey.client';
import {SessionStore} from '../../core/auth/session.store';
import {SignInPage} from './sign-in.page';

async function setup(
  passkeys: Partial<PasskeyClient>,
  api: Partial<AuthApi>,
  options: {oidc?: boolean; query?: Record<string, string>} = {}
) {
  const refresh = vi.fn().mockResolvedValue(undefined);
  const oidc = {isAvailable: vi.fn().mockResolvedValue(options.oidc ?? false), start: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      {provide: PasskeyClient, useValue: passkeys},
      {provide: AuthApi, useValue: api},
      {provide: SessionStore, useValue: {refresh}},
      {provide: OidcSignIn, useValue: oidc},
      {
        provide: ActivatedRoute,
        useValue: {snapshot: {queryParamMap: convertToParamMap(options.query ?? {})}},
      },
    ],
  });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(SignInPage);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const button = (name: RegExp) =>
    [...host.querySelectorAll('cmn-button button')].find(b => name.test(b.textContent ?? '')) as
      HTMLButtonElement | undefined;
  return {fixture, host, button, refresh, navigate, oidc};
}

describe('SignInPage', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('forwards to the lifekit sign-in on its own when that is on', async () => {
    const {oidc, host} = await setup({}, {}, {oidc: true});

    expect(oidc.start).toHaveBeenCalledOnce();
    expect(host.querySelector('cmn-app-mark')).not.toBeNull();
  });

  it('stays put, with the message, after signing out', async () => {
    const {oidc, host, button} = await setup({}, {}, {oidc: true, query: {info: 'signed_out'}});

    expect(oidc.start).not.toHaveBeenCalled();
    expect(host.textContent).toContain('You have signed out');
    button(/lifekit account/)?.click();
    expect(oidc.start).toHaveBeenCalledOnce();
  });

  it('stays put, with the message, after a failed attempt', async () => {
    const {oidc, host} = await setup({}, {}, {oidc: true, query: {error: 'OIDC_FAILED'}});

    expect(oidc.start).not.toHaveBeenCalled();
    expect(host.textContent).toContain('did not finish');
  });

  it('keeps the passkey button next to the lifekit one during the transition', async () => {
    const {button} = await setup({}, {}, {oidc: true, query: {info: 'signed_out'}});

    expect(button(/passkey/)).toBeDefined();
    expect(button(/lifekit account/)).toBeDefined();
  });

  it('is passkey-only, with no forward, when the lifekit sign-in is off', async () => {
    const {oidc, button} = await setup({}, {}, {oidc: false});

    expect(oidc.start).not.toHaveBeenCalled();
    expect(button(/passkey/)).toBeDefined();
    expect(button(/lifekit account/)).toBeUndefined();
  });

  it('asks the server for options, the phone for a passkey, then signs in and opens the list', async () => {
    const api = {
      signInOptions: vi.fn().mockResolvedValue({challenge: 'c'}),
      signIn: vi.fn().mockResolvedValue(undefined),
    };
    const passkeys = {get: vi.fn().mockResolvedValue('{"id":"x"}')};
    const {fixture, button, refresh, navigate} = await setup(passkeys, api);

    button(/passkey/)?.click();
    await fixture.whenStable();

    expect(passkeys.get).toHaveBeenCalledWith({challenge: 'c'});
    expect(api.signIn).toHaveBeenCalledWith('{"id":"x"}');
    expect(refresh).toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith('/list');
  });

  it('says so, without alarm, when the person dismisses the prompt', async () => {
    const api = {signInOptions: vi.fn().mockResolvedValue({}), signIn: vi.fn()};
    const passkeys = {get: vi.fn().mockRejectedValue(new PasskeyCancelledError())};
    const {fixture, host, button, navigate} = await setup(passkeys, api);

    button(/passkey/)?.click();
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
    const {fixture, host, button} = await setup(passkeys, api);

    button(/passkey/)?.click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(host.textContent).toContain('not registered here');

    api.signIn.mockRejectedValue({status: 429});
    button(/passkey/)?.click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(host.textContent).toContain('Too many tries');
  });
});
