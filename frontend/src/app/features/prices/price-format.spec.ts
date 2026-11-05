import {detail, euro, headline, lastPaidLine, shortDate, storeProblem} from './price-format';
import type {Price, ProductStore, StoreStatus} from './prices.model';

function price(over: Partial<Price> = {}): Price {
  return {
    observedOn: '2026-11-05',
    state: 'current',
    price: 2.29,
    wasPrice: null,
    loyaltyPrice: null,
    loyaltyScheme: null,
    promoText: null,
    validFrom: null,
    validUntil: null,
    best: 2.29,
    ...over,
  };
}

function entry(over: Partial<ProductStore> = {}): ProductStore {
  return {
    store: 'lidl',
    name: 'Lidl',
    readBy: 'nightly',
    listing: {url: 'https://www.lidl.ie/p/x/p11115738', title: 'X', checkedAt: null, error: null},
    price: price(),
    lastPaid: null,
    ...over,
  };
}

describe('price formatting', () => {
  it('writes dates and money the Irish way', () => {
    expect(shortDate('2026-11-05')).toBe('5 Nov');
    expect(shortDate(null)).toBe('');
    expect(euro(2)).toBe('€2.00');
    expect(euro(null)).toBe('');
  });

  it('puts the best price today on the right and says why it is there underneath', () => {
    const lidl = entry({
      price: price({
        price: 2.29,
        loyaltyPrice: 1.99,
        loyaltyScheme: 'Lidl Plus',
        best: 1.99,
        wasPrice: 2.49,
        promoText: 'Mix n Match',
        validUntil: '2026-11-11',
      }),
    });

    expect(headline(lidl)).toBe('€1.99');
    expect(detail(lidl)).toBe(
      'Lidl Plus €1.99, €2.29 without · was €2.49 · Mix n Match · until 11 Nov · seen 5 Nov'
    );
  });

  it('does not offer an upcoming or ended offer as the price', () => {
    const upcoming = entry({
      price: price({state: 'upcoming', best: null, validFrom: '2026-11-12'}),
    });
    const ended = entry({price: price({state: 'ended', best: null, validUntil: '2026-11-04'})});

    expect(headline(upcoming)).toBe('from 12 Nov');
    expect(detail(upcoming)).toBe('offer €2.29 starts 12 Nov · seen 5 Nov');
    expect(headline(ended)).toBe('ended');
    expect(detail(ended)).toBe('offer ended 4 Nov · seen 5 Nov');
  });

  it('says what is missing when there is no reading', () => {
    expect(headline(entry({listing: null, price: null}))).toBe('');
    expect(detail(entry({listing: null, price: null}))).toBe('No page linked yet');
    expect(headline(entry({price: null}))).toBe('—');
    expect(detail(entry({price: null}))).toContain('nightly pull');
    expect(
      detail(
        entry({
          price: null,
          listing: {
            url: 'u',
            title: null,
            checkedAt: null,
            error: 'www.tesco.ie answered HTTP 403',
          },
        })
      )
    ).toBe('Not read yet · www.tesco.ie answered HTTP 403');
    expect(headline(entry({price: price({state: 'unavailable', best: null, price: null})}))).toBe(
      'no price'
    );
  });

  it('says Tesco and Aldi rows wait for the PC until a reading arrives, never "unavailable"', () => {
    const waiting = entry({
      store: 'tesco',
      name: 'Tesco',
      readBy: 'pc',
      listing: null,
      price: null,
    });
    const linked = entry({store: 'aldi', name: 'Aldi', readBy: 'pc', price: null});

    expect(detail(waiting)).toBe('Waiting for the next PC read');
    expect(detail(linked)).toBe('Waiting for the next PC read');
    expect(headline(linked)).toBe('—');
    expect(headline(waiting)).toBe('');
    expect(detail({...linked, price: price()})).toBe('seen 5 Nov');
  });

  it('keeps the last known price visible when the latest read failed', () => {
    const failed = entry({
      listing: {url: 'u', title: null, checkedAt: null, error: 'page layout changed'},
    });

    expect(headline(failed)).toBe('€2.29');
    expect(detail(failed)).toBe('seen 5 Nov · last read failed: page layout changed');
  });

  it('shows the last paid price, or that it is not connected yet', () => {
    expect(lastPaidLine({state: 'not_connected', price: null, paidOn: null})).toBe(
      'Last paid: not yet connected'
    );
    expect(lastPaidLine({state: 'known', price: 1.99, paidOn: '2026-10-30'})).toBe(
      'Last paid €1.99 on 30 Oct'
    );
    expect(lastPaidLine(null)).toBe('');
  });

  it('warns about a shop only when its last pull did not fully work', () => {
    const status = (outcome: 'ok' | 'blocked' | 'failed' | 'partial'): StoreStatus => ({
      store: 'lidl',
      name: 'Lidl',
      readBy: 'nightly',
      lastRun: {finishedAt: '2026-11-05T03:00:00Z', outcome, listings: 2, read: 0, message: null},
    });

    expect(storeProblem(status('ok'))).toBe('');
    expect(storeProblem(status('blocked'))).toBe("Lidl blocks basket's automated requests");
    expect(storeProblem(status('failed'))).toBe('Lidl could not be read');
    expect(storeProblem(status('partial'))).toBe('Lidl some products could not be read');
    expect(storeProblem({store: 'lidl', name: 'Lidl', readBy: 'nightly', lastRun: null})).toBe('');
  });
});
