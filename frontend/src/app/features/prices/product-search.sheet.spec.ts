import {TestBed} from '@angular/core/testing';
import {CMN_DRAWER_DATA, CmnDrawerRef} from '@lifekit-hq/ui';

import {CatalogApi} from '../catalog/catalog.api';
import type {CatalogItem, CatalogSearchResponse} from '../catalog/catalog.model';
import {PricesStore} from './prices.store';
import {ProductSearchSheetComponent} from './product-search.sheet';

function item(overrides: Partial<CatalogItem>): CatalogItem {
  return {
    id: 'c1',
    store: 'lidl',
    storeName: 'Lidl',
    name: 'Oat Barista Drink',
    brand: 'Oatly',
    sizeText: '1 L',
    unitPrice: null,
    unitPriceBasis: null,
    imageUrl: 'https://images.example.test/oat.png',
    url: 'https://www.lidl.ie/p/oat-barista-drink/p00000001',
    price: {
      observedOn: '2026-11-14',
      state: 'current',
      price: 1.89,
      wasPrice: null,
      loyaltyPrice: null,
      loyaltyScheme: null,
      promoText: null,
      validFrom: null,
      validUntil: null,
      best: 1.89,
      inStock: true,
    },
    lastSeenAt: '2026-11-14T03:00:00+00:00',
    ...overrides,
  };
}

const FIRST = item({});
const SECOND = item({
  id: 'c2',
  store: 'tesco',
  storeName: 'Tesco',
  name: 'Oat Bars',
  brand: null,
  sizeText: null,
  imageUrl: null,
  price: null,
});

const RESPONSE: CatalogSearchResponse = {query: 'oat', store: null, items: [FIRST, SECOND]};

async function render(query: string, search: ReturnType<typeof vi.fn>, addFromCatalog = vi.fn()) {
  const ref = {close: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      {provide: CatalogApi, useValue: {search}},
      {provide: PricesStore, useValue: {adding: () => false, addFromCatalog}},
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

async function settle(fixture: {detectChanges(): void; whenStable(): Promise<unknown>}) {
  await new Promise(resolve => setTimeout(resolve, 0));
  await fixture.whenStable();
  fixture.detectChanges();
}

describe('ProductSearchSheetComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('searches the catalog for what was typed at once and shows shop, photo, detail and price', async () => {
    const search = vi.fn().mockResolvedValue(RESPONSE);
    const {host} = await render('oat', search);

    expect(search).toHaveBeenCalledWith('oat', undefined);
    expect(host.textContent).toContain('Oat Barista Drink');
    expect(host.textContent).toContain('Oatly · 1 L');
    expect(host.textContent).toContain('Lidl');
    expect(host.textContent).toContain('Tesco');
    expect(host.textContent).toContain('€1.89');
    expect(host.querySelector('img')?.getAttribute('src')).toBe(
      'https://images.example.test/oat.png?ngsw-bypass=true'
    );
    expect(host.textContent).toContain('O');
  });

  it('does not search until there is something to search for', async () => {
    const search = vi.fn();
    await render('', search);

    expect(search).not.toHaveBeenCalled();
  });

  it('narrows the search to a shop when one is picked', async () => {
    const search = vi.fn().mockResolvedValue(RESPONSE);
    const {fixture, host} = await render('oat', search);

    const tesco = Array.from(host.querySelectorAll<HTMLElement>('cmn-segmented button')).find(
      button => button.textContent?.trim() === 'Tesco'
    );
    tesco?.click();
    await settle(fixture);

    expect(search).toHaveBeenLastCalledWith('oat', 'tesco');
  });

  it('adds the tapped row and closes', async () => {
    const addFromCatalog = vi.fn().mockResolvedValue(true);
    const {fixture, ref, host} = await render(
      'oat',
      vi.fn().mockResolvedValue(RESPONSE),
      addFromCatalog
    );

    (host.querySelector('cmn-list-item-row button') as HTMLElement).click();
    await settle(fixture);

    expect(addFromCatalog).toHaveBeenCalledWith(FIRST);
    expect(ref.close).toHaveBeenCalledWith(true);
  });

  it('stays open when the product could not be added', async () => {
    const addFromCatalog = vi.fn().mockResolvedValue(false);
    const {fixture, ref, host} = await render(
      'oat',
      vi.fn().mockResolvedValue(RESPONSE),
      addFromCatalog
    );

    (host.querySelector('cmn-list-item-row button') as HTMLElement).click();
    await settle(fixture);

    expect(ref.close).not.toHaveBeenCalled();
  });

  it('says so, and points at paste-a-link, when nothing is found', async () => {
    const {host} = await render(
      'zzz',
      vi.fn().mockResolvedValue({query: 'zzz', store: null, items: []})
    );

    expect(host.textContent).toContain('Nothing found for “zzz”');
    expect(host.textContent).toContain('link its page from the Prices tab');
  });

  it('shows an error when the search request fails', async () => {
    const {host} = await render('oat', vi.fn().mockRejectedValue(new Error('offline')));

    expect(host.textContent).toContain('The search did not work');
  });
});
