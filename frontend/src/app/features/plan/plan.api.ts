import {HttpClient} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {firstValueFrom} from 'rxjs';

import type {PlanResponse} from './plan.model';

@Injectable({providedIn: 'root'})
export class PlanApi {
  private readonly http = inject(HttpClient);

  public get(): Promise<PlanResponse> {
    return firstValueFrom(this.http.get<PlanResponse>('/api/plan'));
  }
}
