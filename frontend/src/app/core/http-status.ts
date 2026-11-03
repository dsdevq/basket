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

/** The longest display name the API accepts. */
export const MAX_NAME_LENGTH = 40;
