import type {HistoryPoint, PriceHistory} from './price-history.model';
import {changes, chart, summary} from './price-history-format';

function point(
  observedOn: string,
  effective: number | null,
  over: Partial<HistoryPoint> = {}
): HistoryPoint {
  return {
    observedOn,
    effective,
    price: effective,
    wasPrice: null,
    loyaltyPrice: null,
    loyaltyScheme: null,
    promoText: null,
    inStock: true,
    ...over,
  };
}

function history(over: Partial<PriceHistory> = {}): PriceHistory {
  const points = [
    point('2026-09-20', 2.5),
    point('2026-10-10', 2.5),
    point('2026-11-10', 1.8, {promoText: 'Was €2.50'}),
  ];
  return {
    productId: 'p1',
    store: 'lidl',
    storeName: 'Lidl',
    days: 90,
    from: '2026-08-13',
    to: '2026-11-10',
    points,
    lowest: {price: 1.8, on: '2026-11-10'},
    highest: {price: 2.5, on: '2026-10-10'},
    current: 1.8,
    lowestIn90Days: true,
    ...over,
  };
}

describe('price history format', () => {
  it('says plainly when today is the lowest in 90 days and what it was before', () => {
    expect(summary(history())).toBe('€1.80 is the lowest in 90 days (it was €2.50 on 10 Oct).');
  });

  it('gives the range when today is not the lowest', () => {
    const text = summary(
      history({lowestIn90Days: false, lowest: {price: 1.5, on: '2026-10-01'}, current: 1.8})
    );
    expect(text).toBe('€1.80 now. Lowest €1.50 on 1 Oct, highest €2.50 on 10 Oct.');
  });

  it('says so when the price never moved, and when nothing was read', () => {
    expect(
      summary(
        history({
          lowestIn90Days: false,
          lowest: {price: 2, on: '2026-10-01'},
          highest: {price: 2, on: '2026-10-01'},
          current: 2,
        })
      )
    ).toContain('unchanged');
    expect(summary(history({points: [], lowest: null, highest: null, current: null}))).toContain(
      'No price has been read'
    );
  });

  it('lists only the days the price moved, newest first', () => {
    const rows = changes(history());

    expect(rows.map(row => [row.day, row.price])).toEqual([
      ['10 Nov', '€1.80'],
      ['20 Sep', '€2.50'],
    ]);
    expect(rows[0]?.note).toBe('Was €2.50');
  });

  it('draws a step line over the calendar, or nothing when there is no price', () => {
    const geometry = chart(history());

    expect(geometry?.path.startsWith('M')).toBe(true);
    expect(geometry?.path).toContain('V');
    expect(geometry?.min).toBe(1.8);
    expect(geometry?.max).toBe(2.5);
    expect(chart(history({points: [point('2026-11-10', null)]}))).toBeNull();
  });
});
