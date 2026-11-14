import {HttpClient} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {firstValueFrom} from 'rxjs';

import type {StoreId} from '../prices/prices.model';
import type {CatalogProduct, CatalogSearchResponse} from './catalog.model';

@Injectable({providedIn: 'root'})
export class CatalogApi {
  private readonly http = inject(HttpClient);

  /** Products whose name or brand match every word of `query` (each as a prefix), best match first, optionally in one shop. */
  public search(query: string, store?: StoreId): Promise<CatalogSearchResponse> {
    const params: Record<string, string> = {q: query};
    if (store) {
      params['store'] = store;
    }
    return firstValueFrom(this.http.get<CatalogSearchResponse>('/api/catalog/search', {params}));
  }

  public product(id: string): Promise<CatalogProduct> {
    return firstValueFrom(this.http.get<CatalogProduct>(`/api/catalog/${id}`));
  }
}
