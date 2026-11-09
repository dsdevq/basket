import {
  ChangeDetectionStrategy,
  Component,
  effect,
  ElementRef,
  inject,
  Injectable,
  input,
  output,
  viewChild,
} from '@angular/core';

import type {CardFormat} from './cards.model';

/** bwip-js's names for the three codes basket draws. */
const SYMBOLOGY: Readonly<Record<CardFormat, string>> = {
  code128: 'code128',
  ean13: 'ean13',
  qr: 'qrcode',
};

/** Pixels per module: large enough that a phone scales the picture down, never up, so the edges stay sharp. */
const SCALE: Readonly<Record<CardFormat, number>> = {code128: 4, ean13: 4, qr: 10};

/** Bar height in millimetres (bwip-js units) for the linear codes: tall, so a till scanner finds it at any angle. */
const BAR_HEIGHT = 30;

/**
 * Draws a card number as a code on a canvas, with its own white background and quiet zone so it scans the same in the
 * light and the dark theme. bwip-js is loaded on first use, not at start-up: its encoders are large and only the card
 * screen needs them. The number never leaves the page.
 */
@Injectable({providedIn: 'root'})
export class BarcodeRenderer {
  /** Throws when the text cannot be drawn as `format` (a bad length or check digit). */
  public async draw(canvas: HTMLCanvasElement, text: string, format: CardFormat): Promise<void> {
    const bwip = await import('bwip-js/browser');
    bwip.toCanvas(canvas, {
      bcid: SYMBOLOGY[format],
      text,
      scale: SCALE[format],
      backgroundcolor: 'ffffff',
      barcolor: '000000',
      paddingwidth: 8,
      paddingheight: 8,
      ...(format === 'qr' ? {eclevel: 'M'} : {height: BAR_HEIGHT}),
    });
  }
}

/** The picture of a card's code, drawn by {@link BarcodeRenderer}. */
@Component({
  selector: 'bk-barcode',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<canvas
    #canvas
    [attr.aria-label]="label()"
    class="block h-auto w-full rounded-cmn-md"
    role="img"
  ></canvas>`,
})
export class BarcodeComponent {
  private readonly renderer = inject(BarcodeRenderer);
  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');

  public readonly value = input.required<string>();
  public readonly format = input.required<CardFormat>();
  /** Names the picture for assistive tech without reading the number out. */
  public readonly label = input('Barcode');

  /** Emits when the code could not be drawn (the number does not fit the format). */
  public readonly failed = output();

  constructor() {
    effect(() => {
      void this.draw(this.canvas().nativeElement, this.value(), this.format());
    });
  }

  private async draw(canvas: HTMLCanvasElement, value: string, format: CardFormat): Promise<void> {
    try {
      await this.renderer.draw(canvas, value, format);
    } catch {
      this.failed.emit();
    }
  }
}
