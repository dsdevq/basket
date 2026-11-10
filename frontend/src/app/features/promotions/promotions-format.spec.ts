import {gap, offer, product} from './promotions-fixtures';
import {
  byStore,
  gapText,
  headline,
  offerLines,
  placeText,
  unitPriceText,
} from './promotions-format';

describe('promotion formatting', () => {
  it('prints the unit price the way a shelf label does', () => {
    expect(unitPriceText(offer({unitPrice: 4.92, unit: 'kg'}))).toBe('€4.92/kg');
    expect(unitPriceText(offer({unitPrice: 1.01, unit: 'l'}))).toBe('€1.01/l');
    expect(unitPriceText(offer({unitPrice: 0.3, unit: 'each'}))).toBe('€0.30 each');
  });

  it('falls back to the pack price when the size is unknown, never a guessed unit price', () => {
    const noSize = offer({unitPrice: null, unit: null, packSize: null, flags: ['no_size']});

    expect(unitPriceText(noSize)).toBe('');
    expect(headline(noSize)).toBe('€2.00 a pack');
  });

  it('shows a multi-buy as the working: how many, the total, each, the pack, and what it was', () => {
    const lines = offerLines(offer());

    expect(lines[0]).toBe('2 for €4 · saves 9%');
    expect(lines[1]).toBe('buy 2 for €4.00 · €2.00 a pack · 500 g pack · was €4.38/kg');
    expect(lines[2]).toBe('Seen 6 Nov · until 11 Nov');
  });

  it('says a card is needed on a loyalty price', () => {
    const lines = offerLines(
      offer({
        kind: 'loyalty',
        deal: 'Lidl Plus price',
        card: 'Lidl Plus',
        buyQuantity: 1,
        payTotal: 0.99,
        eachPrice: 0.99,
        savingPercent: 34,
        referenceUnitPrice: null,
      })
    );

    expect(lines[0]).toBe('Lidl Plus price · needs Lidl Plus · saves 34%');
    expect(lines[1]).toBe('€0.99 a pack · 500 g pack');
  });

  it('flags an old, out of stock or sizeless reading in words instead of ranking it', () => {
    const stale = offerLines(offer({rank: null, flags: ['stale', 'out_of_stock']}))[2];

    expect(stale).toContain('Old reading, not ranked');
    expect(stale).toContain('Out of stock, not ranked');
    expect(offerLines(offer({flags: ['size_from_name']}))[2]).toContain(
      'Size taken from the product name'
    );
    expect(offerLines(offer({flags: ['other_unit']}))[2]).toContain('different unit');
  });

  it('names the place only among two or more comparable shops', () => {
    const p = product({ranked: 3});

    expect(placeText(offer({rank: 1}), p)).toBe('Best of 3');
    expect(placeText(offer({rank: 2}), p)).toBe('#2 of 3');
    expect(placeText(offer({rank: null}), p)).toBe('');
    expect(placeText(offer({rank: 1}), product({ranked: 1}))).toBe('');
  });

  it('explains a shop with nothing usable, with its date', () => {
    expect(gapText(gap())).toBe('No price read yet');
    expect(gapText(gap({reason: 'no_price'}))).toBe('No price listed');
    expect(gapText(gap({reason: 'upcoming', date: '2026-11-12'}))).toBe('Offer starts 12 Nov');
    expect(gapText(gap({reason: 'ended', date: '2026-11-05'}))).toBe('Offer ended 5 Nov');
  });

  it('groups the promos shop by shop and leaves the ordinary shelf prices out', () => {
    const groups = byStore([
      product({
        id: 'a',
        name: 'Milk',
        offers: [
          offer({store: 'tesco', storeName: 'Tesco'}),
          offer({store: 'aldi', storeName: 'Aldi', isPromo: false, kind: 'regular'}),
        ],
      }),
      product({id: 'b', name: 'Rice', offers: [offer({store: 'tesco', storeName: 'Tesco'})]}),
    ]);

    expect(groups.map(g => g.name)).toEqual(['Tesco']);
    expect(groups[0]?.entries.map(e => e.product.name)).toEqual(['Milk', 'Rice']);
  });
});
