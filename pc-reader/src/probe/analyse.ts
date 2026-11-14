/** What the probe makes of the pages it fetched. Pure: text and parsed JSON in, plain facts out. */

export interface RobotsFacts {
  disallow: string[];
  sitemaps: string[];
  crawlDelay?: number;
}

const MAX_DISALLOW = 80;

export function readRobots(text: string): RobotsFacts {
  const facts: RobotsFacts = { disallow: [], sitemaps: [] };
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    const [name, ...rest] = line.split(':');
    const value = rest.join(':').trim();
    switch (name?.toLowerCase()) {
      case 'disallow':
        if (value && facts.disallow.length < MAX_DISALLOW) {
          facts.disallow.push(value);
        }
        break;
      case 'sitemap':
        if (value) {
          facts.sitemaps.push(value);
        }
        break;
      case 'crawl-delay':
        if (Number.isFinite(Number(value))) {
          facts.crawlDelay = Number(value);
        }
        break;
    }
  }
  return facts;
}

export interface SitemapFacts {
  kind: 'index' | 'urlset' | 'unknown';
  entries: number;
  sample: string[];
}

export function readSitemap(text: string): SitemapFacts {
  const locs = [...text.matchAll(/<loc>\s*([^<\s]+)\s*<\/loc>/gi)].map((m) => m[1] ?? '');
  const kind = /<sitemapindex/i.test(text) ? 'index' : /<urlset/i.test(text) ? 'urlset' : 'unknown';
  return { kind, entries: locs.length, sample: locs.slice(0, 5) };
}

/** Same-origin links on a page, as absolute addresses without fragments, in page order, each once. */
export function sameOriginLinks(html: string, pageUrl: string): string[] {
  const base = new URL(pageUrl);
  const seen = new Set<string>();
  for (const match of html.matchAll(/<a\b[^>]*?\bhref\s*=\s*(?:"([^"]*)"|'([^']*)')/gi)) {
    const href = (match[1] ?? match[2] ?? '').trim();
    if (!href || href.startsWith('#') || /^(mailto|tel|javascript):/i.test(href)) {
      continue;
    }
    try {
      const url = new URL(href, base);
      if (url.origin === base.origin) {
        url.hash = '';
        seen.add(url.href);
      }
    } catch {
      // Not an address.
    }
  }
  return [...seen];
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Every JSON document a page carries in its own HTML: `<script type="application/json|ld+json">` and `window.X = {...};`. */
export function embeddedJson(html: string): { source: string; value: unknown }[] {
  const out: { source: string; value: unknown }[] = [];
  for (const match of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)) {
    const attrs = match[1] ?? '';
    const type = /type\s*=\s*["']([^"']+)["']/i.exec(attrs)?.[1] ?? '';
    if (/json/i.test(type)) {
      try {
        out.push({ source: `script[${type}]${/id\s*=\s*["']([^"']+)/i.exec(attrs)?.[1] ? `#${/id\s*=\s*["']([^"']+)/i.exec(attrs)?.[1]}` : ''}`, value: JSON.parse(match[2] ?? '') });
      } catch {
        // Not JSON after all.
      }
    }
  }
  for (const match of html.matchAll(/window\.(__[A-Z_]+__)\s*=\s*/g)) {
    const start = (match.index ?? 0) + match[0].length;
    const text = cutObject(html, start);
    if (text) {
      try {
        out.push({ source: `window.${match[1]}`, value: JSON.parse(text) });
      } catch {
        // Not plain JSON (e.g. JSON.parse("...") wrapped): left unread.
      }
    }
  }
  return out;
}

/** The balanced `{...}` or `[...]` starting at `start`, string- and escape-aware; null when it does not start one. */
function cutObject(text: string, start: number): string | null {
  const open = text[start];
  if (open !== '{' && open !== '[') {
    return null;
  }
  let depth = 0;
  let inString = false;
  for (let i = start; i < text.length; i++) {
    const c = text[i];
    if (inString) {
      if (c === '\\') {
        i++;
      } else if (c === '"') {
        inString = false;
      }
    } else if (c === '"') {
      inString = true;
    } else if (c === '{' || c === '[') {
      depth++;
    } else if (c === '}' || c === ']') {
      depth--;
      if (depth === 0) {
        return text.slice(start, i + 1);
      }
    }
  }
  return null;
}

const NAME_KEY = /^(name|title|description|displayName|productName)$/i;
const PRICE_KEY = /price|offer/i;
const BARCODE_KEY = /^(gtin\d*|ean\d*|upc|barcode|barcodes|ean13|gtin13|isbn|eans|gtins)$/i;

/** The first object in the tree that looks like a product: it has a name-ish and a price-ish key. Breadth-first, so the shallowest wins. */
export function findProductRecord(root: unknown): { path: string; record: Record<string, unknown> } | undefined {
  const queue: { path: string; value: unknown }[] = [{ path: '$', value: root }];
  let visited = 0;
  while (queue.length > 0 && visited++ < 20_000) {
    const { path, value } = queue.shift()!;
    if (Array.isArray(value)) {
      value.slice(0, 50).forEach((item, i) => queue.push({ path: `${path}[${i}]`, value: item }));
    } else if (isRecord(value)) {
      const keys = Object.keys(value);
      if (keys.some((k) => NAME_KEY.test(k)) && keys.some((k) => PRICE_KEY.test(k))) {
        return { path, record: value };
      }
      for (const key of keys) {
        queue.push({ path: `${path}.${key}`, value: value[key] });
      }
    }
  }
  return undefined;
}

/** A copy that is safe to print: long strings cut, long arrays cut, deep objects cut. Field names are kept. */
export function trimRecord(value: unknown, depth = 0): unknown {
  if (typeof value === 'string') {
    return value.length > 80 ? `${value.slice(0, 80)}...` : value;
  }
  if (Array.isArray(value)) {
    const head = value.slice(0, 2).map((v) => trimRecord(v, depth + 1));
    return value.length > 2 ? [...head, `... ${value.length - 2} more`] : head;
  }
  if (isRecord(value)) {
    if (depth >= 4) {
      return `{${Object.keys(value).length} keys}`;
    }
    return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, trimRecord(v, depth + 1)]));
  }
  return value;
}

/** Dotted paths of every key in the record (arrays as `[]`), so the field names are readable at a glance. */
export function fieldPaths(value: unknown, prefix = '', out: string[] = []): string[] {
  if (Array.isArray(value)) {
    if (value[0] !== undefined) {
      fieldPaths(value[0], `${prefix}[]`, out);
    }
  } else if (isRecord(value)) {
    for (const [key, child] of Object.entries(value)) {
      const path = prefix ? `${prefix}.${key}` : key;
      out.push(path);
      fieldPaths(child, path, out);
    }
  }
  return out;
}

export interface BarcodeFacts {
  present: boolean;
  /** Where a barcode-looking key was found: "source: path". At most a few. */
  at: string[];
  example?: string;
}

/** Whether a barcode (GTIN/EAN/UPC) appears in any of the JSON documents, by key name. A product's barcode is public shelf data. */
export function findBarcode(documents: readonly { source: string; value: unknown }[]): BarcodeFacts {
  const at: string[] = [];
  let example: string | undefined;
  const walk = (source: string, value: unknown, path: string, depth: number): void => {
    if (at.length >= 5 || depth > 12) {
      return;
    }
    if (Array.isArray(value)) {
      value.slice(0, 20).forEach((item) => walk(source, item, `${path}[]`, depth + 1));
    } else if (isRecord(value)) {
      for (const [key, child] of Object.entries(value)) {
        const here = `${path}.${key}`;
        if (BARCODE_KEY.test(key) && child !== null && child !== '' && !isRecord(child)) {
          const text = String(Array.isArray(child) ? child[0] : child);
          if (/^\d{8,14}$/.test(text)) {
            at.push(`${source}: ${here}`);
            example ??= text;
          }
        }
        walk(source, child, here, depth + 1);
      }
    }
  };
  for (const doc of documents) {
    walk(doc.source, doc.value, '$', 0);
  }
  return { present: at.length > 0, at: [...new Set(at)], ...(example ? { example } : {}) };
}

export interface TotalFact {
  at: string;
  value: number;
}

const TOTAL_KEY = /^(total|totalcount|totalresults|totalitems|totalproducts|numfound|numberofresults|resultcount|count|totalpages|pagecount|pagesize|perpage|limit|pagenumber|page|currentpage|nbhits|nbpages|hitsperpage)$/i;

/** Numbers under count/page-size/page-number keys, with where they were, to read the pagination off a category listing. */
export function findTotals(documents: readonly { source: string; value: unknown }[]): TotalFact[] {
  const out: TotalFact[] = [];
  const walk = (source: string, value: unknown, path: string, depth: number): void => {
    if (out.length >= 20 || depth > 10) {
      return;
    }
    if (Array.isArray(value)) {
      value.slice(0, 3).forEach((item) => walk(source, item, `${path}[]`, depth + 1));
    } else if (isRecord(value)) {
      for (const [key, child] of Object.entries(value)) {
        if (typeof child === 'number' && TOTAL_KEY.test(key)) {
          out.push({ at: `${source}: ${path}.${key}`, value: child });
        }
        walk(source, child, `${path}.${key}`, depth + 1);
      }
    }
  };
  for (const doc of documents) {
    walk(doc.source, doc.value, '$', 0);
  }
  return out;
}
