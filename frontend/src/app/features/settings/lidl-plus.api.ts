import {HttpClient} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {firstValueFrom} from 'rxjs';

import type {LidlPlusStatus} from './lidl-plus.model';

const URL = '/api/lidl-plus/connection';

/** The owner's side of the Lidl Plus connection: paste a token once, see whether it works, check it, remove it. */
@Injectable({providedIn: 'root'})
export class LidlPlusApi {
  private readonly http = inject(HttpClient);

  public status(): Promise<LidlPlusStatus> {
    return firstValueFrom(this.http.get<LidlPlusStatus>(URL));
  }

  /** Lidl checks the token before basket keeps it. The token goes in the body, never in the address. */
  public connect(refreshToken: string): Promise<LidlPlusStatus> {
    return firstValueFrom(this.http.put<LidlPlusStatus>(URL, {refreshToken}));
  }

  /** Asks Lidl again with the saved token. */
  public check(): Promise<LidlPlusStatus> {
    return firstValueFrom(this.http.post<LidlPlusStatus>(`${URL}/check`, null));
  }

  public disconnect(): Promise<void> {
    return firstValueFrom(this.http.delete<void>(URL));
  }
}
