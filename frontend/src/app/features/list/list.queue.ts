import type {StoreId} from '../prices/prices.model';

/**
 * A list edit made with no signal, kept until it can be sent. Edits are replayed in the order they were made.
 * An added line has a local id until the server answers; later edits to it carry that id and are re-pointed
 * at the real one when the add goes through.
 */
export type QueuedEdit =
  | {readonly kind: 'add'; readonly localId: string; readonly text: string}
  | {readonly kind: 'check'; readonly id: string; readonly checked: boolean}
  | {readonly kind: 'boughtAt'; readonly id: string; readonly store: StoreId}
  | {readonly kind: 'remove'; readonly id: string};

const LOCAL_PREFIX = 'local-';

let made = 0;

/** A fresh id for a line that only this phone knows so far. */
export function newLocalId(): string {
  made++;
  return `${LOCAL_PREFIX}${Date.now()}-${made}`;
}

/** The line an edit is about; for an add, the local id it will be known by until the server answers. */
export function targetOf(edit: QueuedEdit): string {
  return edit.kind === 'add' ? edit.localId : edit.id;
}

/** The same edit pointed at another line, for when a local id gets its real one. */
export function retarget(edit: QueuedEdit, from: string, to: string): QueuedEdit {
  return edit.kind !== 'add' && edit.id === from ? {...edit, id: to} : edit;
}
