import type {TagVariant} from '@lifekit-hq/ui';

import type {CatalogHealth, RunOutcome, StoreStatus} from './prices.model';

const MS_PER_DAY = 86_400_000;
const THOUSAND = 1000;

const SWEEP_OUTCOMES: Record<RunOutcome, string> = {
  ok: '',
  partial: 'did not reach the whole range',
  blocked: 'was refused by the shop',
  failed: 'listed nothing',
};

/** "today", "yesterday", "3 days ago": whole days since `iso`, counted from `now`. */
export function ago(iso: string, now: Date): string {
  const days = Math.max(0, Math.floor((now.getTime() - new Date(iso).getTime()) / MS_PER_DAY));
  if (days === 0) {
    return 'today';
  }
  return days === 1 ? 'yesterday' : `${days} days ago`;
}

/** 3554 -> "3,554". */
function count(value: number): string {
  return value >= THOUSAND ? value.toLocaleString('en-IE') : String(value);
}

function products(value: number): string {
  return value === 1 ? '1 product' : `${count(value)} products`;
}

/** What the row's tag says and how loudly: a stale or shrunk catalog is a warning, a shop with no sweep yet is neutral. */
export function healthTag(health: CatalogHealth): {text: string; variant: TagVariant} {
  if (health.state === 'never') {
    return {text: 'No sweep yet', variant: 'neutral'};
  }
  if (health.state === 'stale') {
    return {text: 'Stale', variant: 'warning'};
  }
  if (health.shrunk) {
    return {text: 'Shrunk', variant: 'warning'};
  }
  if (health.lastSweep && health.lastSweep.outcome !== 'ok') {
    return {text: 'Check', variant: 'warning'};
  }
  return {text: 'Up to date', variant: 'success'};
}

/**
 * The lines under a shop in the catalog card: when it was last swept and how many products that listed, how the catalog stands
 * (live and discontinued), and what the shelf has lost to it. A shop with no sweep recorded says so and invents nothing.
 */
export function healthLines(health: CatalogHealth, now: Date): string[] {
  const sweep = health.lastSweep;
  const stands = [`${products(health.live)} live`];
  if (health.discontinued > 0) {
    stands.push(`${count(health.discontinued)} discontinued`);
  }
  if (health.discontinuedOnShelf > 0) {
    stands.push(
      health.discontinuedOnShelf === 1
        ? '1 on your shelf'
        : `${health.discontinuedOnShelf} on your shelf`
    );
  }
  if (!sweep) {
    return ['No catalog sweep recorded yet', health.live > 0 ? stands.join(' · ') : ''].filter(
      Boolean
    );
  }
  const swept = [`Swept ${ago(sweep.finishedAt, now)}`, `${products(sweep.seen)} listed`];
  if (sweep.outcome !== 'ok') {
    swept.push(`the sweep ${SWEEP_OUTCOMES[sweep.outcome]}`);
  }
  const lines = [swept.join(' · '), stands.join(' · ')];
  if (health.state === 'stale') {
    lines.push(
      health.lastOkAt
        ? `Last complete sweep ${ago(health.lastOkAt, now)}`
        : 'No complete sweep recorded'
    );
  }
  return lines;
}

/** The warnings the page raises above the list: a stale catalog, and a sweep that saw far less than the one before. */
export function healthWarnings(status: StoreStatus, now: Date): string[] {
  const health = status.catalog;
  const warnings: string[] = [];
  if (health.state === 'stale') {
    warnings.push(
      health.lastOkAt
        ? `${status.name}'s catalog is stale: no complete sweep for more than ${health.staleAfterDays} days (last ${ago(health.lastOkAt, now)})`
        : `${status.name}'s catalog is stale: no sweep has completed`
    );
  }
  if (health.shrunk) {
    warnings.push(
      `${status.name}'s last sweep listed ${count(health.shrunk.seen)} products, under 90% of the ${count(health.shrunk.previous)} the last complete sweep listed`
    );
  }
  return warnings;
}
