import {TestBed} from '@angular/core/testing';

import {PasskeyClient, PasskeyUnsupportedError} from './passkey.client';

describe('PasskeyClient', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function stubWebAuthn(credentials: Partial<CredentialsContainer>): void {
    vi.stubGlobal(
      'PublicKeyCredential',
      class FakePublicKeyCredential {
        public static parseCreationOptionsFromJSON(json: unknown): unknown {
          return {parsed: json};
        }

        public static parseRequestOptionsFromJSON(json: unknown): unknown {
          return {parsed: json};
        }
      }
    );
    vi.stubGlobal('navigator', {credentials});
  }

  it('reports support only when the browser can read the JSON options', () => {
    const client = TestBed.inject(PasskeyClient);
    expect(client.isSupported()).toBe(false);

    stubWebAuthn({});
    expect(client.isSupported()).toBe(true);
  });

  it('refuses to start without support', async () => {
    await expect(TestBed.inject(PasskeyClient).create({})).rejects.toBeInstanceOf(
      PasskeyUnsupportedError
    );
  });

  it('returns the credential as JSON', async () => {
    stubWebAuthn({
      create: vi.fn().mockResolvedValue({toJSON: () => ({id: 'abc'})}),
      get: vi.fn().mockResolvedValue({toJSON: () => ({id: 'def'})}),
    });
    const client = TestBed.inject(PasskeyClient);

    expect(JSON.parse(await client.create({challenge: 'x'}))).toEqual({id: 'abc'});
    expect(JSON.parse(await client.get({challenge: 'y'}))).toEqual({id: 'def'});
  });

  /** A prompt that a person sees and dismisses after `ms`; the browser refuses at once when `ms` is 0. */
  function refusesAfter(ms: number, name: string) {
    return vi.fn().mockImplementation(
      () =>
        new Promise((_, reject) => {
          setTimeout(() => reject(new DOMException('no', name)), ms);
        })
    );
  }

  it('turns a dismissed prompt into a cancellation, not a fault', async () => {
    vi.useFakeTimers();
    try {
      stubWebAuthn({create: refusesAfter(3000, 'NotAllowedError')});
      const attempt = TestBed.inject(PasskeyClient).create({});
      const outcome = expect(attempt).rejects.toMatchObject({
        name: 'PasskeyCancelledError',
        kind: 'cancelled',
        detail: {errorName: 'NotAllowedError', elapsedMs: 3000},
      });
      await vi.advanceTimersByTimeAsync(3000);
      await outcome;
    } finally {
      vi.useRealTimers();
    }
  });

  it('says so when the browser answers create() with no credential at all', async () => {
    stubWebAuthn({create: vi.fn().mockResolvedValue(null)});

    const error = await TestBed.inject(PasskeyClient)
      .create({})
      .catch((e: unknown) => e);

    expect(error).toBeInstanceOf(PasskeyCancelledError);
    expect(error).toMatchObject({detail: {errorName: 'PasskeyClientError', credentialNull: true}});
  });

  it('calls a refusal that comes at once a blocked browser, not a cancellation', async () => {
    stubWebAuthn({create: vi.fn().mockRejectedValue(new DOMException('no', 'NotAllowedError'))});

    const error = await TestBed.inject(PasskeyClient)
      .create({})
      .catch((e: unknown) => e);

    expect(error).toBeInstanceOf(PasskeyUnsupportedError);
    expect(error).toMatchObject({reason: 'blocked', detail: {errorName: 'NotAllowedError'}});
  });

  it.each([
    ['NotSupportedError', 'not-supported'],
    ['SecurityError', 'security'],
  ])('reads %s as a browser that cannot make passkeys (%s)', async (name, reason) => {
    stubWebAuthn({get: vi.fn().mockRejectedValue(new DOMException('bad', name))});

    const error = await TestBed.inject(PasskeyClient)
      .get({})
      .catch((e: unknown) => e);

    expect(error).toBeInstanceOf(PasskeyUnsupportedError);
    expect(error).toMatchObject({reason});
  });

  it('lets other failures through unchanged', async () => {
    const failure = new DOMException('bad', 'InvalidStateError');
    stubWebAuthn({get: vi.fn().mockRejectedValue(failure)});

    await expect(TestBed.inject(PasskeyClient).get({})).rejects.toBe(failure);
  });

  it('says why a page cannot make passkeys before anything is tried', () => {
    const client = TestBed.inject(PasskeyClient);
    expect(client.unsupportedReason()).toBe('no-api');

    stubWebAuthn({});
    expect(client.unsupportedReason()).toBeNull();

    vi.stubGlobal('isSecureContext', false);
    expect(client.unsupportedReason()).toBe('insecure-context');
  });

  it('reports the platform authenticator, or null when the browser cannot say', async () => {
    const client = TestBed.inject(PasskeyClient);
    expect(await client.platformAuthenticatorAvailable()).toBeNull();

    stubWebAuthn({});
    expect(await client.platformAuthenticatorAvailable()).toBeNull();

    vi.stubGlobal(
      'PublicKeyCredential',
      class {
        public static parseCreationOptionsFromJSON(json: unknown): unknown {
          return json;
        }
        public static parseRequestOptionsFromJSON(json: unknown): unknown {
          return json;
        }
        public static isUserVerifyingPlatformAuthenticatorAvailable(): Promise<boolean> {
          return Promise.resolve(false);
        }
      }
    );
    expect(await client.platformAuthenticatorAvailable()).toBe(false);
  });
});
