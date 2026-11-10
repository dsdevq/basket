import {TestBed} from '@angular/core/testing';
import {Router} from '@angular/router';
import {CmnDrawerService} from '@lifekit-hq/ui';
import {of} from 'rxjs';

import {CardsApi} from './cards.api';
import type {CardSummary} from './cards.model';
import {CardsPage} from './cards.page';

const TESCO: CardSummary = {
  store: 'tesco',
  name: 'Tesco',
  format: 'code128',
  masked: '•••• 8923',
  readable: true,
  updatedAt: '2026-11-05T09:00:00Z',
};

async function render(list: ReturnType<typeof vi.fn>, saved: unknown = undefined) {
  const open = vi.fn().mockReturnValue({afterClosed: () => of(saved)});
  const navigate = vi.fn().mockResolvedValue(true);
  TestBed.configureTestingModule({
    providers: [
      {provide: CardsApi, useValue: {list}},
      {provide: CmnDrawerService, useValue: {open}},
      {provide: Router, useValue: {navigate}},
    ],
  });
  const fixture = TestBed.createComponent(CardsPage);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const row = (name: string): HTMLButtonElement =>
    Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find(b =>
      b.textContent?.includes(name)
    ) as HTMLButtonElement;
  const settle = async (): Promise<void> => {
    await fixture.whenStable();
    fixture.detectChanges();
  };
  return {host, open, navigate, row, settle, fixture};
}

describe('CardsPage', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('lists the four shops, with the number hidden down to its last four', async () => {
    const {host} = await render(vi.fn().mockResolvedValue([TESCO]));

    const text = host.textContent ?? '';
    for (const shop of ['Lidl', 'Aldi', 'Tesco', 'Dunnes']) {
      expect(text).toContain(shop);
    }
    expect(text).toContain('•••• 8923 · Code 128');
    expect(text).toContain('Not added yet');
  });

  it('opens the code of a card that is added', async () => {
    const {navigate, row, settle} = await render(vi.fn().mockResolvedValue([TESCO]));

    row('Tesco').click();
    await settle();

    expect(navigate).toHaveBeenCalledWith(['/cards', 'tesco']);
  });

  it('adds a shop that has no card yet, then opens its code', async () => {
    const saved = {store: 'aldi', name: 'Aldi', format: 'code128', number: '99', updatedAt: ''};
    const {open, navigate, row, settle} = await render(vi.fn().mockResolvedValue([TESCO]), saved);

    row('Aldi').click();
    await settle();

    expect(open).toHaveBeenCalledOnce();
    const options = open.mock.calls[0][1] as {data: {chain: {store: string}; existing: unknown}};
    expect(options.data.chain.store).toBe('aldi');
    expect(options.data.existing).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/cards', 'aldi']);
  });

  it('stays on the list when the sheet is dismissed', async () => {
    const {navigate, row, settle} = await render(vi.fn().mockResolvedValue([]));

    row('Aldi').click();
    await settle();

    expect(navigate).not.toHaveBeenCalled();
  });

  it('offers to add again a card the server can no longer read', async () => {
    const unreadable = {...TESCO, readable: false, masked: ''};
    const {host, open, row, settle} = await render(vi.fn().mockResolvedValue([unreadable]));

    expect(host.textContent).toContain('Cannot be read. Add it again.');
    row('Tesco').click();
    await settle();

    expect(open).toHaveBeenCalledOnce();
  });

  it('says so when the cards cannot be loaded, and tries again', async () => {
    const list = vi.fn().mockRejectedValueOnce(new Error('offline')).mockResolvedValue([TESCO]);
    const {host, row, settle} = await render(list);

    expect(host.textContent).toContain('Could not load your cards');
    row('Try again').click();
    await settle();

    expect(host.textContent).toContain('•••• 8923');
  });
});
