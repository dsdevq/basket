import {HttpClient} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {firstValueFrom} from 'rxjs';

import type {PriceHistory} from './price-history.model';
import type {
  Alias,
  AliasProposal,
  Candidate,
  Listing,
  PricesResponse,
  Product,
  StoreId,
} from './prices.model';

@Injectable({providedIn: 'root'})
export class PricesApi {
  private readonly http = inject(HttpClient);

  public all(): Promise<PricesResponse> {
    return firstValueFrom(this.http.get<PricesResponse>('/api/products'));
  }

  /** The last 90 days of readings of the product's page in one shop. */
  public history(id: string, store: StoreId): Promise<PriceHistory> {
    return firstValueFrom(
      this.http.get<PriceHistory>(`/api/products/${id}/stores/${store}/history`)
    );
  }

  public add(name: string): Promise<Product> {
    return firstValueFrom(this.http.post<Product>('/api/products', {name}));
  }

  /** Adds the product the catalog row is: named, barcoded and linked to the row's page in its shop by the server. */
  public addFromCatalog(catalogId: string): Promise<Product> {
    return firstValueFrom(this.http.post<Product>('/api/products', {catalogId}));
  }

  public remove(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/products/${id}`));
  }

  public addAlias(id: string, text: string): Promise<Alias> {
    return firstValueFrom(this.http.post<Alias>(`/api/products/${id}/aliases`, {text}));
  }

  public renameAlias(id: string, aliasId: string, text: string): Promise<Alias> {
    return firstValueFrom(this.http.patch<Alias>(`/api/products/${id}/aliases/${aliasId}`, {text}));
  }

  public removeAlias(id: string, aliasId: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/products/${id}/aliases/${aliasId}`));
  }

  public aliasProposals(): Promise<{proposals: readonly AliasProposal[]}> {
    return firstValueFrom(
      this.http.get<{proposals: readonly AliasProposal[]}>('/api/alias-proposals')
    );
  }

  public acceptAliasProposal(proposal: AliasProposal): Promise<Alias> {
    return firstValueFrom(
      this.http.post<Alias>('/api/alias-proposals/accept', {
        text: proposal.text,
        productId: proposal.productId,
      })
    );
  }

  public rejectAliasProposal(proposal: AliasProposal): Promise<void> {
    return firstValueFrom(
      this.http.post<void>('/api/alias-proposals/reject', {
        text: proposal.text,
        productId: proposal.productId,
      })
    );
  }

  public link(id: string, store: StoreId, url: string, imageUrl?: string): Promise<Listing> {
    return firstValueFrom(
      this.http.put<Listing>(`/api/products/${id}/stores/${store}`, {url, imageUrl})
    );
  }

  public unlink(id: string, store: StoreId): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/products/${id}/stores/${store}`));
  }

  public candidates(id: string): Promise<{candidates: readonly Candidate[]}> {
    return firstValueFrom(
      this.http.get<{candidates: readonly Candidate[]}>(`/api/products/${id}/candidates`)
    );
  }

  public confirm(id: string, candidateId: string): Promise<Listing> {
    return firstValueFrom(
      this.http.post<Listing>(`/api/products/${id}/candidates/${candidateId}/confirm`, {})
    );
  }

  public reject(id: string, candidateId: string): Promise<void> {
    return firstValueFrom(
      this.http.post<void>(`/api/products/${id}/candidates/${candidateId}/reject`, {})
    );
  }
}
