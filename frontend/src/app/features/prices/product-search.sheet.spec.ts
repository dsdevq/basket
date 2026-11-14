import {TestBed} from '@angular/core/testing';
import {CMN_DRAWER_DATA, CmnDrawerRef} from '@lifekit-hq/ui';

import {PricesApi} from './prices.api';
import type {SearchResponse} from './prices.model';
import {PricesStore} from './prices.store';
import {ProductSearchSheetComponent} from './product-search.sheet';

const RESPONSE: SearchResponse = {
  results: [
    {
      store: 'lidl',
      url: 'https://www.lidl.ie/p/oatly-oat-barista-drink/p11407098',
      title: 'OATLY Oat Barista Drink',
      imageUrl: 'https://www.lidl.ie/assets/oat.png',
    },
    {
      store: 'lidl',
      url: 'https://www.lidl.ie/p/oat-bars/p11000002',
      title: 'Oat Bars',
      imageUrl: null,
    },
  ],
  stores: [
    {store: 'lidl', name: 'Lidl', searched: true, note: null},
    {
      store: 'tesco',
      name: 'Tesco',
      searched: false,
      note: 'Tesco cannot be searched from here. Paste a product link on the Prices tab.',
    },
  ],
};

async function render(query: string, search: ReturnType<typeof vi.fn>, addFromSearch = vi.fn()) {
  const ref = {close: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      {provide: PricesApi, useValue: {search}},
      {provide: PricesStore, useValue: {adding: () => false, addFromSearch}},
      {provide: CmnDrawerRef, useValue: ref},
      {provide: CMN_DRAWER_DATA, useValue: {query}},
    ],
  });
  const fixture = TestBed.createComponent(ProductSearchSheetComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return {ref, fixture, host: fixture.nativeElement as HTMLElement};
}

describe('ProductSearchSheetComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('searches what was typed at once and shows each result with its photo or initial', async () => {
    const search = vi.fn().mockResolvedValue(RESPONSE);
    const {host} = await render('oat', search);

    expect(search).toHaveBeenCalledWith('oat');
    expect(host.textContent).toContain('OATLY Oat Barista Drink');
    expect(host.querySelector('img')?.getAttribute('src')).toBe(
      'https://www.lidl.ie/assets/oat.png?ngsw-bypass=true'
    );
    expect(host.textContent).toContain('O');
    expect(host.textContent).toContain('Tesco cannot be searched from here');
  });

  it('does not search until there is something to search for', async () => {
    const search = vi.fn();
    await render('', search);

    expect(search).not.toHaveBeenCalled();
  });

  it('adds the tapped result and closes', async () => {
    const addFromSearch = vi.fn().mockResolvedValue(true);
    const {ref, host} = await render('oat', vi.fn().mockResolvedValue(RESPONSE), addFromSearch);

    (host.querySelector('cmn-selectable-card button') as HTMLElement).click();
    await new Promise(resolve => setTimeout(resolve, 0));

    expect(addFromSearch).toHaveBeenCalledWith(RESPONSE.results[0]);
    expect(ref.close).toHaveBeenCalledWith(true);
  });

  it('stays open when the product could not be added', async () => {
    const addFromSearch = vi.fn().mockResolvedValue(false);
    const {ref, host} = await render('oat', vi.fn().mockResolvedValue(RESPONSE), addFromSearch);

    (host.querySelector('cmn-selectable-card button') as HTMLElement).click();
    await new Promise(resolve => setTimeout(resolve, 0));

    expect(ref.close).not.toHaveBeenCalled();
  });

  it('shows the Lidl failure note when a search returns no results', async () => {
    const note =
      "Lidl's search page had no products, the page layout may have changed. Paste the product's link instead.";
    const {host} = await render(
      'oat',
      vi.fn().mockResolvedValue({
        results: [],
        stores: [{store: 'lidl', name: 'Lidl', searched: false, note}],
      })
    );

    expect(host.textContent).toContain(note);
  });

  it('shows an error when the search request fails', async () => {
    const {host} = await render('oat', vi.fn().mockRejectedValue(new Error('offline')));

    expect(host.textContent).toContain('The search did not work');
  });
});
