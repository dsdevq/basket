import {signal} from '@angular/core';
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
    boughtStore: null,
    ...over,
  };
}

function setup(
  api: Partial<ListApi>,
  user: () => {id: string; displayName: string; role: string} | null = () => ({
    id: 'u1',
    displayName: 'Denys',
    role: 'owner',
  })
) {
  const toast = {error: vi.fn(), success: vi.fn(), info: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      {provide: ListApi, useValue: api},
      {provide: ToastService, useValue: toast},
      {
        provide: SessionStore,
        useValue: {user},
      },
    ],
  });
  return {store: TestBed.inject(ListStore), toast};
}

describe('ListStore', () => {
  beforeEach(() => localStorage.clear());

  it('shows the list as last seen when there is no signal, and says so', async () => {
    const first = setup({items: vi.fn().mockResolvedValue([item('a')])});
    await first.store.load();
    TestBed.resetTestingModule();

    const {store, toast} = setup({items: vi.fn().mockRejectedValue({status: 0})});
    await store.load();

    expect(store.status()).toBe('ready');
    expect(store.toBuy().map(i => i.id)).toEqual(['a']);
    expect(toast.info).toHaveBeenCalledOnce();
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("does not show another person's remembered list, nor one after a server error", async () => {
    localStorage.setItem(
      'basket.offline.list',
      JSON.stringify({userId: 'someone-else', savedAt: '2026-11-20T10:00:00Z', items: [item('x')]})
    );
    const {store} = setup({items: vi.fn().mockRejectedValue({status: 0})});
    await store.load();
    expect(store.status()).toBe('error');
  });

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
    const {store, toast} = setup({add: vi.fn().mockRejectedValue({status: 500})});

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

  it('shows the shop a line was picked from on a tick at once, and none on an untick', async () => {
    const picked = item('a', {productId: 'milk', store: 'aldi'});
    const never = new Promise<ListItem>(() => undefined);
    const {store} = setup({
      items: vi.fn().mockResolvedValue([picked]),
      setChecked: vi.fn().mockReturnValue(never),
    });
    await store.load();

    void store.toggle(picked, true);
    expect(store.items()[0]).toMatchObject({checked: true, boughtStore: 'aldi'});
    void store.toggle(store.items()[0], false);
    expect(store.items()[0]).toMatchObject({checked: false, boughtStore: null});
  });

  it('keeps the shop the server recorded at the tick', async () => {
    const original = item('a', {productId: 'milk'});
    const ticked = {
      ...original,
      checked: true,
      checkedAt: '2026-11-03T10:05:00Z',
      checkedBy: ME,
      boughtStore: 'lidl' as const,
    };
    const {store} = setup({
      items: vi.fn().mockResolvedValue([original]),
      setChecked: vi.fn().mockResolvedValue(ticked),
    });
    await store.load();

    await store.toggle(original, true);

    expect(store.items()[0]?.boughtStore).toBe('lidl');
  });

  it('changes where a ticked line was bought at once and keeps the server answer', async () => {
    const ticked = item('a', {checked: true, boughtStore: 'lidl'});
    let resolve!: (value: ListItem) => void;
    const setBoughtAt = vi.fn().mockReturnValue(new Promise<ListItem>(r => (resolve = r)));
    const {store} = setup({items: vi.fn().mockResolvedValue([ticked]), setBoughtAt});
    await store.load();

    const pending = store.setBoughtStore(ticked, 'tesco');
    expect(store.items()[0]?.boughtStore).toBe('tesco');
    resolve({...ticked, boughtStore: 'tesco'});

    expect(await pending).toBe(true);
    expect(setBoughtAt).toHaveBeenCalledWith('a', 'tesco');
    expect(store.items()[0]?.boughtStore).toBe('tesco');
  });

  it('puts the old shop back and says so when the change is refused', async () => {
    const ticked = item('a', {checked: true, boughtStore: 'lidl'});
    const {store, toast} = setup({
      items: vi.fn().mockResolvedValue([ticked]),
      setBoughtAt: vi.fn().mockRejectedValue({status: 422}),
    });
    await store.load();

    expect(await store.setBoughtStore(ticked, 'tesco')).toBe(false);

    expect(store.items()[0]?.boughtStore).toBe('lidl');
    expect(toast.error).toHaveBeenCalled();
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
      setChecked: vi.fn().mockRejectedValue({status: 500}),
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

  describe('linkProduct', () => {
    it('replaces the typed line with the linked one', async () => {
      const linked = item('a', {productId: 'p1', text: 'Яйця'});
      const linkProduct = vi.fn().mockResolvedValue(linked);
      const {store} = setup({
        items: vi.fn().mockResolvedValue([item('a', {text: 'Яйця'})]),
        linkProduct,
      });
      await store.load();

      expect(await store.linkProduct(store.items()[0], 'p1', true)).toBe(true);

      expect(linkProduct).toHaveBeenCalledWith('a', 'p1', true);
      expect(store.items()).toEqual([linked]);
    });

    it("says the server's reason when the link is refused, and leaves the line as it was", async () => {
      const linkProduct = vi
        .fn()
        .mockRejectedValue({status: 409, error: {message: 'Already on the list.'}});
      const {store, toast} = setup({items: vi.fn().mockResolvedValue([item('a')]), linkProduct});
      await store.load();

      expect(await store.linkProduct(item('a'), 'p1', false)).toBe(false);

      expect(toast.error).toHaveBeenCalledWith('Already on the list.');
      expect(store.items()[0]?.productId).toBeNull();
    });

    it('says a plain failure otherwise', async () => {
      const {store, toast} = setup({
        items: vi.fn().mockResolvedValue([item('a')]),
        linkProduct: vi.fn().mockRejectedValue(new Error('offline')),
      });
      await store.load();

      expect(await store.linkProduct(item('a'), 'p1', true)).toBe(false);

      expect(toast.error).toHaveBeenCalledWith('Could not link that line. Try again.');
    });
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

    it('does not bring the bought items back when the refresh after it has no signal', async () => {
      const items = vi
        .fn()
        .mockResolvedValueOnce([item('b', {checked: true})])
        .mockRejectedValue({status: 0});
      const {store, toast} = setup({items, close: vi.fn().mockResolvedValue({})});
      await store.load();

      await store.closeTrip();
      await Promise.resolve();
      await store.load(true);

      expect(store.items()).toEqual([]);
      expect(toast.info).not.toHaveBeenCalled();

      TestBed.resetTestingModule();
      const next = setup({items: vi.fn().mockRejectedValue({status: 0})});
      await next.store.load();
      expect(next.store.items()).toEqual([]);
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

      expect(toast.error).toHaveBeenCalledTimes(1);
      await vi.waitFor(() => expect(store.items().map(i => i.checked)).toEqual([false]));
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
  describe('with no signal', () => {
    const offline = {status: 0};

    it('queues an edit made with no connection, and sends the same edit at once with one', async () => {
      const online = setup({
        items: vi.fn().mockResolvedValue([item('a')]),
        setChecked: vi.fn().mockResolvedValue(item('a', {checked: true})),
      });
      await online.store.load();
      await online.store.toggle(item('a'), true);
      expect(online.store.waiting()).toBe(0);
      expect(online.store.items()[0]?.checked).toBe(true);
      TestBed.resetTestingModule();
      localStorage.clear();

      const setChecked = vi.fn().mockRejectedValue(offline);
      const {store, toast} = setup({items: vi.fn().mockResolvedValue([item('a')]), setChecked});
      await store.load();
      await store.toggle(item('a'), true);

      expect(store.waiting()).toBe(1);
      expect(store.items()[0]?.checked).toBe(true);
      expect(toast.error).not.toHaveBeenCalled();
    });

    it('keeps queued edits across a restart and applies them, in order, when the connection returns', async () => {
      const calls: string[] = [];
      const first = setup({
        items: vi.fn().mockResolvedValue([item('a'), item('b')]),
        setChecked: vi.fn().mockRejectedValue(offline),
        remove: vi.fn().mockRejectedValue(offline),
      });
      await first.store.load();
      await first.store.toggle(item('a'), true);
      await first.store.remove(item('b'));
      expect(first.store.waiting()).toBe(2);
      TestBed.resetTestingModule();

      const {store} = setup({
        items: vi.fn().mockResolvedValue([item('a', {checked: true})]),
        setChecked: vi.fn().mockImplementation(() => {
          calls.push('check');
          return Promise.resolve(item('a', {checked: true}));
        }),
        remove: vi.fn().mockImplementation(() => {
          calls.push('remove');
          return Promise.resolve();
        }),
      });
      await store.load();

      expect(calls).toEqual(['check', 'remove']);
      expect(store.waiting()).toBe(0);
      expect(localStorage.getItem('basket.offline.queue')).toBeNull();
    });

    it('keeps the queue, and does not replace the list with the server one, while it still cannot send', async () => {
      const {store} = setup({
        items: vi.fn().mockResolvedValue([item('a')]),
        setChecked: vi.fn().mockRejectedValue(offline),
      });
      await store.load();
      await store.toggle(item('a'), true);

      await store.load(true);

      expect(store.waiting()).toBe(1);
      expect(store.items()[0]?.checked).toBe(true);
    });

    it('shows an added line at once and swaps in the server line, re-pointing later edits at it', async () => {
      const added = item('real', {text: 'milk'});
      const first = setup({
        items: vi.fn().mockResolvedValue([]),
        add: vi.fn().mockRejectedValue(offline),
        setChecked: vi.fn().mockRejectedValue(offline),
      });
      await first.store.load();

      expect(await first.store.add('milk')).toBe(true);
      const local = first.store.items()[0];
      expect(local?.text).toBe('milk');
      await first.store.toggle(local, true);
      expect(first.store.waiting()).toBe(2);

      TestBed.resetTestingModule();
      const add = vi.fn().mockResolvedValue(added);
      const setChecked = vi.fn().mockResolvedValue({...added, checked: true});
      const {store} = setup({
        items: vi.fn().mockResolvedValue([{...added, checked: true}]),
        add,
        setChecked,
      });
      await store.load();

      expect(setChecked).toHaveBeenLastCalledWith('real', true);
      expect(store.waiting()).toBe(0);
      expect(store.items().map(i => i.id)).toEqual(['real']);
    });

    it('replays a line added and then deleted with no signal, in order, deleting the real line', async () => {
      const added = item('real', {text: 'milk'});
      const first = setup({
        items: vi.fn().mockResolvedValue([]),
        add: vi.fn().mockRejectedValue(offline),
        remove: vi.fn().mockRejectedValue(offline),
      });
      await first.store.load();
      await first.store.add('milk');
      await first.store.remove(first.store.items()[0]);
      expect(first.store.items()).toEqual([]);
      expect(first.store.waiting()).toBe(2);
      TestBed.resetTestingModule();

      const calls: string[] = [];
      const {store} = setup({
        items: vi.fn().mockResolvedValue([]),
        add: vi.fn().mockImplementation(() => {
          calls.push('add');
          return Promise.resolve(added);
        }),
        remove: vi.fn().mockImplementation((id: string) => {
          calls.push(`remove ${id}`);
          return Promise.resolve();
        }),
      });
      await store.load();

      expect(calls).toEqual(['add', 'remove real']);
      expect(store.waiting()).toBe(0);
    });

    it('drops an edit the server refuses on replay and says so', async () => {
      const setChecked = vi.fn().mockRejectedValueOnce(offline).mockRejectedValue({status: 422});
      const {store, toast} = setup({items: vi.fn().mockResolvedValue([item('a')]), setChecked});
      await store.load();
      await store.toggle(item('a'), true);

      await store.load(true);

      expect(store.waiting()).toBe(0);
      expect(toast.error).toHaveBeenCalledTimes(1);
      expect(store.items()[0]?.checked).toBe(false);
    });

    it('keeps every edit made while an earlier one was still being sent, in order', async () => {
      const setChecked = vi.fn().mockRejectedValue(offline);
      const {store} = setup({
        items: vi.fn().mockResolvedValue([item('a'), item('b')]),
        setChecked,
      });
      await store.load();

      await Promise.all([store.toggle(item('a'), true), store.toggle(item('b'), true)]);

      expect(store.waiting()).toBe(2);
      expect(JSON.parse(localStorage.getItem('basket.offline.queue') ?? '{}').edits).toEqual([
        {kind: 'check', id: 'a', checked: true},
        {kind: 'check', id: 'b', checked: true},
      ]);
    });

    it("does not hand one person's waiting edits to the next person", async () => {
      const current = signal<{id: string; displayName: string; role: string} | null>({
        id: 'u1',
        displayName: 'Denys',
        role: 'owner',
      });
      const setChecked = vi.fn().mockRejectedValue(offline);
      const {store, toast} = setup(
        {items: vi.fn().mockResolvedValue([item('a')]), setChecked},
        () => current()
      );
      await store.load();
      await store.toggle(item('a'), true);
      expect(store.waiting()).toBe(1);

      current.set(null);
      TestBed.tick();
      expect(store.waiting()).toBe(0);
      expect(toast.info).toHaveBeenCalledTimes(1);

      setChecked.mockResolvedValue(item('a', {checked: true}));
      current.set({id: 'u2', displayName: 'Mia', role: 'member'});
      TestBed.tick();
      await store.load(true);

      expect(store.waiting()).toBe(0);
      expect(setChecked).toHaveBeenCalledTimes(1);
    });

    it('stops the replay on a 401 and leaves the queue as it is (the session end clears it)', async () => {
      const setChecked = vi.fn().mockRejectedValueOnce(offline).mockRejectedValue({status: 401});
      const {store, toast} = setup({items: vi.fn().mockResolvedValue([item('a')]), setChecked});
      await store.load();
      await store.toggle(item('a'), true);

      await store.load(true);

      expect(store.waiting()).toBe(1);
      expect(toast.error).not.toHaveBeenCalled();
    });

    it('will not file the trip, and re-reads the list, when a replayed edit is refused', async () => {
      const close = vi.fn().mockResolvedValue({});
      const items = vi
        .fn()
        .mockResolvedValueOnce([item('a')])
        .mockResolvedValue([item('a', {checked: false})]);
      const {store} = setup({
        items,
        setChecked: vi.fn().mockRejectedValueOnce(offline).mockRejectedValue({status: 422}),
        close,
      });
      await store.load();
      await store.toggle(item('a'), true);

      expect(await store.closeTrip()).toBe(false);
      await vi.waitFor(() => expect(items).toHaveBeenCalledTimes(2));

      expect(close).not.toHaveBeenCalled();
      expect(store.items()[0]?.checked).toBe(false);
    });

    it('will not file the trip while edits are still waiting', async () => {
      const close = vi.fn().mockResolvedValue({});
      const {store, toast} = setup({
        items: vi.fn().mockResolvedValue([item('a')]),
        setChecked: vi.fn().mockRejectedValue(offline),
        close,
      });
      await store.load();
      await store.toggle(item('a'), true);

      expect(await store.closeTrip()).toBe(false);

      expect(close).not.toHaveBeenCalled();
      expect(toast.error).toHaveBeenCalledTimes(1);
    });
  });
});
