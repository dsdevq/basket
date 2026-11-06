import {Clipboard} from '@angular/cdk/clipboard';
import {TestBed} from '@angular/core/testing';
import {provideRouter, Router} from '@angular/router';

import {AuthApi} from '../../core/auth/auth.api';
import {
  PasskeyCancelledError,
  PasskeyClient,
  PasskeyUnsupportedError,
} from '../../core/auth/passkey.client';
import {SessionStore} from '../../core/auth/session.store';
import {JoinPage} from './join.page';

const USER = {id: 'u2', displayName: 'Anna', role: 'member' as const};

function setup(hash: string, api: Partial<AuthApi>, passkeys: Partial<PasskeyClient> = {}) {
  window.history.replaceState(null, '', `/join${hash}`);
  const setUser = vi.fn();
  const copy = vi.fn().mockReturnValue(true);
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      {
        provide: AuthApi,
        useValue: {reportPasskeyFailure: vi.fn().mockResolvedValue(undefined), ...api},
      },
      {
        provide: PasskeyClient,
        useValue: {
          unsupportedReason: () => null,
          platformAuthenticatorAvailable: () => Promise.resolve(true),
          ...passkeys,
        },
      },
      {provide: Clipboard, useValue: {copy}},
      {provide: SessionStore, useValue: {setUser}},
    ],
  });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(JoinPage);
  fixture.detectChanges();
  return {fixture, host: fixture.nativeElement as HTMLElement, setUser, navigate, copy};
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

  it('says the link is incomplete when the part after the # sign is missing', async () => {
    const api = {checkInvite: vi.fn()};
    const {fixture, host} = setup('', api);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host.textContent).toContain('This link is incomplete');
    expect(host.textContent).toContain('after the # sign');
    expect(api.checkInvite).not.toHaveBeenCalled();
  });

  it('says the link was used or expired when the server does not know the token', async () => {
    const {fixture, host} = setup('#dead', {checkInvite: vi.fn().mockRejectedValue({status: 404})});
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host.textContent).toContain('used or has expired');
    expect(host.textContent).not.toContain('Cannot reach basket');
  });

  it.each([
    [{status: 0}, 'no answer from the server'],
    [{status: 502}, 'HTTP 502'],
    [{status: 429}, 'HTTP 429'],
  ])('says basket cannot be reached when the check fails with %o', async (error, detail) => {
    const {fixture, host} = setup('#tok', {checkInvite: vi.fn().mockRejectedValue(error)});
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host.textContent).toContain('Cannot reach basket');
    expect(host.textContent).toContain(detail);
    expect(host.textContent).toContain('Try again');
    expect(host.textContent).not.toContain('used or has expired');
    expect(host.textContent).not.toContain('tok');
  });

  it('checks the same link again on Try again, without the address bar', async () => {
    const checkInvite = vi
      .fn()
      .mockRejectedValueOnce({status: 0})
      .mockResolvedValue({role: 'member', label: null});
    const {fixture, host} = setup('#tok', {checkInvite});
    await fixture.whenStable();
    fixture.detectChanges();

    const retry = [...host.querySelectorAll('button')].find(b =>
      b.textContent?.includes('Try again')
    );
    retry?.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(checkInvite).toHaveBeenCalledTimes(2);
    expect(checkInvite).toHaveBeenLastCalledWith('tok');
    expect(window.location.hash).toBe('');
    expect(host.textContent).toContain('Create passkey');
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
    expect(passkeys.create).toHaveBeenCalledWith({challenge: 'c'}, {classifyInstantRefusal: true});
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

    expect(host.textContent).toContain('used or has expired');
  });

  describe('when the passkey cannot be made', () => {
    async function submitWith(
      create: () => Promise<string>,
      api: Partial<AuthApi> = {},
      passkeys: Partial<PasskeyClient> = {}
    ) {
      const all = {
        checkInvite: vi.fn().mockResolvedValue({role: 'member', label: null}),
        registerOptions: vi.fn().mockResolvedValue({challenge: 'c'}),
        reportPasskeyFailure: vi.fn().mockResolvedValue(undefined),
        ...api,
      };
      const made = setup('#tok', all, {create, ...passkeys});
      await made.fixture.whenStable();
      made.fixture.detectChanges();
      const input = made.host.querySelector('input') as HTMLInputElement;
      input.value = 'Anna';
      input.dispatchEvent(new Event('input'));
      (made.host.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
      await made.fixture.whenStable();
      made.fixture.detectChanges();
      return {...made, api: all};
    }

    it('tells a real cancel to try again, and logs it as a cancel', async () => {
      const {host, api} = await submitWith(() =>
        Promise.reject(new PasskeyCancelledError({errorName: 'NotAllowedError', elapsedMs: 3000}))
      );

      expect(host.textContent).toContain('The passkey was not created. Try again');
      expect(host.textContent).toContain('Create passkey');
      expect(host.textContent).toContain('Open this page in Safari or Chrome');
      expect(api.reportPasskeyFailure).toHaveBeenCalledWith({
        kind: 'cancelled',
        errorName: 'NotAllowedError',
        elapsedMs: 3000,
        elapsedBucket: 'later',
        credentialNull: false,
        stage: 'create',
        httpStatus: null,
        platformAuthenticator: true,
      });
    });

    it('gives a dismissed attempt the same copy-link control as the unsupported page', async () => {
      const {host, copy} = await submitWith(() =>
        Promise.reject(new PasskeyCancelledError({errorName: 'NotAllowedError', elapsedMs: 3000}))
      );

      const button = [...host.querySelectorAll('button')].find(b =>
        b.textContent?.includes('Copy link')
      );
      button?.click();
      expect(copy).toHaveBeenCalledWith(`${window.location.origin}/join#tok`);
    });

    it('reports a webview that resolves create() with nothing as a null credential', async () => {
      const {host, api} = await submitWith(() =>
        Promise.reject(
          new PasskeyCancelledError({
            errorName: 'PasskeyClientError',
            elapsedMs: 900,
            credentialNull: true,
          })
        )
      );

      expect(host.textContent).toContain('Open this page in Safari or Chrome');
      expect(api.reportPasskeyFailure).toHaveBeenCalledWith(
        expect.objectContaining({kind: 'cancelled', elapsedBucket: 'later', credentialNull: true})
      );
    });

    it('does not show the open-in-browser hint for a server fault', async () => {
      const {host} = await submitWith(() => Promise.resolve('{}'), {
        register: vi.fn().mockRejectedValue({status: 400}),
      });

      expect(host.textContent).not.toContain('Open this page in Safari or Chrome');
      expect(host.textContent).not.toContain('Copy link');
    });

    it('sends a browser that refuses at once to the phone browser, with a copy-link control', async () => {
      const {host, copy, api} = await submitWith(() =>
        Promise.reject(
          new PasskeyUnsupportedError('blocked', {errorName: 'NotAllowedError', elapsedMs: 12})
        )
      );

      expect(host.textContent).toContain('Open this link in your browser');
      expect(host.textContent).toContain('Safari or Chrome');
      expect(host.textContent).not.toContain('The passkey was not created');
      expect(host.textContent).not.toContain('Create passkey');
      expect(api.reportPasskeyFailure).toHaveBeenCalledWith(
        expect.objectContaining({
          kind: 'blocked',
          errorName: 'NotAllowedError',
          stage: 'create',
          elapsedBucket: 'instant',
          credentialNull: false,
        })
      );

      const button = [...host.querySelectorAll('button')].find(b =>
        b.textContent?.includes('Copy link')
      );
      button?.click();
      expect(copy).toHaveBeenCalledWith(`${window.location.origin}/join#tok`);
    });

    it('shows the instruction instead of the button when the browser has no passkeys at all', async () => {
      const api = {
        checkInvite: vi.fn().mockResolvedValue({role: 'member', label: null}),
        reportPasskeyFailure: vi.fn().mockResolvedValue(undefined),
      };
      const {fixture, host} = setup('#tok', api, {unsupportedReason: () => 'no-api'});
      await fixture.whenStable();
      fixture.detectChanges();

      expect(host.textContent).toContain('Open this link in your browser');
      expect(host.textContent).not.toContain('Create passkey');
      expect(api.reportPasskeyFailure).toHaveBeenCalledWith(
        expect.objectContaining({kind: 'no-api', stage: 'page'})
      );
    });

    it('logs a server fault by its status, and keeps the generic notice for it', async () => {
      const {host, api} = await submitWith(() => Promise.resolve('{}'), {
        register: vi.fn().mockRejectedValue({status: 400}),
      });

      expect(host.textContent).toContain('Could not create the passkey');
      expect(api.reportPasskeyFailure).toHaveBeenCalledWith(
        expect.objectContaining({kind: 'http', httpStatus: 400, stage: 'register'})
      );
    });

    it('does not let a failed report get in the way', async () => {
      const {host} = await submitWith(
        () => Promise.reject(new PasskeyUnsupportedError('blocked')),
        {reportPasskeyFailure: vi.fn().mockRejectedValue({status: 0})}
      );

      expect(host.textContent).toContain('Open this link in your browser');
    });
  });
});
