import {TestBed} from '@angular/core/testing';
import {ToastService} from '@lifekit-hq/ui';

import {HistoryApi} from './history.api';
import type {Trip, TripPage} from './history.model';
import {HistoryStore, reloadMessage} from './history.store';

function trip(id: string, closedAt = '2026-11-05T12:00:00Z'): Trip {
  return {
    id,
    closedAt,
    items: [
      {id: `${id}-1`, text: 'Milk', checkedAt: closedAt, checkedBy: {id: 'u1', name: 'Anna'}},
    ],
  };
}

function page(trips: Trip[], hasMore = false): TripPage {
  return {trips, hasMore};
}

function setup(api: Partial<HistoryApi>) {
  const toast = {error: vi.fn(), success: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      {provide: HistoryApi, useValue: api},
      {provide: ToastService, useValue: toast},
    ],
  });
  return {store: TestBed.inject(HistoryStore), toast};
}

describe('HistoryStore', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('loads the newest page', async () => {
    const {store} = setup({trips: vi.fn().mockResolvedValue(page([trip('a')], true))});

    expect(store.status()).toBe('loading');
    await store.load();

    expect(store.status()).toBe('ready');
    expect(store.trips().map(t => t.id)).toEqual(['a']);
    expect(store.hasMore()).toBe(true);
  });

  it('shows an error state when the first load fails and keeps the trips when a repeat load fails', async () => {
    const trips = vi.fn().mockRejectedValueOnce(new Error('offline'));
    const {store, toast} = setup({trips});

    await store.load();
    expect(store.status()).toBe('error');

    trips.mockResolvedValueOnce(page([trip('a')]));
    await store.load();
    trips.mockRejectedValueOnce(new Error('offline'));
    await store.load();

    expect(store.status()).toBe('ready');
    expect(store.trips()).toHaveLength(1);
    expect(toast.error).toHaveBeenCalledTimes(2);
  });

  it('appends older trips after the last one on screen, once each', async () => {
    const trips = vi
      .fn()
      .mockResolvedValueOnce(page([trip('b', '2026-11-05T12:00:00Z')], true))
      .mockResolvedValueOnce(
        page([trip('b', '2026-11-05T12:00:00Z'), trip('a', '2026-10-29T12:00:00Z')])
      );
    const {store} = setup({trips});
    await store.load();

    await store.loadMore();

    expect(trips).toHaveBeenLastCalledWith('2026-11-05T12:00:00Z');
    expect(store.trips().map(t => t.id)).toEqual(['b', 'a']);
    expect(store.hasMore()).toBe(false);
  });

  it('does not append an older page to a list that was reloaded meanwhile', async () => {
    let finish: (value: TripPage) => void = () => undefined;
    const trips = vi
      .fn()
      .mockResolvedValueOnce(page([trip('b')], true))
      .mockReturnValueOnce(new Promise<TripPage>(resolve => (finish = resolve)))
      .mockResolvedValueOnce(page([trip('c'), trip('b')], true));
    const {store} = setup({trips});
    await store.load();

    const more = store.loadMore();
    await store.load();
    finish(page([trip('a')]));
    await more;

    expect(store.trips().map(t => t.id)).toEqual(['c', 'b']);
    expect(store.loadingMore()).toBe(false);
  });

  it('puts a trip back on the list once and says what happened', async () => {
    const reload = vi.fn().mockResolvedValue({added: 2, alreadyOnList: 1});
    const {store, toast} = setup({reload});

    const done = await store.reload(trip('a'));

    expect(done).toBe(true);
    expect(reload).toHaveBeenCalledWith('a');
    expect(toast.success).toHaveBeenCalledWith('Added 2 items. The rest was already on the list');
    expect(store.reloading()).toBeNull();
  });

  it('reports a failed reload and stays on the page', async () => {
    const {store, toast} = setup({reload: vi.fn().mockRejectedValue({status: 500})});

    const done = await store.reload(trip('a'));

    expect(done).toBe(false);
    expect(toast.error).toHaveBeenCalledTimes(1);
    expect(store.reloading()).toBeNull();
  });

  it('words the reload result', () => {
    expect(reloadMessage({added: 1, alreadyOnList: 0})).toBe('Added 1 item');
    expect(reloadMessage({added: 0, alreadyOnList: 3})).toBe(
      'Everything from that trip is already on the list'
    );
  });
});
