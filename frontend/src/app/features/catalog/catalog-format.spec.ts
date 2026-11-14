import type {Price} from '../prices/prices.model';
import type {CatalogComparison, CatalogItem} from './catalog.model';
import {catalogDetail, catalogPrice, unitPriceLabel, versusLabel} from './catalog-format';

const PRICE: Price = {
  observedOn: '2026-11-14',
  state: 'current',
  price: 1.49,
  wasPrice: null,
  loyaltyPrice: 1.29,
  loyaltyScheme: 'Clubcard',
  promoText: null,
  validFrom: null,
  validUntil: null,
  best: 1.29,
  inStock: true,
};

const ITEM: CatalogItem = {
  id: 'c1',
  store: 'tesco',
  storeName: 'Tesco',
  name: 'Whole Milk',
  brand: 'Dairy Farm',
  sizeText: '2 L',
  unitPrice: 0.75,
  unitPriceBasis: '1 L',
  imageUrl: null,
  url: 'https://www.tesco.ie/groceries/en-IE/products/100000001',
  price: PRICE,
  lastSeenAt: '2026-11-14T03:00:00+00:00',
};

describe('catalogPrice', () => {
  it('shows the best price today, and nothing when no price was read', () => {
    expect(catalogPrice(ITEM)).toBe('€1.29');
    expect(catalogPrice({...ITEM, price: null})).toBe('');
  });

  it('says what stands in for a price that is not usable today', () => {
    expect(catalogPrice({...ITEM, price: {...PRICE, inStock: false}})).toBe('Out of stock');
    expect(
      catalogPrice({...ITEM, price: {...PRICE, state: 'upcoming', validFrom: '2026-11-20'}})
    ).toBe('from 20 Nov');
    expect(catalogPrice({...ITEM, price: {...PRICE, state: 'ended'}})).toBe('ended');
    expect(catalogPrice({...ITEM, price: {...PRICE, state: 'unavailable'}})).toBe('no price');
  });
});

describe('catalogDetail', () => {
  it('joins brand, size and the price per unit', () => {
    expect(catalogDetail(ITEM)).toBe('Dairy Farm · 2 L · €0.75 / 1 L');
  });

  it('leaves out a brand the name already carries and facts the shop did not give', () => {
    expect(
      catalogDetail({
        ...ITEM,
        name: 'Dairy Farm Whole Milk',
        sizeText: null,
        unitPrice: null,
        unitPriceBasis: null,
      })
    ).toBe('');
  });
});

describe('unit price comparison wording', () => {
  const COMPARISON: CatalogComparison = {
    unit: 'l',
    pack: '2 l',
    unitPrice: 0.9,
    difference: -0.1,
    flags: [],
  };

  it('prints the unit price per unit, and nothing without one', () => {
    expect(unitPriceLabel(COMPARISON)).toBe('€0.90 / l');
    expect(unitPriceLabel({...COMPARISON, unitPrice: null})).toBe('');
    expect(unitPriceLabel({...COMPARISON, unit: null})).toBe('');
  });

  it('says cheaper, dearer or the same per unit, and nothing when not comparable', () => {
    expect(versusLabel(COMPARISON)).toBe('€0.10 / l cheaper');
    expect(versusLabel({...COMPARISON, difference: 0.2})).toBe('€0.20 / l dearer');
    expect(versusLabel({...COMPARISON, difference: 0})).toBe('Same unit price');
    expect(versusLabel({...COMPARISON, difference: null})).toBe('');
  });
});
