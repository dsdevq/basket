import {type ComponentFixture, TestBed} from '@angular/core/testing';
import {Router} from '@angular/router';
import {ToastService} from '@lifekit-hq/ui';

import {HistoryApi} from './history.api';
import type {Trip} from './history.model';
import {HistoryPage} from './history.page';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, 0));
  fixture.detectChanges();
  await Promise.resolve();
  fixture.detectChanges();
}

const ANNA = {id: 'u1', name: 'Anna'};
const TRIPS: Trip[] = [
  {
    id: 't2',
    closedAt: '2026-11-05T12:00:00Z',
    items: [
      {id: 'i1', text: 'Whole milk 2L', checkedAt: '2026-11-05T11:00:00Z', checkedBy: ANNA},
      {id: 'i2', text: 'Bananas', checkedAt: '2026-11-05T11:05:00Z', checkedBy: null},
    ],
  },
  {
    id: 't1',
    closedAt: '2026-10-29T12:00:00Z',
    items: [{id: 'i3', text: 'Rice', checkedAt: '2026-10-29T11:00:00Z', checkedBy: ANNA}],
  },
];

async function render(trips: Trip[], hasMore = false) {
  const api = {
    trips: vi.fn().mockResolvedValue({trips, hasMore}),
    reload: vi.fn().mockResolvedValue({added: 1, alreadyOnList: 0}),
  };
  const router = {navigateByUrl: vi.fn().mockResolvedValue(true)};
  TestBed.configureTestingModule({
    providers: [
      {provide: HistoryApi, useValue: api},
      {provide: Router, useValue: router},
      {provide: ToastService, useValue: {error: vi.fn(), success: vi.fn()}},
    ],
  });
  const fixture = TestBed.createComponent(HistoryPage);
  fixture.detectChanges();
  await settle(fixture);
  return {fixture, api, router, host: fixture.nativeElement as HTMLElement};
}

describe('HistoryPage', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('shows an empty state before the first trip is closed', async () => {
    const {host} = await render([]);

    expect(host.textContent).toContain('No trips yet');
  });

  it('lists each trip with its date, items and who ticked them', async () => {
    const {host} = await render(TRIPS);

    const text = host.textContent ?? '';
    expect(text).toContain('Thu 5 Nov 2026 · 2 items');
    expect(text).toContain('Thu 29 Oct 2026 · 1 item');
    expect(text).toContain('Whole milk 2L');
    expect(text).toContain('Ticked by Anna');
    expect(text.indexOf('5 Nov')).toBeLessThan(text.indexOf('29 Oct'));
  });

  it('puts a trip back on the list and goes to the list', async () => {
    const {fixture, api, router, host} = await render(TRIPS);
    const first = Array.from(host.querySelectorAll('button')).find(b =>
      b.textContent?.includes('Same as last time')
    );

    first?.click();
    await settle(fixture);

    expect(api.reload).toHaveBeenCalledWith('t2');
    expect(router.navigateByUrl).toHaveBeenCalledWith('/list');
  });

  it('offers older trips only when there are more', async () => {
    const withMore = await render(TRIPS, true);
    expect(withMore.host.textContent).toContain('Show older trips');
    TestBed.resetTestingModule();

    const without = await render(TRIPS, false);
    expect(without.host.textContent).not.toContain('Show older trips');
  });
});
