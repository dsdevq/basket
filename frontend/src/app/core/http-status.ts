/** The status codes the screens react to. */
export const HttpStatus = {
  unauthorized: 401,
  notFound: 404,
  tooManyRequests: 429,
} as const;

/** The HTTP status of a failed call, or 0 when the error is not an HTTP one. */
export function statusOf(error: unknown): number {
  return (error as {status?: number} | null)?.status ?? 0;
}

/**
 * A short, safe label for a failed call, shown next to "cannot reach basket" so a screenshot says what happened:
 * no answer at all (offline, blocked, the server down) or the status the server sent. Never the URL or a body.
 */
export function describeFailure(error: unknown): string {
  const status = statusOf(error);
  return status === 0 ? 'no answer from the server' : `the server answered HTTP ${status}`;
}

/** The longest display name the API accepts. */
export const MAX_NAME_LENGTH = 40;
