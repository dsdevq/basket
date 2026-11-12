import {HttpErrorResponse} from '@angular/common/http';
import {TestBed} from '@angular/core/testing';
import {ToastService} from '@lifekit-hq/ui';

import {PricesApi} from './prices.api';
import type {Product} from './prices.model';
import {PricesStore} from './prices.store';

const PRODUCT: Product = {id: 'p1', name: 'Milk', stores: []};

function setup(api: Partial<PricesApi>) {
  const toast = {error: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      {provide: PricesApi, useValue: api},
      {provide: ToastService, useValue: toast},
    ],
  });
  return {store: TestBed.inject(PricesStore), toast};
}

describe('PricesStore', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('loads the products and the shops status', async () => {
    const {store} = setup({
      all: vi.fn().mockResolvedValue({
        products: [PRODUCT],
        stores: [{store: 'lidl', name: 'Lidl', readBy: 'nightly', lastRun: null}],
      }),
    });

    await store.load();

    expect(store.status()).toBe('ready');
    expect(store.products()).toEqual([PRODUCT]);
    expect(store.stores()).toHaveLength(1);
  });

  it('shows an error when the first load fails and keeps what is on screen when a refresh fails', async () => {
    const all = vi
      .fn()
      .mockRejectedValueOnce(new Error('offline'))
      .mockResolvedValueOnce({products: [PRODUCT], stores: []})
      .mockRejectedValueOnce(new Error('offline'));
    const {store, toast} = setup({all});

    await store.load();
    expect(store.status()).toBe('error');
    await store.load();
    await store.load();

    expect(store.status()).toBe('ready');
    expect(store.products()).toEqual([PRODUCT]);
    expect(toast.error).toHaveBeenCalledTimes(2);
  });

  it('says so when a product is already tracked', async () => {
    const add = vi.fn().mockRejectedValue(new HttpErrorResponse({status: 409}));
    const {store, toast} = setup({add});

    expect(await store.add('Milk')).toBe(false);

    expect(toast.error).toHaveBeenCalledWith('That product is already tracked.');
    expect(store.adding()).toBe(false);
  });

  it('removes a product at once and puts it back if the server refuses', async () => {
    const all = vi.fn().mockResolvedValue({products: [PRODUCT], stores: []});
    const remove = vi.fn().mockRejectedValue(new HttpErrorResponse({status: 500}));
    const {store, toast} = setup({all, remove});
    await store.load();

    await store.remove(PRODUCT);

    expect(store.products()).toEqual([PRODUCT]);
    expect(toast.error).toHaveBeenCalledTimes(1);
  });

  it('drops a refresh that was already in flight when a product was removed', async () => {
    const other: Product = {id: 'p2', name: 'Bread', stores: []};
    let resolveStale!: (value: unknown) => void;
    const all = vi
      .fn()
      .mockResolvedValueOnce({products: [PRODUCT, other], stores: []})
      .mockReturnValueOnce(new Promise(resolve => (resolveStale = resolve)));
    const {store} = setup({all, remove: vi.fn().mockResolvedValue(undefined)});
    await store.load();

    const pull = store.load();
    await store.remove(PRODUCT);
    resolveStale({products: [PRODUCT, other], stores: []});
    await pull;

    expect(store.products()).toEqual([other]);
  });

  it('keeps the newest load when an older one answers last', async () => {
    const other: Product = {id: 'p2', name: 'Bread', stores: []};
    let resolveOlder!: (value: unknown) => void;
    const all = vi
      .fn()
      .mockReturnValueOnce(new Promise(resolve => (resolveOlder = resolve)))
      .mockResolvedValueOnce({products: [PRODUCT, other], stores: []});
    const {store} = setup({all});

    const older = store.load();
    await store.load(true);
    resolveOlder({products: [PRODUCT], stores: []});
    await older;

    expect(store.products()).toEqual([PRODUCT, other]);
    expect(store.status()).toBe('ready');
  });

  it('hands back the server text for a link it refused, and reloads after one it accepted', async () => {
    const refused = new HttpErrorResponse({
      status: 400,
      error: {error: 'url_invalid', message: "Paste the address of the product's Lidl page."},
    });
    const link = vi.fn().mockRejectedValueOnce(refused).mockResolvedValueOnce({});
    const all = vi.fn().mockResolvedValue({products: [PRODUCT], stores: []});
    const {store} = setup({link, all});

    expect(await store.link(PRODUCT, 'lidl', 'nope')).toBe(
      "Paste the address of the product's Lidl page."
    );
    expect(all).not.toHaveBeenCalled();
    expect(
      await store.link(PRODUCT, 'lidl', 'https://www.lidl.ie/p/aubergine/p11214436')
    ).toBeNull();
    expect(all).toHaveBeenCalledTimes(1);
  });
});
