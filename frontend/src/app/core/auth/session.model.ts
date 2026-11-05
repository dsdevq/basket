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

export interface Passkey {
  readonly id: string;
  readonly name: string;
  readonly createdAt: string;
  readonly backedUp: boolean;
}

/** A credential for the PC-side price reader. The raw token is never listed; only its label and date. */
export interface IngestToken {
  readonly id: string;
  readonly label: string | null;
  readonly createdAt: string;
}

/** The one answer that carries the raw token: shown once, then only its hash exists. */
export interface CreatedIngestToken {
  readonly id: string;
  readonly label: string | null;
  readonly createdAt: string;
  readonly token: string;
}
