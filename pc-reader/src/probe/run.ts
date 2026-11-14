import { detectBlock } from '../parse/block.ts';
import { MIN_GAP_MS } from '../run.ts';
import {
  embeddedJson,
  fieldPaths,
  findBarcode,
  findProductRecord,
  findTotals,
  readRobots,
  readSitemap,
  sameOriginLinks,
  trimRecord,
  type BarcodeFacts,
  type RobotsFacts,
  type SitemapFacts,
  type TotalFact,
} from './analyse.ts';
import { jsonEndpoints, pagingParameters, type DiscoveredEndpoint, type SeenRequest } from './discover.ts';

export type ProbeStore = 'tesco' | 'dunnes';
export const PROBE_STORES: readonly ProbeStore[] = ['dunnes', 'tesco'];

interface StoreShape {
  origin: string;
  /** A category listing's address path, and a product page's. */
  category: RegExp;
  product: RegExp;
}

const SHAPES: Record<ProbeStore, StoreShape> = {
  tesco: { origin: 'https://www.tesco.ie', category: /^\/groceries\/en-IE\/shop\/[a-z0-9-]+(\/[a-z0-9-]+)*\/all$/i, product: /^\/groceries\/en-IE\/products\/\d+$/i },
  dunnes: { origin: 'https://www.dunnesstoresgrocery.com', category: /^\/(category|shop|groceries)\/[^?]+$/i, product: /^\/product\/[a-z0-9-]+-\d{6,12}$/i },
};

/** What one page load gave, with the page's own network calls and the JSON bodies it fetched. */
export interface Inspected {
  status: number;
  contentType: string;
  body: string;
  requests: SeenRequest[];
  /** Bodies of the JSON responses the page fetched itself (size-capped by the browser layer). */
  json: { url: string; value: unknown }[];
}

export interface ProbeDeps {
  inspect(url: string): Promise<Inspected>;
  sleep(ms: number): Promise<void>;
  now(): number;
  log(line: string): void;
}

export interface StepResult {
  step: 'robots' | 'sitemap' | 'home' | 'category' | 'product';
  url: string;
  status?: number;
  contentType?: string;
  bytes?: number;
  note?: string;
}

export interface ProbeReport {
  store: ProbeStore;
  startedAt: string;
  steps: StepResult[];
  /** Set when the store refused: the probe stopped there, nothing was retried, the later steps were not asked. */
  blocked?: { step: StepResult['step']; url: string; why: string; notAsked: string[] };
  robots?: RobotsFacts;
  sitemap?: SitemapFacts;
  /** The JSON/XHR addresses each page fetched for itself. */
  endpoints: Partial<Record<'home' | 'category' | 'product', DiscoveredEndpoint[]>>;
  pagination?: { parameters: string[]; totals: TotalFact[] };
  product?: { jsonAt: string; fields: string[]; example: unknown; barcode: BarcodeFacts };
}

const STEP_ORDER = ['robots', 'sitemap', 'home', 'category', 'product'] as const;

/**
 * The probe: /robots.txt, /sitemap.xml, the home page, one category page, one product page, one at a time, MIN_GAP_MS
 * apart, no retries. The first refusal ends it (the reader's own block detection decides). It reads only: no login, no
 * posting, nothing sent to basket. The category and product addresses are the first ones the previous page links to.
 */
export async function probe(store: ProbeStore, deps: ProbeDeps, minGapMs = MIN_GAP_MS): Promise<ProbeReport> {
  const shape = SHAPES[store];
  const report: ProbeReport = { store, startedAt: new Date(deps.now()).toISOString(), steps: [], endpoints: {} };
  let lastLoad: number | undefined;
  let categoryUrl: string | undefined;
  let productUrl: string | undefined;

  const fetchStep = async (step: StepResult['step'], url: string): Promise<{ result: StepResult; page?: Inspected }> => {
    if (lastLoad !== undefined) {
      const wait = lastLoad + minGapMs - deps.now();
      if (wait > 0) {
        await deps.sleep(wait);
      }
    }
    lastLoad = deps.now();
    try {
      const page = await deps.inspect(url);
      const result: StepResult = { step, url, status: page.status, contentType: page.contentType, bytes: page.body.length };
      deps.log(`${store}: ${step} ${page.status} ${url}`);
      return { result, page };
    } catch (error) {
      const note = `did not load: ${error instanceof Error ? error.message : String(error)}`;
      deps.log(`${store}: ${step} ${note}`);
      return { result: { step, url, note } };
    }
  };

  for (const step of STEP_ORDER) {
    const url =
      step === 'robots' ? `${shape.origin}/robots.txt`
      : step === 'sitemap' ? `${shape.origin}/sitemap.xml`
      : step === 'home' ? `${shape.origin}/`
      : step === 'category' ? categoryUrl
      : productUrl;
    if (!url) {
      report.steps.push({ step, url: '', note: `no ${step} address found on the previous page` });
      continue;
    }
    const { result, page } = await fetchStep(step, url);
    report.steps.push(result);
    if (!page) {
      continue;
    }

    const why = detectBlock({ status: page.status, html: page.body });
    if (why) {
      const notAsked = STEP_ORDER.slice(STEP_ORDER.indexOf(step) + 1).map(String);
      report.blocked = { step, url, why, notAsked };
      deps.log(`${store}: BLOCKED (${why}) at ${url}; stopping`);
      return report;
    }

    if (step === 'robots') {
      report.robots = readRobots(page.body);
    } else if (step === 'sitemap') {
      report.sitemap = readSitemap(page.body);
    } else {
      report.endpoints[step] = jsonEndpoints(page.requests);
      const docs = [...embeddedJson(page.body).map((d) => ({ source: d.source, value: d.value })), ...page.json.map((j) => ({ source: `xhr ${j.url}`, value: j.value }))];
      if (step === 'home') {
        categoryUrl = firstMatching(page.body, url, shape.category);
      } else if (step === 'category') {
        productUrl = firstMatching(page.body, url, shape.product);
        const urls = [url, ...sameOriginLinks(page.body, url), ...page.requests.map((r) => r.url)];
        report.pagination = { parameters: pagingParameters(urls), totals: findTotals(docs) };
      } else {
        const found = [...docs.map((d) => ({ doc: d, hit: findProductRecord(d.value) }))].find((x) => x.hit);
        report.product = {
          jsonAt: found ? `${found.doc.source}: ${found.hit!.path}` : 'none found',
          fields: found ? fieldPaths(found.hit!.record) : [],
          example: found ? trimRecord(found.hit!.record) : null,
          barcode: findBarcode(docs),
        };
      }
    }
  }
  return report;
}

function firstMatching(html: string, pageUrl: string, path: RegExp): string | undefined {
  return sameOriginLinks(html, pageUrl).find((link) => path.test(new URL(link).pathname));
}
