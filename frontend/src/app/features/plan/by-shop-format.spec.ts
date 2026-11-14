import {basket, bought, shop} from './by-shop-fixtures';
import {lineDetail, orderedShops, storeDetail, verdict, walkSections} from './by-shop-format';
import {missingTag} from './plan-format';

const SPLIT = {total: 0, complete: false, versusCheapest: null, groups: [], unplaced: []};

describe('verdict', () => {
  it('names the cheapest complete shop and says what splitting would save', () => {
    const answer = verdict(
      basket({
        stores: [
          shop({
            store: 'aldi',
            storeName: 'Aldi',
            priced: 2,
            total: 3.5,
            complete: true,
            cheapest: true,
          }),
        ],
        split: {...SPLIT, total: 3.4, complete: true, versusCheapest: -0.1},
      })
    );

    expect(answer.status).toEqual({tone: 'success', label: 'Aldi is cheapest'});
    expect(answer.pills.map(p => p.label)).toEqual(['€3.50', 'Split saves €0.10']);
    expect(answer.caveat).toBe(
      'Aldi has a fresh price for every line, €3.50 in all. Splitting the list across shops costs €3.40, €0.10 less.'
    );
  });

  it('says so when the split costs more because it buys the better value pack', () => {
    const answer = verdict(
      basket({
        stores: [shop({priced: 2, total: 3.5, complete: true, cheapest: true})],
        split: {...SPLIT, total: 3.8, complete: true, versusCheapest: 0.3},
      })
    );

    expect(answer.pills.map(p => p.label)).toEqual(['€3.50']);
    expect(answer.caveat).toContain('€3.80, €0.30 more, because it buys the better value pack');
  });

  it('names the closest shop, its total so far and the lines it lacks when no shop is complete', () => {
    const answer = verdict(
      basket({
        lineCount: 3,
        stores: [
          shop({
            priced: 1,
            total: 1.2,
            missing: [{itemId: 'x', text: 'Eggs', reason: 'not_linked'}],
          }),
          shop({
            store: 'aldi',
            storeName: 'Aldi',
            priced: 2,
            total: 4.1,
            missing: [{itemId: 'x', text: 'Eggs', reason: 'not_linked'}],
          }),
        ],
        split: {
          ...SPLIT,
          total: 3.3,
          groups: [{store: 'aldi', storeName: 'Aldi', total: 3.3, lines: [bought()]}],
          unplaced: [{itemId: 'x', text: 'Eggs', reason: 'no_price'}],
        },
      })
    );

    expect(answer.status).toEqual({tone: 'warning', label: 'No shop prices the whole list'});
    expect(answer.pills.map(p => p.label)).toEqual(['Closest: Aldi', '1 line missing']);
    expect(answer.caveat).toBe(
      'Aldi has a fresh price for 2 of 3 lines, €4.10 so far. Splitting the list across shops places 2 of 3 for €3.30.'
    );
  });

  it('says no shop prices the list when none prices a line', () => {
    const answer = verdict(basket({lineCount: 1, stores: [shop(), shop({store: 'aldi'})]}));

    expect(answer.status.label).toBe('No shop prices it yet');
    expect(answer.caveat).toBe('None of the lines has a fresh price at any shop.');
  });
});

describe('storeDetail', () => {
  it('counts the lines, dates the oldest price and says what is in the total', () => {
    const text = storeDetail(
      shop({priced: 3, oldestSeenOn: '2026-11-04', cards: ['Clubcard'], packsDiffer: 2}),
      4
    );

    expect(text).toBe(
      '3 of 4 lines · oldest price 4 Nov · Clubcard price included · packs differ on 2'
    );
  });

  it('counts offers when no card is needed, and says nothing of a shop that prices nothing', () => {
    expect(storeDetail(shop({priced: 1, oldestSeenOn: '2026-11-04', promoLines: 1}), 1)).toBe(
      '1 of 1 line · oldest price 4 Nov · 1 offer included'
    );
    expect(storeDetail(shop(), 2)).toBe('0 of 2 lines');
  });
});

describe('walkSections', () => {
  it("cuts a shop's lines into runs of one section, keeping the order they came in", () => {
    const sections = walkSections([
      bought({itemId: 'a', section: 'Fruit & veg'}),
      bought({itemId: 'b', section: 'Fruit & veg'}),
      bought({itemId: 'c', section: 'Drinks'}),
      bought({itemId: 'd', section: 'Other'}),
    ]);

    expect(sections.map(s => [s.section, s.lines.map(l => l.itemId)])).toEqual([
      ['Fruit & veg', ['a', 'b']],
      ['Drinks', ['c']],
      ['Other', ['d']],
    ]);
  });

  it('has no sections for no lines', () => {
    expect(walkSections([])).toEqual([]);
  });
});

describe('lineDetail', () => {
  it('puts the date first, then the deal, the card it needs and the unit price', () => {
    expect(
      lineDetail(
        bought({isPromo: true, deal: '2 for €4', card: 'Clubcard', unitPrice: 4, unit: 'kg'})
      )
    ).toBe('Seen 5 Nov · 2 for €4 · needs Clubcard · €4.00/kg');
  });

  it('does not repeat the card when the deal already names it', () => {
    expect(
      lineDetail(
        bought({
          isPromo: true,
          deal: 'Clubcard price',
          card: 'Clubcard',
          unit: null,
          unitPrice: null,
        })
      )
    ).toBe('Seen 5 Nov · Clubcard price');
  });
});

describe('orderedShops', () => {
  it('puts complete shops first by total, then the others by lines priced', () => {
    const order = orderedShops(
      basket({
        stores: [
          shop({store: 'lidl', priced: 1}),
          shop({store: 'aldi', priced: 2, total: 4, complete: true}),
          shop({store: 'tesco', priced: 2, total: 3, complete: true, cheapest: true}),
          shop({store: 'dunnes', priced: 0}),
        ],
      })
    ).map(s => s.store);

    expect(order).toEqual(['tesco', 'aldi', 'lidl', 'dunnes']);
  });
});

describe('missingTag', () => {
  it('words a shop that cannot price a line', () => {
    expect(missingTag('not_linked')).toBe('No page there');
    expect(missingTag('stale')).toBe('Old price');
    expect(missingTag('out_of_stock')).toBe('Out of stock');
    expect(missingTag('unmatched')).toBe('Not matched');
    expect(missingTag('upcoming')).toBe('Offer not started');
  });
});
