/** One network response a page made while it loaded, as far as discovery needs to know it. */
export interface SeenRequest {
  url: string;
  method: string;
  /** Playwright's resource type: "xhr", "fetch", "document", "script", ... */
  resourceType: string;
  status: number;
  contentType: string;
}

/** A JSON/XHR address the page itself fetched: the query string is kept (it carries the paging), nothing else is. */
export interface DiscoveredEndpoint {
  url: string;
  method: string;
  status: number;
  contentType: string;
}

const JSON_TYPE = /json/i;

/**
 * The addresses a page fetched for itself over XHR/fetch, or that answered JSON: the Aldi discovery method, where the
 * page's own calls name the endpoint a sweep would use. Order of first sight, each address once. Static assets and
 * the page document are not endpoints and are left out.
 */
export function jsonEndpoints(seen: readonly SeenRequest[]): DiscoveredEndpoint[] {
  const found = new Map<string, DiscoveredEndpoint>();
  for (const request of seen) {
    const dynamic = request.resourceType === 'xhr' || request.resourceType === 'fetch';
    const json = JSON_TYPE.test(request.contentType) && request.resourceType !== 'script' && request.resourceType !== 'stylesheet';
    if (!(dynamic || json)) {
      continue;
    }
    const key = `${request.method} ${request.url}`;
    if (!found.has(key)) {
      found.set(key, { url: request.url, method: request.method, status: request.status, contentType: request.contentType });
    }
  }
  return [...found.values()];
}

/** Query parameter names that usually page a listing, so the report can name them without a human reading every URL. */
const PAGING_NAMES = /^(page|pagenumber|pageno|p|offset|start|skip|limit|count|size|pagesize|perpage|per_page|take|from|cursor|after|sort)$/i;

export function pagingParameters(urls: readonly string[]): string[] {
  const names = new Set<string>();
  for (const url of urls) {
    let parsed: URL;
    try {
      parsed = new URL(url);
    } catch {
      continue;
    }
    for (const name of parsed.searchParams.keys()) {
      if (PAGING_NAMES.test(name)) {
        names.add(name);
      }
    }
  }
  return [...names].sort();
}
