/** Where the Lidl Plus connection stands. Only `connected` means basket can read Lidl Plus. */
export type LidlPlusState = 'not_connected' | 'connected' | 'needs_new_token' | 'unreadable';

/** The answer of every Lidl Plus route. It never carries the token. */
export interface LidlPlusStatus {
  readonly state: LidlPlusState;
  readonly connectedAt: string | null;
  readonly verifiedAt: string | null;
}
