import type { Config } from './config.ts';
import { bearerSource, FLEET_SCOPES } from './fleet.ts';

export interface CheckStep {
  name: string;
  ok: boolean;
  detail: string;
}

export interface CheckDeps {
  fetchImpl?: typeof fetch;
  now?: () => number;
}

/** The claims of a JWT, read without verifying it (basket verifies; this only shows what the identity service stamped). */
function claimsOf(token: string): Record<string, unknown> | undefined {
  try {
    const payload = JSON.parse(Buffer.from(token.split('.')[1] ?? '', 'base64url').toString('utf8')) as unknown;
    return typeof payload === 'object' && payload !== null ? (payload as Record<string, unknown>) : undefined;
  } catch {
    return undefined;
  }
}

function isLoopback(address: string): boolean {
  const { hostname } = new URL(address);
  return hostname === 'localhost' || hostname === '127.0.0.1';
}

/**
 * `npm run check`: proves, from this machine, that the fleet identity works end to end over the addresses in the config.
 * Stops at the first failing step; the steps before it passed. Prints no token and no secret: only statuses, the issuer
 * and the audience/scope the token carries.
 *
 * 1. the issuer is not a loopback address (a loopback issuer means a hand-held ssh tunnel to the VPS);
 * 2. the issuer's discovery document answers, and the issuer it reports is the configured one, character for character
 *    (the token's `iss` is that value, and basket accepts only its own configured `FleetAgent:Authority`);
 * 3. a client-credentials token is granted, carrying that issuer, the fleet audience and both scopes the reader asks for;
 * 4. basket accepts it (`GET /api/products`, scope basket:catalog-read) at the configured origin.
 */
export async function checkIdentity(config: Config, deps: CheckDeps = {}): Promise<CheckStep[]> {
  const fetchImpl = deps.fetchImpl ?? fetch;
  const steps: CheckStep[] = [];
  const record = (name: string, ok: boolean, detail: string): boolean => {
    steps.push({ name, ok, detail });
    return ok;
  };
  const { auth } = config;
  if (auth.kind !== 'fleet') {
    record('fleet identity', false, 'No fleet identity is configured (BASKET_FLEET_CLIENT_ID, BASKET_FLEET_CLIENT_SECRET, BASKET_FLEET_ISSUER); the bsk_ token does not use an issuer.');
    return steps;
  }

  const tunnel = isLoopback(auth.issuer);
  if (!record('issuer address', !tunnel, tunnel
    ? `${auth.issuer} is this machine only: it works only while a reverse ssh tunnel from the VPS is open. Use the identity service's tailnet https address.`
    : `${auth.issuer} (https, not a loopback address)`)) {
    return steps;
  }

  try {
    const response = await fetchImpl(`${auth.issuer}/.well-known/openid-configuration`, { headers: { accept: 'application/json' } });
    const body: unknown = await response.json().catch(() => undefined);
    const reported = typeof body === 'object' && body !== null ? (body as Record<string, unknown>)['issuer'] : undefined;
    if (!response.ok) {
      record('discovery', false, `HTTP ${response.status} from ${auth.issuer}/.well-known/openid-configuration`);
    } else if (reported !== auth.issuer) {
      record('discovery', false, `the identity service reports its issuer as ${typeof reported === 'string' ? reported : '(nothing)'}, not ${auth.issuer}; set BASKET_FLEET_ISSUER to the reported value.`);
    } else {
      record('discovery', true, `answers, and reports the issuer ${reported}`);
    }
  } catch (error) {
    record('discovery', false, `could not reach ${auth.issuer}: ${error instanceof Error ? error.message : String(error)}`);
  }
  if (!steps.at(-1)?.ok) {
    return steps;
  }

  let token: string;
  try {
    token = await bearerSource(auth, deps)();
  } catch (error) {
    record('token', false, error instanceof Error ? error.message : String(error));
    return steps;
  }
  const claims = claimsOf(token);
  const audience = ([] as unknown[]).concat(claims?.['aud'] ?? []);
  const scopes = typeof claims?.['scope'] === 'string' ? claims['scope'].split(' ') : [];
  const lacking = FLEET_SCOPES.filter((scope) => !scopes.includes(scope));
  const problems = [
    claims?.['iss'] === auth.issuer ? undefined : `iss is ${String(claims?.['iss'])}, not ${auth.issuer}`,
    audience.includes(auth.resource) ? undefined : `audience is ${audience.join(' ') || '(none)'}, not ${auth.resource}`,
    lacking.length === 0 ? undefined : `scope lacks ${lacking.join(' ')}`,
  ].filter((p): p is string => p !== undefined);
  if (!record('token', problems.length === 0, problems.length === 0
    ? `granted; iss ${auth.issuer}, audience ${auth.resource}, scope ${scopes.join(' ')}`
    : `granted, but ${problems.join('; ')}`)) {
    return steps;
  }

  try {
    const response = await fetchImpl(new URL('/api/products', config.origin), { headers: { authorization: `Bearer ${token}`, accept: 'application/json' } });
    record('basket', response.ok, response.ok
      ? `${config.origin} accepts the token (GET /api/products, HTTP ${response.status})`
      : `${config.origin} answered GET /api/products with HTTP ${response.status}${response.status === 401 ? ': basket does not accept this token (is its FLEET_AGENT_AUTHORITY the issuer above?)' : ''}`);
  } catch (error) {
    record('basket', false, `could not reach ${config.origin}: ${error instanceof Error ? error.message : String(error)}`);
  }
  return steps;
}
