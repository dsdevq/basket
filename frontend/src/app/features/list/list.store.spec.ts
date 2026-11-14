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
    productId: null,
    store: null,
    imageUrl: null,
    ...over,
  };
}

function setup(api: Partial<ListApi>) {
  const toast = {error: vi.fn(), success: vi.fn()};
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

  it('ignores a refresh that was read before a tick landed', async () => {
    const original = item('a');
    const ticked = {...original, checked: true, checkedAt: '2026-11-03T10:05:00Z', checkedBy: ME};
    let answerPoll!: (value: ListItem[]) => void;
    const items = vi
      .fn()
      .mockResolvedValueOnce([original])
      .mockReturnValueOnce(new Promise<ListItem[]>(r => (answerPoll = r)));
    const {store} = setup({items, setChecked: vi.fn().mockResolvedValue(ticked)});
    await store.load();

    const poll = store.load(true);
    await store.toggle(original, true);
    answerPoll([original]);
    await poll;

    expect(store.items()).toEqual([ticked]);
    expect(store.status()).toBe('ready');
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

  describe('closeTrip', () => {
    it('takes the ticked items off the list once the server filed the trip, and keeps the rest', async () => {
      const items = vi
        .fn()
        .mockResolvedValueOnce([item('a'), item('b', {checked: true})])
        .mockResolvedValue([item('a')]);
      const close = vi.fn().mockResolvedValue({});
      const {store, toast} = setup({items, close});
      await store.load();

      const done = await store.closeTrip();

      expect(done).toBe(true);
      expect(close).toHaveBeenCalledTimes(1);
      expect(store.items().map(i => i.id)).toEqual(['a']);
      expect(toast.success).toHaveBeenCalledWith('Trip saved to History');
      expect(store.closing()).toBe(false);
    });

    it('leaves the list as it was when the server refuses', async () => {
      const items = vi.fn().mockResolvedValue([item('b', {checked: true})]);
      const close = vi.fn().mockRejectedValue({status: 500});
      const {store, toast} = setup({items, close});
      await store.load();

      const done = await store.closeTrip();

      expect(done).toBe(false);
      expect(store.items().map(i => i.id)).toEqual(['b']);
      expect(toast.error).toHaveBeenCalledTimes(1);
      expect(toast.success).not.toHaveBeenCalled();
      expect(store.closing()).toBe(false);
    });

    it('re-reads the list when the server says it changed under us', async () => {
      const items = vi
        .fn()
        .mockResolvedValueOnce([item('b', {checked: true})])
        .mockResolvedValue([item('b')]);
      const close = vi.fn().mockRejectedValue({status: 409});
      const {store, toast} = setup({items, close});
      await store.load();

      await store.closeTrip();
      await Promise.resolve();

      expect(toast.error).toHaveBeenCalledTimes(1);
      expect(store.items().map(i => i.checked)).toEqual([false]);
    });

    it('ignores a second tap while the first is in flight', async () => {
      let finish: (value: unknown) => void = () => undefined;
      const close = vi.fn().mockReturnValue(new Promise(resolve => (finish = resolve)));
      const {store} = setup({
        items: vi.fn().mockResolvedValue([item('b', {checked: true})]),
        close,
      });
      await store.load();

      const first = store.closeTrip();
      const second = await store.closeTrip();
      finish({});
      await first;

      expect(second).toBe(false);
      expect(close).toHaveBeenCalledTimes(1);
    });
  });

  it('puts a picked product on the list once, whether the server created the line or returned the open one', async () => {
    const line = item('p', {text: 'Whole milk 2L', productId: 'milk', store: 'tesco'});
    const addProduct = vi.fn().mockResolvedValue(line);
    const {store} = setup({addProduct, items: vi.fn().mockResolvedValue([])});
    await store.load();

    expect(await store.addProduct('milk', 'tesco')).toBe(true);
    expect(await store.addProduct('milk', 'tesco')).toBe(true);

    expect(addProduct).toHaveBeenCalledWith('milk', 'tesco');
    expect(store.toBuy()).toEqual([line]);
  });

  it('says so and adds nothing when a picked product is refused', async () => {
    const {store, toast} = setup({addProduct: vi.fn().mockRejectedValue(new Error('offline'))});

    expect(await store.addProduct('milk', null)).toBe(false);

    expect(store.items()).toEqual([]);
    expect(toast.error).toHaveBeenCalledTimes(1);
  });

  describe('setProductStore (the compare sheet choosing a shop)', () => {
    const waiting = (id: string, shop: ListItem['store']) =>
      item(id, {text: 'Whole milk 2L', productId: 'milk', store: shop});

    it('moves the single line a product has to the chosen shop, adding first and removing after', async () => {
      const calls: string[] = [];
      const api = {
        items: vi.fn().mockResolvedValue([waiting('old', null)]),
        addProduct: vi.fn().mockImplementation(() => {
          calls.push('add');
          return Promise.resolve(waiting('new', 'aldi'));
        }),
        remove: vi.fn().mockImplementation(() => {
          calls.push('remove');
          return Promise.resolve();
        }),
      };
      const {store} = setup(api);
      await store.load();

      expect(await store.setProductStore('milk', 'aldi')).toBe(true);

      expect(api.addProduct).toHaveBeenCalledWith('milk', 'aldi');
      expect(api.remove).toHaveBeenCalledWith('old');
      expect(calls).toEqual(['add', 'remove']);
      expect(store.toBuy().map(i => [i.id, i.store])).toEqual([['new', 'aldi']]);
    });

    it('leaves the shop unset by moving a line at a shop to no shop', async () => {
      const api = {
        items: vi.fn().mockResolvedValue([waiting('old', 'lidl')]),
        addProduct: vi.fn().mockResolvedValue(waiting('new', null)),
        remove: vi.fn().mockResolvedValue(undefined),
      };
      const {store} = setup(api);
      await store.load();

      expect(await store.setProductStore('milk', null)).toBe(true);

      expect(api.addProduct).toHaveBeenCalledWith('milk', null);
      expect(store.toBuy().map(i => i.store)).toEqual([null]);
    });

    it('does nothing when the product already waits at that shop', async () => {
      const api = {
        items: vi.fn().mockResolvedValue([waiting('old', 'aldi')]),
        addProduct: vi.fn(),
        remove: vi.fn(),
      };
      const {store} = setup(api);
      await store.load();

      expect(await store.setProductStore('milk', 'aldi')).toBe(true);

      expect(api.addProduct).not.toHaveBeenCalled();
      expect(api.remove).not.toHaveBeenCalled();
    });

    it('keeps the old line when the new one is refused', async () => {
      const api = {
        items: vi.fn().mockResolvedValue([waiting('old', null)]),
        addProduct: vi.fn().mockRejectedValue(new Error('offline')),
        remove: vi.fn(),
      };
      const {store} = setup(api);
      await store.load();

      expect(await store.setProductStore('milk', 'aldi')).toBe(false);

      expect(api.remove).not.toHaveBeenCalled();
      expect(store.toBuy().map(i => i.id)).toEqual(['old']);
    });

    it('adds a line without removing one when the product waits at several shops', async () => {
      const api = {
        items: vi.fn().mockResolvedValue([waiting('a', 'lidl'), waiting('b', 'tesco')]),
        addProduct: vi.fn().mockResolvedValue(waiting('c', 'aldi')),
        remove: vi.fn(),
      };
      const {store} = setup(api);
      await store.load();

      expect(await store.setProductStore('milk', 'aldi')).toBe(true);

      expect(api.remove).not.toHaveBeenCalled();
      expect(store.toBuy()).toHaveLength(3);
    });

    it('ignores a ticked line: it is history, not a line waiting somewhere', async () => {
      const api = {
        items: vi.fn().mockResolvedValue([{...waiting('done', 'aldi'), checked: true}]),
        addProduct: vi.fn().mockResolvedValue(waiting('new', 'aldi')),
        remove: vi.fn(),
      };
      const {store} = setup(api);
      await store.load();

      expect(await store.setProductStore('milk', 'aldi')).toBe(true);

      expect(api.addProduct).toHaveBeenCalledWith('milk', 'aldi');
      expect(api.remove).not.toHaveBeenCalled();
    });
  });
});
