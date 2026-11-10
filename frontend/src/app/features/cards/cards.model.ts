import type {StoreId} from '../prices/prices.model';

/** How a card is drawn as a code: the linear barcode shops print, and the QR code Lidl Plus shows. */
export type CardFormat = 'code128' | 'qr';

/** A card in the list. The number is hidden down to its last four characters. */
export interface CardSummary {
  readonly store: StoreId;
  readonly name: string;
  readonly format: CardFormat;
  readonly masked: string;
  /** False when the stored number can no longer be decrypted (the server lost its keys): add the card again. */
  readonly readable: boolean;
  readonly updatedAt: string;
}

/** One card with its number, for drawing the code. */
export interface Card {
  readonly store: StoreId;
  readonly name: string;
  readonly format: CardFormat;
  readonly number: string | null;
  readonly updatedAt: string;
}

export interface Chain {
  readonly store: StoreId;
  readonly name: string;
  /** The code the shop's own card or app shows, preselected when the card is added. */
  readonly defaultFormat: CardFormat;
  readonly hint: string;
}

/** The four shops, in the order the household uses them. Matches the server's list of shops. */
export const CHAINS: readonly Chain[] = [
  {
    store: 'lidl',
    name: 'Lidl',
    defaultFormat: 'qr',
    hint: 'The text behind the QR code in the Lidl Plus app',
  },
  {store: 'aldi', name: 'Aldi', defaultFormat: 'code128', hint: 'The number on the card'},
  {store: 'tesco', name: 'Tesco', defaultFormat: 'code128', hint: 'The Clubcard number'},
  {store: 'dunnes', name: 'Dunnes', defaultFormat: 'code128', hint: 'The ValueClub card number'},
];

export const FORMAT_LABELS: Readonly<Record<CardFormat, string>> = {
  code128: 'Code 128',
  qr: 'QR code',
};

/** Digits in groups of four, as printed on cards; anything else (a QR text) as it is. */
export function groupedNumber(number: string): string {
  return /^\d+$/.test(number) ? (number.match(/.{1,4}/g) ?? []).join(' ') : number;
}
