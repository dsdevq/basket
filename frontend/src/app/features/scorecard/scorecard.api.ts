import {HttpClient} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {firstValueFrom} from 'rxjs';

import type {ScorecardResponse} from './scorecard.model';

@Injectable({providedIn: 'root'})
export class ScorecardApi {
  private readonly http = inject(HttpClient);

  public get(): Promise<ScorecardResponse> {
    return firstValueFrom(this.http.get<ScorecardResponse>('/api/scorecard'));
  }
}
