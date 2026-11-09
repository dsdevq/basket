import type { BearerSource } from './fleet.ts';
import { STORES, type WorkItem } from './types.ts';
import { normalise, parseWorkList } from './worklist.ts';

/**
 * The pages basket tracks that the PC reads, from `GET /api/products` (scope basket:catalog-read): every product's
 * linked store page whose shop is read by the PC. Each address goes through the same check as the URL file, so the
 * browser is only ever pointed at a product page on the shop's own host; one that fails it is skipped and counted, not
 * a reason to stop the run (basket checked it when it was linked).
 */
export async function fetchCatalogWorkList(
  origin: string,
  bearer: BearerSource,
  fetchImpl: typeof fetch = fetch,
): Promise<{ items: WorkItem[]; skipped: number }> {
  const token = await bearer();
  const response = await fetchImpl(new URL('/api/products', origin), { headers: { authorization: `Bearer ${token}`, accept: 'application/json' } });
  if (!response.ok) {
    throw new Error(`basket answered the catalog read with HTTP ${response.status}.`);
  }
  const body: unknown = await response.json().catch(() => undefined);
  const products = typeof body === 'object' && body !== null ? (body as { products?: unknown }).products : undefined;
  if (!Array.isArray(products)) {
    throw new Error('basket answered the catalog read without a product list.');
  }
  const entries: unknown[] = [];
  for (const product of products as unknown[]) {
    const stores = typeof product === 'object' && product !== null ? (product as { stores?: unknown }).stores : undefined;
    for (const store of Array.isArray(stores) ? (stores as unknown[]) : []) {
      const { store: slug, readBy, listing } = (typeof store === 'object' && store !== null ? store : {}) as {
        store?: unknown;
        readBy?: unknown;
        listing?: { url?: unknown } | null;
      };
      if (readBy === 'pc' && STORES.some((s) => s === slug) && listing && typeof listing.url === 'string') {
        entries.push({ store: slug, url: listing.url });
      }
    }
  }
  const valid = entries.filter((entry) => typeof normalise(entry) !== 'string');
  return { items: parseWorkList(valid), skipped: entries.length - valid.length };
}

export interface WorkListSources {
  /** The catalog call; absent when the reader has no fleet identity (the `bsk_` token cannot read the catalog). */
  catalog?: () => Promise<{ items: WorkItem[]; skipped: number }>;
  /** The owner's URL file. */
  file: () => Promise<WorkItem[]>;
  log: (line: string) => void;
}

/**
 * The basket catalog when the reader can ask it; the URL file only when the catalog is not configured or the call fails.
 * An empty catalog is an answer, not a failure: nothing is read, and the file is not consulted.
 */
export async function resolveWorkList(sources: WorkListSources): Promise<WorkItem[]> {
  if (!sources.catalog) {
    return sources.file();
  }
  let reason: string;
  try {
    const { items, skipped } = await sources.catalog();
    sources.log(`Page list from basket's catalog: ${items.length} page${items.length === 1 ? '' : 's'}${skipped ? `, ${skipped} skipped as not a shop product page` : ''}.`);
    return items;
  } catch (error) {
    reason = error instanceof Error ? error.message : String(error);
  }
  sources.log(`Could not read basket's catalog (${reason}); falling back to the URL file.`);
  try {
    return await sources.file();
  } catch (error) {
    throw new Error(`No page list: the catalog call failed (${reason}) and ${error instanceof Error ? error.message : String(error)}`);
  }
}
