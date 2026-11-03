import {Injectable} from '@angular/core';

/** The person dismissed the browser's passkey prompt, or it timed out. Not a fault. */
export class PasskeyCancelledError extends Error {
  constructor() {
    super('The passkey prompt was dismissed.');
    this.name = 'PasskeyCancelledError';
  }
}

/** This browser cannot make passkeys (too old, or the page is not on https). */
export class PasskeyUnsupportedError extends Error {
  constructor() {
    super('Passkeys are not available in this browser.');
    this.name = 'PasskeyUnsupportedError';
  }
}

/**
 * The one place that touches `navigator.credentials`. The server speaks the WebAuthn JSON format, which
 * the browser converts with `parse…OptionsFromJSON` and `toJSON()`; nothing here reads the credential.
 */
@Injectable({providedIn: 'root'})
export class PasskeyClient {
  public isSupported(): boolean {
    return (
      typeof window !== 'undefined' &&
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

  private assertSupported(): void {
    if (!this.isSupported()) {
      throw new PasskeyUnsupportedError();
    }
  }

  private async run(action: () => Promise<Credential | null>): Promise<Credential> {
    try {
      const credential = await action();
      if (credential === null) {
        throw new PasskeyCancelledError();
      }
      return credential;
    } catch (error) {
      if (
        error instanceof DOMException &&
        (error.name === 'NotAllowedError' || error.name === 'AbortError')
      ) {
        throw new PasskeyCancelledError();
      }
      throw error;
    }
  }
}
