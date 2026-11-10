import {HttpClient} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {firstValueFrom} from 'rxjs';

import type {ByShopResponse} from './by-shop.model';

@Injectable({providedIn: 'root'})
export class ByShopApi {
  private readonly http = inject(HttpClient);

  public get(): Promise<ByShopResponse> {
    return firstValueFrom(this.http.get<ByShopResponse>('/api/plan/by-shop'));
  }
}
