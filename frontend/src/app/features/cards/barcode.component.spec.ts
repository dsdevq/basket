import {TestBed} from '@angular/core/testing';

import {BarcodeComponent, BarcodeRenderer} from './barcode.component';
import type {CardFormat} from './cards.model';

const draw = vi.fn();

async function render(value: string, format: CardFormat) {
  TestBed.configureTestingModule({providers: [{provide: BarcodeRenderer, useValue: {draw}}]});
  const fixture = TestBed.createComponent(BarcodeComponent);
  const failed = vi.fn();
  fixture.componentRef.setInput('value', value);
  fixture.componentRef.setInput('format', format);
  fixture.componentInstance.failed.subscribe(failed);
  fixture.detectChanges();
  await new Promise(resolve => setTimeout(resolve, 0));
  return {fixture, failed, host: fixture.nativeElement as HTMLElement};
}

describe('BarcodeComponent', () => {
  beforeEach(() => {
    draw.mockReset();
  });

  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('draws the number in its format on its own canvas', async () => {
    const {host} = await render('4006381333931', 'ean13');

    expect(draw).toHaveBeenCalledTimes(1);
    expect(draw).toHaveBeenCalledWith(host.querySelector('canvas'), '4006381333931', 'ean13');
  });

  it('names the picture without reading the number out', async () => {
    const {fixture, host} = await render('4006381333931', 'ean13');
    fixture.componentRef.setInput('label', 'Tesco card EAN-13');
    fixture.detectChanges();

    const canvas = host.querySelector('canvas');
    expect(canvas?.getAttribute('role')).toBe('img');
    expect(canvas?.getAttribute('aria-label')).toBe('Tesco card EAN-13');
  });

  it('redraws when the number changes', async () => {
    const {fixture} = await render('111122', 'code128');

    fixture.componentRef.setInput('value', '333344');
    fixture.detectChanges();
    await new Promise(resolve => setTimeout(resolve, 0));

    expect(draw).toHaveBeenCalledTimes(2);
    expect(draw.mock.calls[1][1]).toBe('333344');
  });

  it('says so when the number does not fit the code', async () => {
    draw.mockRejectedValue(new Error('bwipp.ean13badLength'));

    const {failed} = await render('123', 'ean13');

    expect(failed).toHaveBeenCalledOnce();
  });
});

describe('BarcodeRenderer', () => {
  /** jsdom has no canvas; a stub context that holds pixels in memory lets bwip-js run its real encoders. */
  function canvasWithStubContext(): HTMLCanvasElement {
    const canvas = document.createElement('canvas');
    const context = {
      canvas,
      setTransform: () => undefined,
      fillRect: () => undefined,
      clearRect: () => undefined,
      getImageData: (_x: number, _y: number, width: number, height: number) => ({
        width,
        height,
        data: new Uint8ClampedArray(width * height * 4),
      }),
      putImageData: () => undefined,
    };
    vi.spyOn(canvas, 'getContext').mockReturnValue(context as unknown as CanvasRenderingContext2D);
    return canvas;
  }

  const renderer = new BarcodeRenderer();

  it.each([
    ['code128', '634004012345678923'],
    ['ean13', '4006381333931'],
    ['qr', 'LIDLPLUS-0001'],
  ] as const)('draws a %s code', async (format, text) => {
    const canvas = canvasWithStubContext();

    await renderer.draw(canvas, text, format);

    expect(canvas.width).toBeGreaterThan(0);
    expect(canvas.height).toBeGreaterThan(0);
  });

  it('refuses an EAN-13 number of the wrong length', async () => {
    await expect(renderer.draw(canvasWithStubContext(), '4006381', 'ean13')).rejects.toThrow();
  });
});
