import {HttpClient} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {firstValueFrom} from 'rxjs';

import type {PromotionsResponse} from './promotions.model';

@Injectable({providedIn: 'root'})
export class PromotionsApi {
  private readonly http = inject(HttpClient);

  public all(): Promise<PromotionsResponse> {
    return firstValueFrom(this.http.get<PromotionsResponse>('/api/promotions'));
  }
}
