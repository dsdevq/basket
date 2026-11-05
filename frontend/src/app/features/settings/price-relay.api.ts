import {HttpClient} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {firstValueFrom} from 'rxjs';

import type {CreatedIngestToken, IngestToken} from '../../core/auth/session.model';

/** The owner's side of the price relay: issue, list and revoke the tokens the PC reader posts with. */
@Injectable({providedIn: 'root'})
export class PriceRelayApi {
  private readonly http = inject(HttpClient);

  public tokens(): Promise<IngestToken[]> {
    return firstValueFrom(this.http.get<IngestToken[]>('/api/ingest-tokens'));
  }

  public createToken(label: string): Promise<CreatedIngestToken> {
    return firstValueFrom(this.http.post<CreatedIngestToken>('/api/ingest-tokens', {label}));
  }

  public revokeToken(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/ingest-tokens/${encodeURIComponent(id)}`));
  }
}
