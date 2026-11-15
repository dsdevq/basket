import type {CatalogHealth} from './prices.model';

/** A shop whose catalog was swept last night, complete: the healthy case the tests start from. */
export function health(over: Partial<CatalogHealth> = {}): CatalogHealth {
  return {
    state: 'fresh',
    staleAfterDays: 2,
    lastSweep: {
      finishedAt: '2026-11-05T03:30:00Z',
      outcome: 'ok',
      seen: 690,
      live: 690,
      message: null,
    },
    lastOkAt: '2026-11-05T03:30:00Z',
    live: 690,
    discontinued: 0,
    discontinuedOnShelf: 0,
    shrunk: null,
    ...over,
  };
}

/** A shop with no sweep row at all: nothing is guessed. */
export function neverSwept(over: Partial<CatalogHealth> = {}): CatalogHealth {
  return health({
    state: 'never',
    lastSweep: null,
    lastOkAt: null,
    live: 0,
    ...over,
  });
}
