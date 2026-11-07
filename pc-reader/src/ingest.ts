import type { Reading } from './types.ts';

/** The server's per-batch cap (`PriceIngest.MaxBatch`). */
export const MAX_BATCH = 200;

export interface IngestItemResult {
  index: number;
  accepted: boolean;
  reason: string | null;
}

export interface PostOutcome {
  /** HTTP status of the answer. */
  status: number;
  accepted: number;
  rejected: IngestItemResult[];
  /** Set when the batch as a whole was refused (401, 413, 429, ...), with what the server said. */
  error?: string;
}

/**
 * One POST to the write-only ingest endpoint. A refusal is reported, never retried: the next run is the retry.
 * The token goes only in the Authorization header and never into a message.
 */
export async function postBatch(origin: string, token: string, readings: Reading[], fetchImpl: typeof fetch = fetch): Promise<PostOutcome> {
  const response = await fetchImpl(new URL('/api/ingest/prices', origin), {
    method: 'POST',
    headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
    body: JSON.stringify({ prices: readings }),
  });
  const body: unknown = await response.json().catch(() => undefined);
  const results = isResults(body) ? body.results : [];
  const accepted = results.filter((r) => r.accepted).length;
  const outcome: PostOutcome = { status: response.status, accepted, rejected: results.filter((r) => !r.accepted) };
  // 200 and 422 both carry per-reading results; anything else is the whole batch being refused.
  if (!(response.status === 200 || (response.status === 422 && isResults(body)))) {
    outcome.error = describe(body) ?? `HTTP ${response.status}`;
  }
  return outcome;
}

function isResults(body: unknown): body is { results: IngestItemResult[] } {
  return typeof body === 'object' && body !== null && Array.isArray((body as { results?: unknown }).results);
}

function describe(body: unknown): string | undefined {
  if (typeof body === 'object' && body !== null) {
    const { error, message } = body as { error?: unknown; message?: unknown };
    if (typeof error === 'string') {
      return typeof message === 'string' ? `${error}: ${message}` : error;
    }
  }
  return undefined;
}
