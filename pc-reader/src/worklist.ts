import { readFile } from 'node:fs/promises';
import type { StoreId, WorkItem } from './types.ts';

const SHAPES: Record<StoreId, { host: string; path: RegExp }> = {
  aldi: { host: 'www.aldi.ie', path: /^\/product\/[a-z0-9-]+-\d{6,}$/i },
  tesco: { host: 'www.tesco.ie', path: /^\/groceries\/en-IE\/products\/\d{4,12}$/i },
};

/**
 * The product pages to read, from a JSON file the owner keeps: `[{"store": "aldi", "url": "https://..."}]`. The same
 * address shape basket accepts when a product is linked, checked here so the browser is only ever pointed at a product
 * page on the shop's own host. Query and fragment are dropped. A bad entry stops the run, with its position.
 */
export async function loadWorkList(path: string): Promise<WorkItem[]> {
  let data: unknown;
  try {
    data = JSON.parse(await readFile(path, 'utf8'));
  } catch (error) {
    throw new Error(`Cannot read the URL file ${path}: ${error instanceof Error ? error.message : String(error)}`);
  }
  return parseWorkList(data);
}

export function parseWorkList(data: unknown): WorkItem[] {
  if (!Array.isArray(data)) {
    throw new Error('The URL file must be a JSON array of {"store", "url"} entries.');
  }
  const seen = new Set<string>();
  const items: WorkItem[] = [];
  data.forEach((entry: unknown, index) => {
    const item = normalise(entry);
    if (typeof item === 'string') {
      throw new Error(`URL file entry ${index + 1}: ${item}`);
    }
    if (!seen.has(item.url)) {
      seen.add(item.url);
      items.push(item);
    }
  });
  return items;
}

function normalise(entry: unknown): WorkItem | string {
  if (typeof entry !== 'object' || entry === null) {
    return 'expected an object with "store" and "url".';
  }
  const { store, url } = entry as Record<string, unknown>;
  if (store !== 'aldi' && store !== 'tesco') {
    return `"store" must be "aldi" or "tesco", got ${JSON.stringify(store)}.`;
  }
  if (typeof url !== 'string') {
    return '"url" must be a string.';
  }
  let parsed: URL;
  try {
    parsed = new URL(url.trim());
  } catch {
    return `"${url}" is not an address.`;
  }
  const shape = SHAPES[store];
  const path = parsed.pathname.replace(/\/+$/, '');
  if (parsed.protocol !== 'https:' || parsed.hostname !== shape.host || parsed.port !== '' || !shape.path.test(path)) {
    return `"${url}" is not a ${store} product page (expected https://${shape.host}${store === 'aldi' ? '/product/<name>-<number>' : '/groceries/en-IE/products/<number>'}).`;
  }
  return { store, url: `https://${shape.host}${path}` };
}
