import {HttpClient} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {firstValueFrom} from 'rxjs';

import type {Habit, HabitsResponse} from './habits.model';

@Injectable({providedIn: 'root'})
export class HabitsApi {
  private readonly http = inject(HttpClient);

  /** Most bought first. */
  public async all(): Promise<readonly Habit[]> {
    const response = await firstValueFrom(this.http.get<HabitsResponse>('/api/trips/habits'));
    return response.products;
  }
}
