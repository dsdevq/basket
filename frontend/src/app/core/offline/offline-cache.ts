/**
 * The last thing the server said, kept on this phone so the in-shop screen still opens with no signal.
 * Only the signed-in person's own session and list are stored; `forget` removes both on sign-out or when the
 * server says the session is over. Storage can be missing or full (private windows), so every call is best-effort.
 */
const SESSION_KEY = 'basket.offline.session';
const LIST_KEY = 'basket.offline.list';

export interface CachedList<T> {
  readonly userId: string;
  readonly savedAt: string;
  readonly items: readonly T[];
}

function read<T>(key: string): T | null {
  try {
    const raw = localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as T) : null;
  } catch {
    return null;
  }
}

function write(key: string, value: unknown): void {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Storage unavailable or full: the app works online as before.
  }
}

export const offlineCache = {
  readSession: <T>(): T | null => read<T>(SESSION_KEY),
  writeSession: (user: unknown): void => write(SESSION_KEY, user),
  readList: <T>(userId: string): CachedList<T> | null => {
    const cached = read<CachedList<T>>(LIST_KEY);
    return cached?.userId === userId && Array.isArray(cached.items) ? cached : null;
  },
  writeList: <T>(userId: string, items: readonly T[]): void =>
    write(LIST_KEY, {userId, savedAt: new Date().toISOString(), items}),
  forget: (): void => {
    try {
      localStorage.removeItem(SESSION_KEY);
      localStorage.removeItem(LIST_KEY);
    } catch {
      // Nothing to clear.
    }
  },
};
