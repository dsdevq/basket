import {HttpClient} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {firstValueFrom} from 'rxjs';

import type {Listing, PricesResponse, Product, StoreId} from './prices.model';

@Injectable({providedIn: 'root'})
export class PricesApi {
  private readonly http = inject(HttpClient);

  public all(): Promise<PricesResponse> {
    return firstValueFrom(this.http.get<PricesResponse>('/api/products'));
  }

  public add(name: string): Promise<Product> {
    return firstValueFrom(this.http.post<Product>('/api/products', {name}));
  }

  public remove(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/products/${id}`));
  }

  public link(id: string, store: StoreId, url: string): Promise<Listing> {
    return firstValueFrom(this.http.put<Listing>(`/api/products/${id}/stores/${store}`, {url}));
  }

  public unlink(id: string, store: StoreId): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/products/${id}/stores/${store}`));
  }
}
