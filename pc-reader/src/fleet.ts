import type { Auth } from './config.ts';

/** The scopes the reader asks for: pushing prices, and reading which pages basket tracks. Nothing else. */
export const FLEET_SCOPES = ['basket:price-ingest', 'basket:catalog-read'] as const;

/** A token is renewed this long before it expires, so a request never goes out with one about to lapse. */
export const REFRESH_MARGIN_MS = 60_000;

/** Gives the bearer token for the next request to basket. */
export type BearerSource = () => Promise<string>;

export interface FleetDeps {
  fetchImpl?: typeof fetch;
  now?: () => number;
}

/**
 * The bearer for basket: the `bsk_` token as it is, or a fleet token fetched by OAuth client credentials and renewed
 * shortly before it expires. The client secret goes only in the token request's Authorization header and is never put in
 * a message; an error carries what the identity service said (`error`, `error_description`) and nothing of the request.
 */
export function bearerSource(auth: Auth, deps: FleetDeps = {}): BearerSource {
  if (auth.kind === 'token') {
    return async () => auth.token;
  }
  const fleet = auth;
  const fetchImpl = deps.fetchImpl ?? fetch;
  const now = deps.now ?? Date.now;
  let cached: { token: string; expiresAt: number } | undefined;
  let inFlight: Promise<string> | undefined;

  async function fetchToken(): Promise<string> {
    const basic = Buffer.from(`${encodeURIComponent(fleet.clientId)}:${encodeURIComponent(fleet.clientSecret)}`).toString('base64');
    let response: Response;
    try {
      response = await fetchImpl(`${fleet.issuer}/token`, {
        method: 'POST',
        headers: { authorization: `Basic ${basic}`, 'content-type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({ grant_type: 'client_credentials', resource: fleet.resource, scope: FLEET_SCOPES.join(' ') }).toString(),
      });
    } catch (error) {
      throw new Error(`Could not reach the identity service at ${fleet.issuer}: ${error instanceof Error ? error.message : String(error)}`);
    }
    const body: unknown = await response.json().catch(() => undefined);
    const fields = typeof body === 'object' && body !== null ? (body as Record<string, unknown>) : {};
    if (!response.ok) {
      const why = [fields['error'], fields['error_description']].filter((v): v is string => typeof v === 'string').join(': ');
      throw new Error(`The identity service refused the fleet token request (HTTP ${response.status}${why ? `, ${why}` : ''}).`);
    }
    const token = fields['access_token'];
    const lifetime = fields['expires_in'];
    if (typeof token !== 'string' || token === '' || typeof lifetime !== 'number' || !(lifetime > 0)) {
      throw new Error('The identity service answered the fleet token request without an access_token and expires_in.');
    }
    cached = { token, expiresAt: now() + lifetime * 1000 };
    return token;
  }

  return async () => {
    if (cached && now() < cached.expiresAt - REFRESH_MARGIN_MS) {
      return cached.token;
    }
    inFlight ??= fetchToken().finally(() => {
      inFlight = undefined;
    });
    return inFlight;
  };
}
