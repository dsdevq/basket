import {HttpClient} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {firstValueFrom} from 'rxjs';

import type {StoreId} from '../prices/prices.model';
import type {Card, CardFormat, CardSummary} from './cards.model';

@Injectable({providedIn: 'root'})
export class CardsApi {
  private readonly http = inject(HttpClient);

  public async list(): Promise<CardSummary[]> {
    const response = await firstValueFrom(this.http.get<{cards: CardSummary[]}>('/api/cards'));
    return response.cards;
  }

  public get(store: StoreId): Promise<Card> {
    return firstValueFrom(this.http.get<Card>(`/api/cards/${store}`));
  }

  /** Adds the shop's card, or replaces the one there. */
  public save(store: StoreId, number: string, format: CardFormat): Promise<Card> {
    return firstValueFrom(this.http.put<Card>(`/api/cards/${store}`, {number, format}));
  }

  public remove(store: StoreId): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/cards/${store}`));
  }
}
