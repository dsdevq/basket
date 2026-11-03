import {TestBed} from '@angular/core/testing';

import {PasskeyCancelledError, PasskeyClient, PasskeyUnsupportedError} from './passkey.client';

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

  it('turns a dismissed prompt into a cancellation, not a fault', async () => {
    stubWebAuthn({create: vi.fn().mockRejectedValue(new DOMException('no', 'NotAllowedError'))});

    await expect(TestBed.inject(PasskeyClient).create({})).rejects.toBeInstanceOf(
      PasskeyCancelledError
    );
  });

  it('lets other failures through unchanged', async () => {
    const failure = new DOMException('bad', 'SecurityError');
    stubWebAuthn({get: vi.fn().mockRejectedValue(failure)});

    await expect(TestBed.inject(PasskeyClient).get({})).rejects.toBe(failure);
  });
});
