import { readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export interface Config {
  origin: string;
  token: string;
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

/**
 * BASKET_API_ORIGIN and BASKET_INGEST_TOKEN come from the environment, or from the gitignored `pc-reader/.env.local`
 * (or the file named by BASKET_READER_ENV_FILE). The environment wins. The token is never logged.
 */
export function loadConfig(env: NodeJS.ProcessEnv = process.env, readFile: (path: string) => string = (p) => readFileSync(p, 'utf8')): Config {
  const envFile = env['BASKET_READER_ENV_FILE'] ?? join(READER_DIR, '.env.local');
  let fromFile: Record<string, string> = {};
  try {
    fromFile = parseEnvFile(readFile(envFile));
  } catch {
    // No local file: the environment alone has to carry it.
  }
  const get = (key: string): string | undefined => env[key] || fromFile[key] || undefined;

  const origin = get('BASKET_API_ORIGIN');
  const token = get('BASKET_INGEST_TOKEN');
  if (!origin || !token) {
    throw new Error(`Set BASKET_API_ORIGIN and BASKET_INGEST_TOKEN in the environment or in ${envFile} (see pc-reader/README.md).`);
  }
  let parsedOrigin: URL;
  try {
    parsedOrigin = new URL(origin);
  } catch {
    throw new Error('BASKET_API_ORIGIN is not an address (expected e.g. https://basket.example.com).');
  }
  const local = parsedOrigin.hostname === 'localhost' || parsedOrigin.hostname === '127.0.0.1';
  if (parsedOrigin.protocol !== 'https:' && !local) {
    throw new Error('BASKET_API_ORIGIN must be https (plain http is only accepted for localhost).');
  }
  if (!token.startsWith('bsk_')) {
    throw new Error('BASKET_INGEST_TOKEN does not look like a basket price token (it starts with bsk_).');
  }

  const state = env['XDG_STATE_HOME'] ?? join(homedir(), '.local', 'state');
  return {
    origin: parsedOrigin.origin,
    token,
    urlsFile: resolve(get('BASKET_READER_URLS') ?? join(READER_DIR, 'urls.local.json')),
    profileDir: resolve(get('BASKET_READER_PROFILE') ?? join(state, 'basket-pc-reader', 'profile')),
    channel: get('BASKET_READER_CHANNEL') ?? 'chrome',
    headless: get('BASKET_READER_HEADLESS') === '1',
  };
}
