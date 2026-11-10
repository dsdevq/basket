import {HttpErrorResponse} from '@angular/common/http';
import {TestBed} from '@angular/core/testing';
import {CMN_DRAWER_DATA, CmnDrawerRef} from '@lifekit-hq/ui';

import {CardFormSheetComponent} from './card-form.sheet';
import {CardsApi} from './cards.api';
import {type Card, CHAINS} from './cards.model';

const LIDL = CHAINS[0];
const TESCO = CHAINS[2];
const SAVED: Card = {
  store: 'tesco',
  name: 'Tesco',
  format: 'code128',
  number: '634004012345678923',
  updatedAt: '2026-11-05T09:00:00Z',
};

function render(api: Partial<Record<keyof CardsApi, ReturnType<typeof vi.fn>>>, data: object) {
  const ref = {close: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      {provide: CardsApi, useValue: api},
      {provide: CmnDrawerRef, useValue: ref},
      {provide: CMN_DRAWER_DATA, useValue: data},
    ],
  });
  const fixture = TestBed.createComponent(CardFormSheetComponent);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const submit = async (value: string): Promise<void> => {
    const input = host.querySelector('input') as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    host.querySelector('form')?.dispatchEvent(new Event('submit'));
    await new Promise(resolve => setTimeout(resolve, 0));
    fixture.detectChanges();
  };
  return {ref, host, submit, fixture};
}

describe('CardFormSheetComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it("saves the typed number as the shop's usual code and closes with the card", async () => {
    const save = vi.fn().mockResolvedValue(SAVED);
    const {ref, submit} = render({save}, {chain: TESCO, existing: null});

    await submit(' 634004 0123 4567 8923 ');

    expect(save).toHaveBeenCalledWith('tesco', '634004 0123 4567 8923', 'code128');
    expect(ref.close).toHaveBeenCalledWith(SAVED);
  });

  it('starts Lidl on the QR code the Lidl Plus app shows', async () => {
    const save = vi.fn().mockResolvedValue({...SAVED, store: 'lidl', format: 'qr'});
    const {submit} = render({save}, {chain: LIDL, existing: null});

    await submit('LIDLPLUS-0001');

    expect(save).toHaveBeenCalledWith('lidl', 'LIDLPLUS-0001', 'qr');
  });

  it('starts from the saved card when one is changed', async () => {
    const save = vi.fn().mockResolvedValue(SAVED);
    const existing: Card = {...SAVED, format: 'qr', number: 'LIDLPLUS-0001'};
    const {host, submit} = render({save}, {chain: TESCO, existing});

    expect((host.querySelector('input') as HTMLInputElement).value).toBe('LIDLPLUS-0001');
    await submit('LIDLPLUS-0001');

    expect(save).toHaveBeenCalledWith('tesco', 'LIDLPLUS-0001', 'qr');
    expect(host.textContent).toContain('Save card');
  });

  it('asks for a number before calling the server', async () => {
    const save = vi.fn();
    const {ref, host, submit} = render({save}, {chain: TESCO, existing: null});

    await submit('   ');

    expect(save).not.toHaveBeenCalled();
    expect(ref.close).not.toHaveBeenCalled();
    expect(host.textContent).toContain('Type the Tesco card number first.');
  });

  it("stays open and shows the server's reason, without repeating the number", async () => {
    const refusal = new HttpErrorResponse({
      status: 400,
      error: {
        error: 'number_invalid',
        message: 'Enter the card number: 4 to 48 letters or digits, no spaces needed.',
      },
    });
    const save = vi.fn().mockRejectedValue(refusal);
    const {ref, host, submit} = render({save}, {chain: TESCO, existing: null});

    await submit('1234');

    expect(ref.close).not.toHaveBeenCalled();
    expect(host.textContent).toContain('Enter the card number: 4 to 48');
    expect(host.textContent).not.toContain('1234');
  });

  it('says to check the connection when the server cannot be reached', async () => {
    const save = vi.fn().mockRejectedValue(new HttpErrorResponse({status: 0}));
    const {host, submit} = render({save}, {chain: TESCO, existing: null});

    await submit('634004');

    expect(host.textContent).toContain('Could not save the card.');
  });
});
