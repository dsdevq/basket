import {HttpClient} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {firstValueFrom} from 'rxjs';

import type {LidlCoupons} from './lidl-coupons.model';

const URL = '/api/lidl-plus/coupons';

/** The owner's Lidl Plus coupons: list them, switch one on, switch them all on. */
@Injectable({providedIn: 'root'})
export class LidlCouponsApi {
  private readonly http = inject(HttpClient);

  public list(): Promise<LidlCoupons> {
    return firstValueFrom(this.http.get<LidlCoupons>(URL));
  }

  public activate(id: string): Promise<LidlCoupons> {
    return firstValueFrom(
      this.http.post<LidlCoupons>(`${URL}/${encodeURIComponent(id)}/activate`, null)
    );
  }

  public activateAll(): Promise<LidlCoupons> {
    return firstValueFrom(this.http.post<LidlCoupons>(`${URL}/activate`, null));
  }
}
