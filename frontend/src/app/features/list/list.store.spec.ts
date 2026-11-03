import {TestBed} from '@angular/core/testing';
import {ToastService} from '@lifekit-hq/ui';

import {SessionStore} from '../../core/auth/session.store';
import {ListApi} from './list.api';
import type {ListItem} from './list.model';
import {ListStore} from './list.store';

const ME = {id: 'u1', name: 'Denys'};

function item(id: string, over: Partial<ListItem> = {}): ListItem {
  return {
    id,
    text: `item ${id}`,
    checked: false,
    addedAt: '2026-11-03T10:00:00Z',
    addedBy: ME,
    checkedAt: null,
    checkedBy: null,
    ...over,
  };
}

function setup(api: Partial<ListApi>) {
  const toast = {error: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      {provide: ListApi, useValue: api},
      {provide: ToastService, useValue: toast},
      {
        provide: SessionStore,
        useValue: {user: () => ({id: 'u1', displayName: 'Denys', role: 'owner'})},
      },
    ],
  });
  return {store: TestBed.inject(ListStore), toast};
}

describe('ListStore', () => {
  it('loads the list and splits it into to-buy and ticked', async () => {
    const {store} = setup({
      items: vi.fn().mockResolvedValue([item('a'), item('b', {checked: true})]),
    });

    expect(store.status()).toBe('loading');
    await store.load();

    expect(store.status()).toBe('ready');
    expect(store.toBuy().map(i => i.id)).toEqual(['a']);
    expect(store.done().map(i => i.id)).toEqual(['b']);
  });

  it('shows an error state when the first load fails, and keeps the list when a refresh fails', async () => {
    const items = vi.fn().mockRejectedValueOnce(new Error('offline'));
    const {store, toast} = setup({items});

    await store.load();
    expect(store.status()).toBe('error');
    expect(toast.error).toHaveBeenCalledTimes(1);

    items.mockResolvedValueOnce([item('a')]);
    await store.load();
    items.mockRejectedValueOnce(new Error('offline'));
    await store.load(true);

    expect(store.status()).toBe('ready');
    expect(store.items()).toHaveLength(1);
    expect(toast.error).toHaveBeenCalledTimes(1);
  });

  it('adds an item once, even if a refresh already brought it in', async () => {
    const created = item('new');
    const {store} = setup({
      items: vi.fn().mockResolvedValue([created]),
      add: vi.fn().mockResolvedValue(created),
    });
    await store.load();

    expect(await store.add('item new')).toBe(true);

    expect(store.items()).toHaveLength(1);
    expect(store.adding()).toBe(false);
  });

  it('reports a failed add and leaves the list alone', async () => {
    const {store, toast} = setup({add: vi.fn().mockRejectedValue(new Error('x'))});

    expect(await store.add('milk')).toBe(false);

    expect(store.items()).toEqual([]);
    expect(toast.error).toHaveBeenCalled();
  });

  it('ticks at once and keeps the server answer', async () => {
    const original = item('a');
    let resolve!: (value: ListItem) => void;
    const setChecked = vi.fn().mockReturnValue(new Promise<ListItem>(r => (resolve = r)));
    const {store} = setup({items: vi.fn().mockResolvedValue([original]), setChecked});
    await store.load();

    const pending = store.toggle(original, true);
    expect(store.items()[0]).toMatchObject({checked: true, checkedBy: ME});

    resolve({...original, checked: true, checkedAt: '2026-11-03T10:05:00Z', checkedBy: ME});
    await pending;
    expect(store.items()[0]?.checkedAt).toBe('2026-11-03T10:05:00Z');
  });

  it('puts an item back when the tick is refused', async () => {
    const original = item('a');
    const {store, toast} = setup({
      items: vi.fn().mockResolvedValue([original]),
      setChecked: vi.fn().mockRejectedValue(new Error('x')),
    });
    await store.load();

    await store.toggle(original, true);

    expect(store.items()[0]).toEqual(original);
    expect(toast.error).toHaveBeenCalled();
  });

  it('removes an item, and brings it back if the delete fails', async () => {
    const original = item('a');
    const remove = vi.fn().mockResolvedValueOnce(undefined).mockRejectedValueOnce({status: 500});
    const {store, toast} = setup({items: vi.fn().mockResolvedValue([original]), remove});
    await store.load();

    await store.remove(original);
    expect(store.items()).toEqual([]);

    await store.load();
    await store.remove(original);
    expect(store.items()).toEqual([original]);
    expect(toast.error).toHaveBeenCalled();
  });

  it('treats a delete of something already gone as done', async () => {
    const original = item('a');
    const {store, toast} = setup({
      items: vi.fn().mockResolvedValue([original]),
      remove: vi.fn().mockRejectedValue({status: 404}),
    });
    await store.load();

    await store.remove(original);

    expect(store.items()).toEqual([]);
    expect(toast.error).not.toHaveBeenCalled();
  });
});
