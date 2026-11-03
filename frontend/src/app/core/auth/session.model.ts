export type Role = 'owner' | 'member';

export interface SessionUser {
  readonly id: string;
  readonly displayName: string;
  readonly role: Role;
}

export interface SessionResponse {
  readonly authenticated: boolean;
  readonly user: SessionUser | null;
}

export interface Member {
  readonly id: string;
  readonly displayName: string;
  readonly role: Role;
  readonly createdAt: string;
}

export interface PendingInvite {
  readonly id: string;
  readonly label: string | null;
  readonly createdAt: string;
  readonly expiresAt: string;
}

export interface CreatedInvite {
  readonly id: string;
  readonly label: string | null;
  readonly expiresAt: string;
  readonly token: string;
  readonly link: string;
}
