import {health, neverSwept} from './catalog-health-fixtures';
import {ago, healthLines, healthTag, healthWarnings} from './catalog-health-format';
import type {StoreStatus} from './prices.model';

const NOW = new Date('2026-11-06T10:00:00Z');

function status(catalog = health(), name = 'Lidl'): StoreStatus {
  return {store: 'lidl', name, readBy: 'nightly', catalog, lastRun: null};
}

describe('catalog health formatting', () => {
  it('counts whole days back from now', () => {
    expect(ago('2026-11-06T03:30:00Z', NOW)).toBe('today');
    expect(ago('2026-11-05T11:00:00Z', NOW)).toBe('today');
    expect(ago('2026-11-05T09:00:00Z', NOW)).toBe('yesterday');
    expect(ago('2026-11-01T03:30:00Z', NOW)).toBe('5 days ago');
    expect(ago('2026-11-07T03:30:00Z', NOW)).toBe('today');
  });

  it('says there is no sweep yet instead of guessing, with the catalog size when one is known', () => {
    expect(healthLines(neverSwept(), NOW)).toEqual(['No catalog sweep recorded yet']);
    expect(healthLines(neverSwept({live: 40}), NOW)).toEqual([
      'No catalog sweep recorded yet',
      '40 products live',
    ]);
    expect(healthTag(neverSwept())).toEqual({text: 'No sweep yet', variant: 'neutral'});
    expect(healthWarnings(status(neverSwept()), NOW)).toEqual([]);
  });

  it('writes when the shop was swept, what that listed and how the catalog stands', () => {
    const lines = healthLines(health({live: 3554, discontinued: 12, discontinuedOnShelf: 2}), NOW);

    expect(lines).toEqual([
      'Swept yesterday · 690 products listed',
      '3,554 products live · 12 discontinued · 2 on your shelf',
    ]);
    expect(healthTag(health())).toEqual({text: 'Up to date', variant: 'success'});
    expect(healthWarnings(status(), NOW)).toEqual([]);
  });

  it('keeps singular counts singular and leaves out a discontinued count of none', () => {
    expect(healthLines(health({live: 1, discontinuedOnShelf: 1}), NOW)[1]).toBe(
      '1 product live · 1 on your shelf'
    );
  });

  it('flags a stale catalog with the last complete sweep, or says none completed', () => {
    const stale = health({state: 'stale', lastOkAt: '2026-11-01T03:30:00Z'});
    expect(healthLines(stale, NOW)[2]).toBe('Last complete sweep 5 days ago');
    expect(healthTag(stale)).toEqual({text: 'Stale', variant: 'warning'});
    expect(healthWarnings(status(stale), NOW)).toEqual([
      "Lidl's catalog is stale: no complete sweep for more than 2 days (last 5 days ago)",
    ]);

    const never = health({state: 'stale', lastOkAt: null});
    expect(healthLines(never, NOW)[2]).toBe('No complete sweep recorded');
    expect(healthWarnings(status(never), NOW)).toEqual([
      "Lidl's catalog is stale: no sweep has completed",
    ]);
  });

  it('names a sweep that did not finish well, and warns when it listed under 90% of the one before', () => {
    const shrunk = health({
      lastSweep: {
        finishedAt: '2026-11-06T03:30:00Z',
        outcome: 'partial',
        seen: 300,
        live: 690,
        message: null,
      },
      shrunk: {seen: 300, previous: 690},
    });

    expect(healthLines(shrunk, NOW)[0]).toBe(
      'Swept today · 300 products listed · the sweep did not reach the whole range'
    );
    expect(healthTag(shrunk)).toEqual({text: 'Shrunk', variant: 'warning'});
    expect(healthWarnings(status(shrunk), NOW)).toEqual([
      "Lidl's last sweep listed 300 products, under 90% of the 690 the last complete sweep listed",
    ]);
  });

  it('asks for a check when the last sweep was refused or listed nothing but nothing shrank', () => {
    const blocked = health({
      lastSweep: {
        finishedAt: '2026-11-06T03:30:00Z',
        outcome: 'blocked',
        seen: 0,
        live: 690,
        message: null,
      },
    });

    expect(healthTag(blocked)).toEqual({text: 'Check', variant: 'warning'});
    expect(healthLines(blocked, NOW)[0]).toContain('the sweep was refused by the shop');
  });
});
