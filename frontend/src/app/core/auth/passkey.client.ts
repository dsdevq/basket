import {Injectable} from '@angular/core';

/** What the browser said, kept so a failed attempt can be reported to the server by its class (never by its text). */
export interface PasskeyErrorDetail {
  /** The `DOMException` name (`NotAllowedError`…), or `PasskeyClientError` when the browser said nothing. */
  readonly errorName: string;
  /** How long the browser took to refuse, in ms; null when it was refused before it was asked. */
  readonly elapsedMs: number | null;
  /** The browser resolved the call with no credential instead of rejecting it. */
  readonly credentialNull?: boolean;
}

/** The person dismissed the browser's passkey prompt, or it timed out. Not a fault. */
export class PasskeyCancelledError extends Error {
  public readonly kind = 'cancelled';

  constructor(
    public readonly detail: PasskeyErrorDetail = {errorName: 'NotAllowedError', elapsedMs: null}
  ) {
    super('The passkey prompt was dismissed.');
    this.name = 'PasskeyCancelledError';
  }
}

/**
 * Why this page cannot make a passkey, as the name the server log uses: `no-api` (the browser lacks WebAuthn or
 * its JSON helpers), `insecure-context` (not https), `not-supported` and `security` (the browser said so), and
 * `blocked` (the browser refused the call at once, before any prompt could be shown or dismissed).
 */
export type PasskeyUnsupportedReason =
  'no-api' | 'insecure-context' | 'not-supported' | 'security' | 'blocked';

/**
 * This browser cannot make passkeys here. Trying again cannot help: the person has to open the page in the
 * phone's own browser. An app's built-in browser is the usual case.
 */
export class PasskeyUnsupportedError extends Error {
  constructor(
    public readonly reason: PasskeyUnsupportedReason = 'no-api',
    public readonly detail: PasskeyErrorDetail = {errorName: 'PasskeyClientError', elapsedMs: null}
  ) {
    super('Passkeys are not available in this browser.');
    this.name = 'PasskeyUnsupportedError';
  }

  public get kind(): PasskeyUnsupportedReason {
    return this.reason;
  }
}

/**
 * A refusal that comes sooner than a person could have read the prompt and dismissed it is the browser
 * refusing, not the person. Real cancels take seconds.
 */
export const INSTANT_REFUSAL_MS = 400;

/**
 * The one place that touches `navigator.credentials`. The server speaks the WebAuthn JSON format, which
 * the browser converts with `parse…OptionsFromJSON` and `toJSON()`; nothing here reads the credential.
 */
@Injectable({providedIn: 'root'})
export class PasskeyClient {
  public isSupported(): boolean {
    return (
      typeof window !== 'undefined' &&
      window.isSecureContext !== false &&
      'PublicKeyCredential' in window &&
      typeof PublicKeyCredential.parseCreationOptionsFromJSON === 'function' &&
      typeof PublicKeyCredential.parseRequestOptionsFromJSON === 'function'
    );
  }

  /** Makes a passkey from the server's creation options; returns the credential as JSON. */
  public async create(optionsJson: unknown): Promise<string> {
    this.assertSupported();
    const publicKey = PublicKeyCredential.parseCreationOptionsFromJSON(
      optionsJson as PublicKeyCredentialCreationOptionsJSON
    );
    const credential = await this.run(() => navigator.credentials.create({publicKey}));
    return JSON.stringify((credential as PublicKeyCredential).toJSON());
  }

  /** Asks for a passkey for the server's request options; returns the assertion as JSON. */
  public async get(optionsJson: unknown): Promise<string> {
    this.assertSupported();
    const publicKey = PublicKeyCredential.parseRequestOptionsFromJSON(
      optionsJson as PublicKeyCredentialRequestOptionsJSON
    );
    const credential = await this.run(() => navigator.credentials.get({publicKey}));
    return JSON.stringify((credential as PublicKeyCredential).toJSON());
  }

  /**
   * Whether the device offers a built-in passkey store (Face ID, Touch ID, Windows Hello…); null when the
   * browser cannot say. For the failure report only: security keys and nearby phones work without one.
   */
  public async platformAuthenticatorAvailable(): Promise<boolean | null> {
    try {
      if (
        !this.isSupported() ||
        typeof PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable !== 'function'
      ) {
        return null;
      }
      return await PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable();
    } catch {
      return null;
    }
  }

  /** Why this page cannot make a passkey at all, or null when it can. */
  public unsupportedReason(): PasskeyUnsupportedReason | null {
    if (typeof window !== 'undefined' && window.isSecureContext === false) {
      return 'insecure-context';
    }
    return this.isSupported() ? null : 'no-api';
  }

  private assertSupported(): void {
    const reason = this.unsupportedReason();
    if (reason) {
      throw new PasskeyUnsupportedError(reason);
    }
  }

  private async run(action: () => Promise<Credential | null>): Promise<Credential> {
    const started = Date.now();
    try {
      const credential = await action();
      if (credential === null) {
        throw new PasskeyCancelledError({
          errorName: 'PasskeyClientError',
          elapsedMs: Date.now() - started,
          credentialNull: true,
        });
      }
      return credential;
    } catch (error) {
      if (error instanceof PasskeyCancelledError || !(error instanceof DOMException)) {
        throw error;
      }
      const detail = {errorName: error.name, elapsedMs: Date.now() - started};
      if (error.name === 'NotAllowedError' && detail.elapsedMs < INSTANT_REFUSAL_MS) {
        throw new PasskeyUnsupportedError('blocked', detail);
      }
      if (error.name === 'NotAllowedError' || error.name === 'AbortError') {
        throw new PasskeyCancelledError(detail);
      }
      if (error.name === 'NotSupportedError') {
        throw new PasskeyUnsupportedError('not-supported', detail);
      }
      if (error.name === 'SecurityError') {
        throw new PasskeyUnsupportedError('security', detail);
      }
      throw error;
    }
  }
}
