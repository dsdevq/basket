import {HttpErrorResponse} from '@angular/common/http';
import {TestBed} from '@angular/core/testing';
import {Router} from '@angular/router';
import {
  CmnDialogBareContainerComponent,
  CmnDialogService,
  CmnDrawerService,
  ToastService,
} from '@lifekit-hq/ui';
import {of} from 'rxjs';

import {BarcodeRenderer} from './barcode.component';
import {CardPage} from './card.page';
import {CardsApi} from './cards.api';
import type {Card} from './cards.model';
import {TillDialogComponent} from './till.dialog';

const TESCO: Card = {
  store: 'tesco',
  name: 'Tesco',
  format: 'code128',
  number: '634004012345678923',
  updatedAt: '2026-11-05T09:00:00Z',
};

async function render(
  api: Partial<Record<keyof CardsApi, ReturnType<typeof vi.fn>>>,
  options: {store?: string; confirm?: boolean; saved?: Card} = {}
) {
  const navigate = vi.fn().mockResolvedValue(true);
  const toast = {error: vi.fn(), success: vi.fn()};
  const confirm = vi.fn().mockReturnValue(of(options.confirm ?? true));
  const open = vi.fn().mockReturnValue({afterClosed: () => of(options.saved)});
  const openDialog = vi.fn();
  TestBed.configureTestingModule({
    providers: [
      {provide: CardsApi, useValue: api},
      {provide: BarcodeRenderer, useValue: {draw: vi.fn().mockResolvedValue(undefined)}},
      {provide: Router, useValue: {navigate}},
      {provide: ToastService, useValue: toast},
      {provide: CmnDialogService, useValue: {confirm, open: openDialog}},
      {provide: CmnDrawerService, useValue: {open}},
    ],
  });
  const fixture = TestBed.createComponent(CardPage);
  fixture.componentRef.setInput('store', options.store ?? 'tesco');
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const button = (label: string): HTMLButtonElement =>
    Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find(b =>
      b.textContent?.includes(label)
    ) as HTMLButtonElement;
  const settle = async (): Promise<void> => {
    await fixture.whenStable();
    fixture.detectChanges();
  };
  return {host, navigate, toast, confirm, open, openDialog, button, settle};
}

describe('CardPage', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('shows the code and the number in groups of four', async () => {
    const {host} = await render({get: vi.fn().mockResolvedValue(TESCO)});

    expect(host.querySelector('bk-barcode canvas')).not.toBeNull();
    expect(host.querySelector('[data-testid="number"]')?.textContent?.trim()).toBe(
      '6340 0401 2345 6789 23'
    );
    expect(host.textContent).toContain('Code 128');
  });

  it('puts the code on the whole screen from "Show at till"', async () => {
    const {openDialog, button} = await render({get: vi.fn().mockResolvedValue(TESCO)});

    button('Show at till').click();

    expect(openDialog).toHaveBeenCalledOnce();
    const [component, config] = openDialog.mock.calls[0];
    expect(component).toBe(TillDialogComponent);
    expect(config).toMatchObject({
      data: {card: TESCO},
      ariaLabel: 'Tesco card at the till',
      container: CmnDialogBareContainerComponent,
    });
    expect(config.panelClass).toContain('cmn-dialog-panel--full');
    expect(config.ariaLabel).not.toContain(TESCO.number);
  });

  it('shows a QR text as it is', async () => {
    const lidl: Card = {
      ...TESCO,
      store: 'lidl',
      name: 'Lidl',
      format: 'qr',
      number: 'LIDLPLUS-0001',
    };
    const {host} = await render({get: vi.fn().mockResolvedValue(lidl)}, {store: 'lidl'});

    expect(host.querySelector('[data-testid="number"]')?.textContent?.trim()).toBe('LIDLPLUS-0001');
  });

  it('does not ask the server about a shop it does not know', async () => {
    const get = vi.fn();
    const {host} = await render({get}, {store: 'spar'});

    expect(get).not.toHaveBeenCalled();
    expect(host.textContent).toContain('No card here');
  });

  it('says there is no card when the server has none', async () => {
    const get = vi.fn().mockRejectedValue(new HttpErrorResponse({status: 404}));
    const {host, button, navigate, settle} = await render({get});

    expect(host.textContent).toContain('No card here');
    button('Back to cards').click();
    await settle();

    expect(navigate).toHaveBeenCalledWith(['/cards']);
  });

  it('says so when the card cannot be loaded', async () => {
    const get = vi.fn().mockRejectedValue(new HttpErrorResponse({status: 0}));
    const {host} = await render({get});

    expect(host.textContent).toContain('Could not load this card');
  });

  it('says when the server can no longer read the number', async () => {
    const {host} = await render({get: vi.fn().mockResolvedValue({...TESCO, number: null})});

    expect(host.textContent).toContain('can no longer read this card number');
    expect(host.querySelector('bk-barcode')).toBeNull();
    expect(host.textContent).not.toContain('Show at till');
  });

  it('shows the changed card after the sheet saves it', async () => {
    const changed: Card = {...TESCO, number: '111122223333'};
    const {host, open, button, settle} = await render(
      {get: vi.fn().mockResolvedValue(TESCO)},
      {saved: changed}
    );

    button('Change number or code').click();
    await settle();

    expect(open).toHaveBeenCalledOnce();
    expect(host.querySelector('[data-testid="number"]')?.textContent?.trim()).toBe(
      '1111 2222 3333'
    );
  });

  it('removes the card after a confirmation and goes back to the list', async () => {
    const remove = vi.fn().mockResolvedValue(undefined);
    const {button, navigate, settle} = await render({
      get: vi.fn().mockResolvedValue(TESCO),
      remove,
    });

    button('Remove card').click();
    await settle();

    expect(remove).toHaveBeenCalledWith('tesco');
    expect(navigate).toHaveBeenCalledWith(['/cards']);
  });

  it('keeps the card when the confirmation is declined', async () => {
    const remove = vi.fn();
    const {button, navigate, settle} = await render(
      {get: vi.fn().mockResolvedValue(TESCO), remove},
      {confirm: false}
    );

    button('Remove card').click();
    await settle();

    expect(remove).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
  });

  it('stays on the card and says so when the removal fails', async () => {
    const remove = vi.fn().mockRejectedValue(new HttpErrorResponse({status: 500}));
    const {button, navigate, toast, settle} = await render({
      get: vi.fn().mockResolvedValue(TESCO),
      remove,
    });

    button('Remove card').click();
    await settle();

    expect(toast.error).toHaveBeenCalledWith('Could not remove that card.');
    expect(navigate).not.toHaveBeenCalled();
  });
});
