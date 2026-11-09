import {HttpClient, HttpParams} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {firstValueFrom} from 'rxjs';

import type {ReloadResult, TripPage} from './history.model';

@Injectable({providedIn: 'root'})
export class HistoryApi {
  private readonly http = inject(HttpClient);

  /** Newest first. `before` is the `closedAt` of the last trip already on screen. */
  public trips(before?: string): Promise<TripPage> {
    const params = before ? new HttpParams().set('before', before) : undefined;
    return firstValueFrom(this.http.get<TripPage>('/api/trips', {params}));
  }

  public reload(id: string): Promise<ReloadResult> {
    return firstValueFrom(this.http.post<ReloadResult>(`/api/trips/${id}/reload`, {}));
  }
}
