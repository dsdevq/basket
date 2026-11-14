import {card, entry} from './scorecard-fixtures';
import {monthLabel, orderedStores, verdict} from './scorecard-format';

const SCORED = card({
  trips: 4,
  compared: 10,
  notCompared: 2,
  saved: 6.5,
  topStore: 'aldi',
  stores: [
    entry({store: 'lidl', storeName: 'Lidl', wins: 3, share: 30}),
    entry({store: 'aldi', storeName: 'Aldi', wins: 6, share: 60}),
    entry({store: 'tesco', storeName: 'Tesco', wins: 1, share: 10}),
    entry({store: 'dunnes', storeName: 'Dunnes'}),
  ],
  trend: [
    {month: '2026-09', trips: 1, compared: 2, saved: 1},
    {month: '2026-10', trips: 1, compared: 3, saved: 2.5},
    {month: '2026-11', trips: 2, compared: 5, saved: 3},
  ],
});

describe('scorecard format', () => {
  it('names a month', () => {
    expect(monthLabel('2026-11')).toBe('Nov 2026');
    expect(monthLabel('nope')).toBe('nope');
  });

  it('puts the shop with most wins first and keeps the fixed order on a tie', () => {
    expect(orderedStores(SCORED).map(s => s.store)).toEqual(['aldi', 'lidl', 'tesco', 'dunnes']);
  });

  it('says who wins, what was saved and what was left out', () => {
    const answer = verdict(SCORED);
    expect(answer.status.label).toBe('Aldi wins most');
    expect(answer.pills.map(p => p.label)).toEqual(['€6.50 saved', '10 lines']);
    expect(answer.caveat).toBe('Aldi was cheapest on 6 of 10 lines. 2 not compared.');
    expect(answer.caveat.length).toBeLessThan(75);
  });

  it('does not name a winner when nothing could be compared', () => {
    const answer = verdict(card({trips: 2, notCompared: 1}));
    expect(answer.status.label).toBe('Nothing to score yet');
    expect(answer.caveat).toBe('No line had fresh prices at two shops. 1 not compared.');
  });
});
