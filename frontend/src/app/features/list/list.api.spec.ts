import {provideHttpClient} from '@angular/common/http';
import {HttpTestingController, provideHttpClientTesting} from '@angular/common/http/testing';
import {TestBed} from '@angular/core/testing';

import {ListApi} from './list.api';

// With no signal the service worker answers an edit with a 504 of its own. The list queues an edit only on a failure with
// status 0, so every edit must tell the worker to stay out of it (`ngsw-bypass`) and let the browser's own failure through.
describe('ListApi edits', () => {
  let api: ListApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({providers: [provideHttpClient(), provideHttpClientTesting()]});
    api = TestBed.inject(ListApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  const edits: [string, string, string, () => Promise<unknown>][] = [
    ['add', 'POST', '/api/list/items', () => api.add('Milk')],
    ['addProduct', 'POST', '/api/list/items', () => api.addProduct('p1', 'lidl')],
    ['setChecked', 'PATCH', '/api/list/items/a', () => api.setChecked('a', true)],
    ['setBoughtAt', 'PATCH', '/api/list/items/a/bought-at', () => api.setBoughtAt('a', 'aldi')],
    ['close', 'POST', '/api/list/close', () => api.close()],
    ['remove', 'DELETE', '/api/list/items/a', () => api.remove('a')],
  ];

  it.each(edits)('%s skips the service worker', async (_name, method, url, call) => {
    const sent = call();
    const request = http.expectOne(url);
    expect(request.request.method).toBe(method);
    expect(request.request.headers.get('ngsw-bypass')).toBe('true');
    request.flush({});
    await sent;
  });
});
