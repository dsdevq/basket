import {TestBed} from '@angular/core/testing';
import {CMN_DRAWER_DATA, CmnDrawerRef} from '@lifekit-hq/ui';

import {MatchSheetComponent, shortUrl} from './match.sheet';
import type {Candidate, Product, ProductStore} from './prices.model';
import {PricesStore} from './prices.store';

const ENTRY: ProductStore = {
  store: 'lidl',
  name: 'Lidl',
  readBy: 'nightly',
  listing: null,
  price: null,
  lastPaid: null,
  candidates: 2,
};
const PRODUCT: Product = {id: 'p1', name: 'Whole milk 2L', stores: [ENTRY]};
const FIRST: Candidate = {
  id: 'c1',
  store: 'lidl',
  url: 'https://www.lidl.ie/p/milbona-fresh-whole-milk-2-l/p10018800',
  title: 'Milbona fresh whole milk 2 litres',
  barcode: null,
  basis: 'name_size',
  score: 70,
};
const SECOND: Candidate = {
  ...FIRST,
  id: 'c2',
  title: null,
  basis: 'name',
  url: 'https://www.lidl.ie/p/x/p2',
};

async function render(prices: Record<string, unknown>) {
  const ref = {close: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      {provide: PricesStore, useValue: prices},
      {provide: CmnDrawerRef, useValue: ref},
      {provide: CMN_DRAWER_DATA, useValue: {product: PRODUCT, entry: ENTRY}},
    ],
  });
  const fixture = TestBed.createComponent(MatchSheetComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const press = async (text: string): Promise<void> => {
    Array.from(host.querySelectorAll('button'))
      .find(b => b.textContent?.includes(text))
      ?.click();
    await fixture.whenStable();
    fixture.detectChanges();
  };
  return {ref, host, press};
}

describe('MatchSheetComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('shows the likeliest suggestion with why it was suggested and a link to the shop page', async () => {
    const {host} = await render({candidates: vi.fn().mockResolvedValue([FIRST, SECOND])});

    expect(host.textContent).toContain('Milbona fresh whole milk 2 litres');
    expect(host.textContent).toContain('Same name and size');
    expect(host.textContent).toContain('2 suggestions to check at Lidl');
    expect(host.querySelector('a')?.getAttribute('href')).toBe(FIRST.url);
  });

  it("says a suggestion from another shop's own label is the same kind and size, not the same name", async () => {
    const {host} = await render({
      candidates: vi
        .fn()
        .mockResolvedValue([{...FIRST, basis: 'generic', title: 'Milbona whole milk 2 litres'}]),
    });

    expect(host.textContent).toContain('Same kind and size, other label');
    expect(host.textContent).not.toContain('Same name and size');
  });

  it('asks the store for this shop only', async () => {
    const candidates = vi.fn().mockResolvedValue([FIRST]);
    await render({candidates});

    expect(candidates).toHaveBeenCalledWith(PRODUCT, 'lidl');
  });

  it('closes after a yes, because the page is linked and the other suggestions are settled', async () => {
    const confirm = vi.fn().mockResolvedValue(true);
    const {ref, press} = await render({
      candidates: vi.fn().mockResolvedValue([FIRST, SECOND]),
      confirm,
    });

    await press('Yes, same product');

    expect(confirm).toHaveBeenCalledWith(PRODUCT, FIRST);
    expect(ref.close).toHaveBeenCalledWith();
  });

  it('moves to the next suggestion after a no, and says so when none is left', async () => {
    const reject = vi.fn().mockResolvedValue(true);
    const {ref, host, press} = await render({
      candidates: vi.fn().mockResolvedValue([FIRST, SECOND]),
      reject,
    });

    await press('No, not this one');

    expect(reject).toHaveBeenCalledWith(PRODUCT, FIRST);
    expect(host.textContent).toContain('1 suggestion to check');
    expect(host.textContent).toContain('lidl.ie/p/x/p2');
    expect(ref.close).not.toHaveBeenCalled();

    await press('No, not this one');

    expect(host.textContent).toContain('Nothing left to check');
  });

  it('keeps the suggestion when the answer could not be saved', async () => {
    const reject = vi.fn().mockResolvedValue(false);
    const {host, press} = await render({candidates: vi.fn().mockResolvedValue([FIRST]), reject});

    await press('No, not this one');

    expect(host.textContent).toContain('Milbona fresh whole milk 2 litres');
  });

  it('keeps the sheet open and re-reads the suggestions when a yes could not be saved', async () => {
    const confirm = vi.fn().mockResolvedValue(false);
    const candidates = vi.fn().mockResolvedValueOnce([FIRST]).mockResolvedValueOnce([]);
    const {ref, host, press} = await render({candidates, confirm});

    await press('Yes, same product');

    expect(ref.close).not.toHaveBeenCalled();
    expect(candidates).toHaveBeenCalledTimes(2);
    expect(host.textContent).toContain('Nothing left to check');
  });

  it('offers to paste the address instead', async () => {
    const {ref, press} = await render({candidates: vi.fn().mockResolvedValue([FIRST])});

    await press('Paste the page address instead');

    expect(ref.close).toHaveBeenCalledWith('paste');
  });

  it('closes when the suggestions cannot be loaded', async () => {
    const {ref} = await render({candidates: vi.fn().mockResolvedValue(null)});

    expect(ref.close).toHaveBeenCalledWith();
  });
});

describe('shortUrl', () => {
  it('drops the scheme, www and a trailing slash', () => {
    expect(shortUrl('https://www.lidl.ie/p/milk/p1/')).toBe('lidl.ie/p/milk/p1');
    expect(shortUrl('not a url')).toBe('not a url');
  });
});
