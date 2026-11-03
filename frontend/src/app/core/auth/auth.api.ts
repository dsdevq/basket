import {HttpClient} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {firstValueFrom} from 'rxjs';

import type {CreatedInvite, Member, PendingInvite, SessionResponse} from './session.model';

/** The sign-in, registration and invite endpoints of the API, one method each. */
@Injectable({providedIn: 'root'})
export class AuthApi {
  private readonly http = inject(HttpClient);

  public session(): Promise<SessionResponse> {
    return firstValueFrom(this.http.get<SessionResponse>('/api/session'));
  }

  public checkInvite(token: string): Promise<{role: string; label: string | null}> {
    return firstValueFrom(
      this.http.post<{role: string; label: string | null}>('/api/auth/invite', {token})
    );
  }

  public registerOptions(token: string, displayName: string): Promise<unknown> {
    return firstValueFrom(
      this.http.post<unknown>('/api/auth/register/options', {token, displayName})
    );
  }

  public register(token: string, credentialJson: string): Promise<SessionResponse> {
    return firstValueFrom(
      this.http.post<SessionResponse>('/api/auth/register', {token, credentialJson})
    );
  }

  public signInOptions(): Promise<unknown> {
    return firstValueFrom(this.http.post<unknown>('/api/auth/sign-in/options', {}));
  }

  public signIn(credentialJson: string): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/auth/sign-in', {credentialJson}));
  }

  public signOut(): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/auth/sign-out', {}));
  }

  public members(): Promise<Member[]> {
    return firstValueFrom(this.http.get<Member[]>('/api/members'));
  }

  public pendingInvites(): Promise<PendingInvite[]> {
    return firstValueFrom(this.http.get<PendingInvite[]>('/api/invites'));
  }

  public createInvite(label: string): Promise<CreatedInvite> {
    return firstValueFrom(this.http.post<CreatedInvite>('/api/invites', {label}));
  }

  public revokeInvite(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/invites/${id}`));
  }
}
