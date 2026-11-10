import {TestBed} from '@angular/core/testing';
import {CMN_DIALOG_DATA} from '@lifekit-hq/ui';

import {BarcodeRenderer} from './barcode.component';
import type {Card} from './cards.model';
import {TillDialogComponent} from './till.dialog';

const draw = vi.fn();

function render(card: Card & {number: string}) {
  TestBed.configureTestingModule({
    providers: [
      {provide: BarcodeRenderer, useValue: {draw}},
      {provide: CMN_DIALOG_DATA, useValue: {card}},
    ],
  });
  const fixture = TestBed.createComponent(TillDialogComponent);
  fixture.detectChanges();
  return {fixture, host: fixture.nativeElement as HTMLElement};
}

const TESCO = {
  store: 'tesco',
  name: 'Tesco',
  format: 'code128',
  number: '634004012345678923',
  updatedAt: '2026-11-05T09:00:00Z',
} as const;

describe('TillDialogComponent', () => {
  beforeEach(() => {
    draw.mockReset();
    draw.mockResolvedValue(undefined);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('draws the card number in the card format, with the number in groups of four under it', async () => {
    const {fixture, host} = render(TESCO);
    await fixture.whenStable();

    expect(draw).toHaveBeenCalledWith(host.querySelector('canvas'), TESCO.number, 'code128');
    expect(host.querySelector('[data-testid="till-number"]')?.textContent?.trim()).toBe(
      '6340 0401 2345 6789 23'
    );
  });

  it('names the picture for a screen reader without reading the number out', () => {
    const {host} = render(TESCO);

    const canvas = host.querySelector('canvas');
    expect(canvas?.getAttribute('role')).toBe('img');
    expect(canvas?.getAttribute('aria-label')).toBe('Tesco card Code 128');
  });

  it('shows a QR text as typed', () => {
    const {host} = render({
      ...TESCO,
      store: 'lidl',
      name: 'Lidl',
      format: 'qr',
      number: 'LIDLPLUS-0001',
    });

    expect(host.querySelector('[data-testid="till-number"]')?.textContent?.trim()).toBe(
      'LIDLPLUS-0001'
    );
  });

  it('says so, and not only draws nothing, when the number does not fit the code', async () => {
    draw.mockRejectedValue(new Error('bwipp.qrcodeNoValidSymbol'));
    const {fixture, host} = render(TESCO);
    await fixture.whenStable();
    await new Promise(resolve => setTimeout(resolve, 0));
    fixture.detectChanges();

    expect(host.textContent).toContain('This number does not fit the code');
  });
});
