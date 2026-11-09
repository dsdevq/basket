import { readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

/** How the reader proves itself to basket: the owner-minted `bsk_` token, or the fleet identity by client credentials. */
export type Auth =
  | { kind: 'token'; token: string }
  | { kind: 'fleet'; issuer: string; clientId: string; clientSecret: string; resource: string };

/** The fleet API resource basket accepts tokens for by default (`FleetAgent:Audience`). */
export const DEFAULT_FLEET_RESOURCE = 'https://fleet.lifekit.internal/api';

export interface Config {
  origin: string;
  auth: Auth;
  urlsFile: string;
  profileDir: string;
  channel: string;
  headless: boolean;
}

const HERE = dirname(fileURLToPath(import.meta.url));
export const READER_DIR = resolve(HERE, '..');

/** KEY=VALUE lines (comments with #, optional quotes). A missing file is just an empty set. */
export function parseEnvFile(text: string): Record<string, string> {
  const values: Record<string, string> = {};
  for (const line of text.split(/\r?\n/)) {
    const match = /^\s*(?:export\s+)?([A-Z0-9_]+)\s*=\s*(.*?)\s*$/.exec(line);
    if (match?.[1] && !line.trimStart().startsWith('#')) {
      values[match[1]] = (match[2] ?? '').replace(/^(['"])(.*)\1$/, '$2');
    }
  }
  return values;
}

type Settings = (key: string) => string | undefined;

/** One setting: the environment first, then the gitignored env file (`BASKET_READER_ENV_FILE`, default `.env.local`). */
function settings(env: NodeJS.ProcessEnv, readFile: (path: string) => string): { get: Settings; envFile: string } {
  const envFile = env['BASKET_READER_ENV_FILE'] ?? join(READER_DIR, '.env.local');
  let fromFile: Record<string, string> = {};
  try {
    fromFile = parseEnvFile(readFile(envFile));
  } catch {
    // No local file: the environment alone has to carry it.
  }
  return { get: (key) => env[key] || fromFile[key] || undefined, envFile };
}

function profileDirFrom(get: Settings, env: NodeJS.ProcessEnv): string {
  const state = env['XDG_STATE_HOME'] ?? join(homedir(), '.local', 'state');
  return resolve(get('BASKET_READER_PROFILE') ?? join(state, 'basket-pc-reader', 'profile'));
}

/** The reader's Chrome profile directory and browser, resolved exactly as the reader resolves them. Needs no token. */
export function loadProfileSettings(
  env: NodeJS.ProcessEnv = process.env,
  readFile: (path: string) => string = (p) => readFileSync(p, 'utf8'),
): { profileDir: string; channel: string } {
  const { get } = settings(env, readFile);
  return { profileDir: profileDirFrom(get, env), channel: get('BASKET_READER_CHANNEL') ?? 'chrome' };
}

/** An https address, or http for this machine only: the same rule for basket's origin and the identity service's issuer. */
function checkedAddress(name: string, value: string, example: string): URL {
  let parsed: URL;
  try {
    parsed = new URL(value);
  } catch {
    throw new Error(`${name} is not an address (expected e.g. ${example}).`);
  }
  const local = parsed.hostname === 'localhost' || parsed.hostname === '127.0.0.1';
  if (parsed.protocol !== 'https:' && !(local && parsed.protocol === 'http:')) {
    throw new Error(`${name} must be https (plain http is only accepted for localhost).`);
  }
  return parsed;
}

function authFrom(get: Settings, envFile: string): Auth {
  const clientId = get('BASKET_FLEET_CLIENT_ID');
  const clientSecret = get('BASKET_FLEET_CLIENT_SECRET');
  const issuer = get('BASKET_FLEET_ISSUER');
  if (clientId || clientSecret || issuer) {
    const missing = [
      ['BASKET_FLEET_CLIENT_ID', clientId],
      ['BASKET_FLEET_CLIENT_SECRET', clientSecret],
      ['BASKET_FLEET_ISSUER', issuer],
    ].filter(([, value]) => !value);
    if (!clientId || !clientSecret || !issuer) {
      throw new Error(`The fleet identity needs ${missing.map(([name]) => name).join(', ')} too (environment or ${envFile}; see pc-reader/README.md).`);
    }
    const parsed = checkedAddress('BASKET_FLEET_ISSUER', issuer, 'https://identity.example.com/oidc');
    return {
      kind: 'fleet',
      issuer: parsed.href.replace(/\/+$/, ''),
      clientId,
      clientSecret,
      resource: get('BASKET_FLEET_RESOURCE') ?? DEFAULT_FLEET_RESOURCE,
    };
  }
  const token = get('BASKET_INGEST_TOKEN');
  if (!token) {
    throw new Error(
      `Set BASKET_FLEET_CLIENT_ID, BASKET_FLEET_CLIENT_SECRET and BASKET_FLEET_ISSUER (the fleet identity), or BASKET_INGEST_TOKEN, in the environment or in ${envFile} (see pc-reader/README.md).`,
    );
  }
  if (!token.startsWith('bsk_')) {
    throw new Error('BASKET_INGEST_TOKEN does not look like a basket price token (it starts with bsk_).');
  }
  return { kind: 'token', token };
}

/**
 * BASKET_API_ORIGIN and the credentials come from the environment, or from the gitignored `pc-reader/.env.local` (or the
 * file named by BASKET_READER_ENV_FILE). The environment wins. The credentials are the fleet identity
 * (BASKET_FLEET_CLIENT_ID, BASKET_FLEET_CLIENT_SECRET, BASKET_FLEET_ISSUER) when any of those is set, otherwise the
 * `bsk_` token (BASKET_INGEST_TOKEN). Neither the token nor the secret is ever logged.
 */
export function loadConfig(env: NodeJS.ProcessEnv = process.env, readFile: (path: string) => string = (p) => readFileSync(p, 'utf8')): Config {
  const { get, envFile } = settings(env, readFile);

  const origin = get('BASKET_API_ORIGIN');
  if (!origin) {
    throw new Error(`Set BASKET_API_ORIGIN in the environment or in ${envFile} (see pc-reader/README.md).`);
  }
  const parsedOrigin = checkedAddress('BASKET_API_ORIGIN', origin, 'https://basket.example.com');
  const auth = authFrom(get, envFile);

  return {
    origin: parsedOrigin.origin,
    auth,
    urlsFile: resolve(get('BASKET_READER_URLS') ?? join(READER_DIR, 'urls.local.json')),
    profileDir: profileDirFrom(get, env),
    channel: get('BASKET_READER_CHANNEL') ?? 'chrome',
    headless: get('BASKET_READER_HEADLESS') === '1',
  };
}
