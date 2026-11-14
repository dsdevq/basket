import {provideHttpClient} from '@angular/common/http';
import {HttpTestingController, provideHttpClientTesting} from '@angular/common/http/testing';
import {TestBed} from '@angular/core/testing';

import {CatalogApi} from './catalog.api';
import type {CatalogProduct, CatalogSearchResponse} from './catalog.model';

const ITEM: CatalogSearchResponse['items'][number] = {
  id: '0b6f1f0e-0000-4000-8000-000000000001',
  store: 'lidl',
  storeName: 'Lidl',
  name: 'Dark Chocolate Bar',
  brand: 'Cocoa Farm',
  sizeText: '100 g',
  unitPrice: 14.9,
  unitPriceBasis: '1 KG',
  imageUrl: 'https://images.example.test/dark-bar.jpg',
  url: 'https://www.lidl.ie/p/dark-chocolate-bar/p00000001',
  price: {
    observedOn: '2026-11-14',
    state: 'current',
    price: 1.49,
    wasPrice: null,
    loyaltyPrice: null,
    loyaltyScheme: null,
    promoText: null,
    validFrom: null,
    validUntil: null,
    best: 1.49,
    inStock: true,
  },
  lastSeenAt: '2026-11-14T03:00:00+00:00',
};

function setup(): {api: CatalogApi; http: HttpTestingController} {
  TestBed.configureTestingModule({providers: [provideHttpClient(), provideHttpClientTesting()]});
  return {api: TestBed.inject(CatalogApi), http: TestBed.inject(HttpTestingController)};
}

describe('CatalogApi', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
  });

  it('searches every shop when none is named', async () => {
    const {api, http} = setup();

    const result = api.search('choc');
    const request = http.expectOne(r => r.url === '/api/catalog/search');
    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('q')).toBe('choc');
    expect(request.request.params.has('store')).toBe(false);
    const body: CatalogSearchResponse = {query: 'choc', store: null, items: [ITEM]};
    request.flush(body);

    await expect(result).resolves.toEqual(body);
  });

  it('narrows the search to one shop', async () => {
    const {api, http} = setup();

    const result = api.search('milk', 'tesco');
    const request = http.expectOne(r => r.url === '/api/catalog/search');
    expect(request.request.params.get('q')).toBe('milk');
    expect(request.request.params.get('store')).toBe('tesco');
    request.flush({query: 'milk', store: 'tesco', items: []});

    await expect(result).resolves.toEqual({query: 'milk', store: 'tesco', items: []});
  });

  it('reads one product by its id', async () => {
    const {api, http} = setup();
    const product: CatalogProduct = {
      ...ITEM,
      sizeAmount: 100,
      sizeUnit: 'g',
      sizePacks: 1,
      barcode: '05012345678900',
      category: 'Treats > Chocolate',
      firstSeenAt: '2026-10-15T03:00:00+00:00',
      discontinuedAt: null,
    };

    const result = api.product(ITEM.id);
    const request = http.expectOne(`/api/catalog/${ITEM.id}`);
    expect(request.request.method).toBe('GET');
    request.flush(product);

    await expect(result).resolves.toEqual(product);
  });
});
