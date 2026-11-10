import type {PlanCoverage, PlanPairSplit} from './plan.model';
import {entry, line, offer, paid} from './plan-fixtures';
import {
  blockerTag,
  flagText,
  orderedStores,
  paidText,
  planRows,
  priceText,
  rowDetail,
  rowLines,
  rowTag,
  tagVariant,
  verdict,
  wasText,
} from './plan-format';

describe('plan-format', () => {
  describe('a shop row', () => {
    it('shows the date first, then the deal, the card it needs and the unit price', () => {
      const clubcard = entry({
        store: 'tesco',
        storeName: 'Tesco',
        observedOn: '2026-11-05',
        offer: offer({
          kind: 'loyalty',
          isPromo: true,
          deal: '2 for €4',
          card: 'Clubcard',
          eachPrice: 2,
          unit: 'kg',
          unitPrice: 4.92,
        }),
      });

      expect(rowDetail(clubcard)).toBe('Seen 5 Nov · 2 for €4 · needs Clubcard · €4.92/kg');
      expect(priceText(clubcard)).toBe('€2.00');
    });

    it('does not repeat a card the deal already names, nor a shelf price as if it were a deal', () => {
      const loyalty = entry({
        offer: offer({isPromo: true, deal: 'Clubcard price', card: 'Clubcard'}),
      });
      const shelf = entry({offer: offer({unit: null, unitPrice: null})});

      expect(rowDetail(loyalty)).toBe('Seen 5 Nov · Clubcard price · €0.65/l');
      expect(rowDetail(shelf)).toBe('Seen 5 Nov');
    });

    it('strikes the old price only while it is higher than what is charged', () => {
      expect(wasText(entry({wasPrice: 1.49, offer: offer({eachPrice: 0.99})}))).toBe('€1.49');
      expect(wasText(entry({wasPrice: 0.99, offer: offer({eachPrice: 1.29})}))).toBeNull();
      expect(wasText(entry({wasPrice: null}))).toBeNull();
    });

    it('says plainly when a shop has no usable price, with a dash for the price', () => {
      const unread = entry({state: 'not_read', observedOn: null, price: null, offer: null});

      expect(rowTag(unread)).toBe('Not read yet');
      expect(tagVariant(unread)).toBe('warning');
      expect(priceText(unread)).toBe('—');
      expect(rowDetail(unread)).toBe('Nothing read');
      expect(rowTag(entry({state: 'stale'}))).toBe('Old price');
      expect(rowTag(entry({state: 'out_of_stock'}))).toBe('Out of stock');
      expect(rowTag(entry({state: 'upcoming', offer: null}))).toBe('Offer not started');
      expect(rowTag(entry({state: 'ended', offer: null}))).toBe('Offer ended');
      expect(rowTag(entry({state: 'no_price', offer: null}))).toBe('No price listed');
    });

    it('marks the best shop and leaves a plain priced shop without a tag', () => {
      expect(rowTag(entry({best: true}))).toBe('Best');
      expect(tagVariant(entry({best: true}))).toBe('success');
      expect(rowTag(entry())).toBeNull();
      expect(tagVariant(entry())).toBe('neutral');
    });

    it('lists the best shop first, then the other usable prices, then the rest, keeping the shop order inside each', () => {
      const item = line({
        stores: [
          entry({store: 'lidl', state: 'stale'}),
          entry({store: 'aldi'}),
          entry({store: 'tesco', best: true}),
          entry({store: 'dunnes', state: 'out_of_stock'}),
        ],
      });

      expect(orderedStores(item).map(s => s.store)).toEqual(['tesco', 'aldi', 'lidl', 'dunnes']);
    });
  });

  describe('flags', () => {
    it('gives every flag a short label and a sentence, naming the limit the server uses', () => {
      expect(flagText('unmatched', 3).tag).toBe('Not matched');
      expect(flagText('no_price', 3).tag).toBe('No price');
      expect(flagText('stale', 3)).toEqual({
        tag: 'Old prices',
        detail: 'Every price is over 3 days old.',
      });
      expect(flagText('out_of_stock', 3).tag).toBe('Out of stock');
      expect(flagText('not_comparable', 3).tag).toBe("Can't compare");
    });
  });

  describe('the answer on top', () => {
    const none: PlanCoverage = {
      singleStore: false,
      store: null,
      coveredBy: [],
      closestStore: null,
      blockers: [],
      split: null,
    };

    it('names the one shop that covers the list', () => {
      const answer = verdict({...none, singleStore: true, store: 'lidl', coveredBy: ['lidl']}, 3);

      expect(answer.status).toEqual({tone: 'success', label: 'One shop covers it'});
      expect(answer.pills).toEqual([{label: 'Lidl', tone: 'success'}]);
      expect(answer.caveat).toBe('Lidl has a fresh price for every line.');
    });

    it('names the best of several shops that cover it', () => {
      const answer = verdict(
        {...none, singleStore: true, store: 'lidl', coveredBy: ['lidl', 'tesco']},
        2
      );

      expect(answer.pills.map(p => p.label)).toEqual(['Lidl', '2 shops can']);
      expect(answer.caveat).toBe(
        'Lidl, Tesco all have a fresh price for every line. Lidl is cheapest for the most.'
      );
    });

    it('names the closest shop and how many lines block it', () => {
      const blockers = [
        {itemId: 'a', text: 'Bananas', reason: 'unmatched' as const},
        {itemId: 'b', text: 'Eggs', reason: 'no_price' as const},
      ];
      const answer = verdict({...none, closestStore: 'tesco', blockers}, 5);

      expect(answer.status).toEqual({tone: 'warning', label: 'No one shop covers it'});
      expect(answer.pills.map(p => p.label)).toEqual(['Closest: Tesco', '2 lines block it']);
      expect(answer.caveat).toBe('Tesco has a fresh price for 3 of 5 lines.');
    });

    it('proposes the cheapest two shops when no one shop covers the list', () => {
      const split: PlanPairSplit = {
        total: 3.9,
        oldestSeenOn: '2026-11-05',
        promoLines: 1,
        packsDiffer: 0,
        cards: ['Lidl Plus'],
        groups: [],
      };
      const groups = [
        {store: 'lidl' as const, storeName: 'Lidl', total: 0.99, lines: []},
        {store: 'aldi' as const, storeName: 'Aldi', total: 2.91, lines: []},
      ];
      const answer = verdict({...none, closestStore: 'lidl', split: {...split, groups}}, 2);

      expect(answer.status).toEqual({tone: 'warning', label: 'No one shop covers it'});
      expect(answer.pills.map(p => p.label)).toEqual([
        'Best two: Lidl + Aldi',
        '€3.90',
        'Closest: Lidl',
      ]);
      expect(answer.caveat).toBe(
        'Lidl and Aldi together have a fresh price for every line, €3.90 in all, the oldest seen 5 Nov.'
      );
    });

    it('warns when packs differ in the proposed pair', () => {
      const split: PlanPairSplit = {
        total: 3.9,
        oldestSeenOn: null,
        promoLines: 0,
        packsDiffer: 2,
        cards: [],
        groups: [
          {store: 'lidl', storeName: 'Lidl', total: 1.9, lines: []},
          {store: 'tesco', storeName: 'Tesco', total: 2, lines: []},
        ],
      };

      expect(verdict({...none, split}, 3).caveat).toBe(
        'Lidl and Tesco together have a fresh price for every line, €3.90 in all. Packs differ on 2 lines, so check the sizes.'
      );
    });

    it('says so when no shop prices any line', () => {
      const blockers = [{itemId: 'a', text: 'Bananas', reason: 'unmatched' as const}];
      const answer = verdict({...none, blockers}, 1);

      expect(answer.status.label).toBe('No shop prices it yet');
      expect(answer.pills.map(p => p.label)).toEqual(['1 line blocks it']);
    });
  });

  describe("this week's offer beside the last price paid", () => {
    const lidlOffer = entry({
      best: true,
      offer: offer({
        kind: 'loyalty',
        isPromo: true,
        deal: 'Lidl Plus price',
        card: 'Lidl Plus',
        eachPrice: 0.99,
        unitPrice: 0.5,
      }),
    });

    it('words a paid price the way the Shelf does, with its day', () => {
      expect(paidText(paid({price: 1.19, paidOn: '2026-11-03'}))).toBe('Last paid €1.19 on 3 Nov');
    });

    it('puts the paid price on a second line of the shop that has the offer', () => {
      const [row] = planRows(line({stores: [lidlOffer], lastPaid: [paid()]}));

      expect(rowLines(row)).toBe(
        'Seen 5 Nov · Lidl Plus price · €0.50/l\nLast paid €1.19 on 3 Nov'
      );
      expect(priceText(row.entry)).toBe('€0.99');
      expect(rowTag(row.entry)).toBe('Best');
    });

    it('leaves a shop it was never bought at with its reading alone', () => {
      const tesco = entry({store: 'tesco', storeName: 'Tesco'});
      const rows = planRows(line({stores: [lidlOffer, tesco], lastPaid: [paid()]}));

      expect(rows.map(r => r.paid?.store ?? null)).toEqual(['lidl', null]);
      expect(rowLines(rows[1])).toBe('Seen 5 Nov · €0.65/l');
    });

    it('has no rows for a line with neither a reading nor a paid price', () => {
      expect(planRows(line())).toEqual([]);
    });
  });

  describe('what blocks the closest shop', () => {
    it('uses the line flag when no shop can price it, the shop state otherwise', () => {
      const stale = line({flag: 'stale'});
      const priced = line({flag: null});

      expect(blockerTag({itemId: 'i1', text: 'x', reason: 'stale'}, stale, 3)).toBe('Old prices');
      expect(blockerTag({itemId: 'i1', text: 'x', reason: 'stale'}, priced, 3)).toBe('Old price');
      expect(blockerTag({itemId: 'i1', text: 'x', reason: 'unmatched'}, undefined, 3)).toBe(
        'Not matched'
      );
      expect(blockerTag({itemId: 'i1', text: 'x', reason: 'not_linked'}, priced, 3)).toBe(
        'No page there'
      );
      expect(blockerTag({itemId: 'i1', text: 'x', reason: 'not_read'}, priced, 3)).toBe(
        'Not read yet'
      );
    });
  });
});
