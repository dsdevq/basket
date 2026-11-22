import {HttpClient} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {firstValueFrom} from 'rxjs';

import type {StoreId} from '../prices/prices.model';
import type {ListItem} from './list.model';

/**
 * Edits skip the service worker: with no signal it answers 504 for them, and the list tells "no signal" (status 0, so the
 * edit is queued and replayed) from a server that refused. ngsw-bypass lets the browser's own failure through.
 */
const DIRECT = {headers: {'ngsw-bypass': 'true'}};

@Injectable({providedIn: 'root'})
export class ListApi {
  private readonly http = inject(HttpClient);

  public async items(): Promise<ListItem[]> {
    const response = await firstValueFrom(this.http.get<{items: ListItem[]}>('/api/list'));
    return response.items;
  }

  public add(text: string): Promise<ListItem> {
    return firstValueFrom(this.http.post<ListItem>('/api/list/items', {text}, DIRECT));
  }

  /** Adds a product picked on the Shelf; the server answers with the open line when it is already there. */
  public addProduct(productId: string, store: StoreId | null): Promise<ListItem> {
    return firstValueFrom(this.http.post<ListItem>('/api/list/items', {productId, store}, DIRECT));
  }

  public setChecked(id: string, checked: boolean): Promise<ListItem> {
    return firstValueFrom(this.http.patch<ListItem>(`/api/list/items/${id}`, {checked}, DIRECT));
  }

  /** Changes the shop a ticked item was bought at, when it was not the one recorded at the tick. */
  public setBoughtAt(id: string, store: StoreId): Promise<ListItem> {
    return firstValueFrom(this.http.patch<ListItem>(`/api/list/items/${id}/bought-at`, {store}, DIRECT));
  }

  /** Files the ticked items as a trip in the history and takes them off the list. */
  public close(): Promise<unknown> {
    return firstValueFrom(this.http.post('/api/list/close', {}, DIRECT));
  }

  public remove(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/list/items/${id}`, DIRECT));
  }
}
