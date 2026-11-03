import {HttpClient} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {firstValueFrom} from 'rxjs';

import type {ListItem} from './list.model';

@Injectable({providedIn: 'root'})
export class ListApi {
  private readonly http = inject(HttpClient);

  public async items(): Promise<ListItem[]> {
    const response = await firstValueFrom(this.http.get<{items: ListItem[]}>('/api/list'));
    return response.items;
  }

  public add(text: string): Promise<ListItem> {
    return firstValueFrom(this.http.post<ListItem>('/api/list/items', {text}));
  }

  public setChecked(id: string, checked: boolean): Promise<ListItem> {
    return firstValueFrom(this.http.patch<ListItem>(`/api/list/items/${id}`, {checked}));
  }

  public remove(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/list/items/${id}`));
  }
}
