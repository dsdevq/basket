import {signal} from '@angular/core';
import {type ComponentFixture, TestBed} from '@angular/core/testing';
import {CmnDialogService, CmnDrawerService, ToastService} from '@lifekit-hq/ui';
import {of} from 'rxjs';

import {SessionStore} from '../../core/auth/session.store';
import {HabitsApi} from '../habits/habits.api';
import type {Habit} from '../habits/habits.model';
import {PricesApi} from '../prices/prices.api';
import type {Product} from '../prices/prices.model';
import {ListApi} from './list.api';
import type {ListItem} from './list.model';
import {ListPage} from './list.page';

const ANNA = {id: 'u2', name: 'Anna'};

/** Lets pending promises finish. `whenStable` would wait for the page's 15-second refresh timer. */
async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, 0));
  fixture.detectChanges();
  await Promise.resolve();
  fixture.detectChanges();
}

/** `cmn-async-state` keeps a shown skeleton up for a moment, so wait until the loading notice is gone. */
async function loaded(fixture: ComponentFixture<unknown>): Promise<void> {
  await vi.waitFor(() => {
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('Loading');
  });
}

function item(id: string, text: string, over: Partial<ListItem> = {}): ListItem {
  return {
    id,
    text,
    checked: false,
    addedAt: '2026-11-03T10:00:00Z',
    addedBy: ANNA,
    checkedAt: null,
    checkedBy: null,
    productId: null,
    store: null,
    imageUrl: null,
    boughtStore: null,
    quantity: null,
    note: null,
    ...over,
  };
}

interface Harness {
  readonly fixture: ComponentFixture<ListPage>;
  readonly api: {
    items: ReturnType<typeof vi.fn>;
    add: ReturnType<typeof vi.fn>;
    setChecked: ReturnType<typeof vi.fn>;
    remove: ReturnType<typeof vi.fn>;
    close: ReturnType<typeof vi.fn>;
    addProduct: ReturnType<typeof vi.fn>;
  };
  readonly habitsApi: {all: ReturnType<typeof vi.fn>};
  readonly toast: {info: ReturnType<typeof vi.fn>};
  readonly dialog: {confirm: ReturnType<typeof vi.fn>};
  readonly drawer: {open: ReturnType<typeof vi.fn>};
  readonly host: HTMLElement;
}

function habit(productId: string, name: string, boughtIn: number, usual = true): Habit {
  return {productId, name, boughtIn, lastBoughtAt: '2026-11-05T12:00:00Z', usual};
}

async function render(
  items: ListItem[],
  habits: Habit[] = [],
  products: Product[] = []
): Promise<Harness> {
  const api = {
    items: vi.fn().mockResolvedValue(items),
    add: vi.fn(),
    setChecked: vi.fn(),
    remove: vi.fn().mockResolvedValue(undefined),
    close: vi.fn().mockResolvedValue({}),
    addProduct: vi
      .fn()
      .mockImplementation((productId: string) =>
        Promise.resolve(item(`new-${productId}`, productId, {productId}))
      ),
  };
  const habitsApi = {all: vi.fn().mockResolvedValue(habits)};
  const toast = {error: vi.fn(), info: vi.fn(), success: vi.fn()};
  const drawer = {open: vi.fn()};
  const dialog = {confirm: vi.fn().mockReturnValue(of(true))};
  TestBed.configureTestingModule({
    providers: [
      {provide: ListApi, useValue: api},
      {provide: HabitsApi, useValue: habitsApi},
      {provide: CmnDrawerService, useValue: drawer},
      {provide: CmnDialogService, useValue: dialog},
      {provide: PricesApi, useValue: {all: vi.fn().mockResolvedValue({products, stores: []})}},
      {provide: ToastService, useValue: toast},
      {
        provide: SessionStore,
        useValue: {user: signal({id: 'u1', displayName: 'Denys', role: 'owner'})},
      },
    ],
  });
  const fixture = TestBed.createComponent(ListPage);
  fixture.detectChanges();
  await settle(fixture);
  await loaded(fixture);
  return {
    fixture,
    api,
    habitsApi,
    toast,
    dialog,
    drawer,
    host: fixture.nativeElement as HTMLElement,
  };
}

function product(id: string, name: string, aliases: string[] = []): Product {
  return {
    id,
    name,
    stores: [],
    aliases: aliases.map((text, index) => ({id: `${id}-a${index}`, text})),
  };
}

async function typeText(h: Harness, value: string): Promise<void> {
  const input = h.host.querySelector<HTMLInputElement>('input[aria-label="Add an item"]');
  if (!input) {
    throw new Error('No add field');
  }
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await settle(h.fixture);
}

function swipeButton(host: HTMLElement, name: string): HTMLButtonElement {
  const button = host.querySelector<HTMLButtonElement>(`button[aria-label="${name}"]`);
  if (!button) {
    throw new Error(`No swipe action named "${name}"`);
  }
  return button;
}

describe('ListPage', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('marks a typed line Not linked, and a product line not', async () => {
    const {host} = await render([
      item('1', 'Яйця'),
      item('2', 'Whole milk 2L', {productId: 'p1', store: 'tesco'}),
    ]);
    const text = host.textContent ?? '';

    expect(text.match(/Not linked/g)).toHaveLength(1);
  });

  it('offers what is usually bought, and not what is already waiting, as one-tap adds', async () => {
    const {host, api, fixture} = await render(
      [item('1', 'Whole milk 2L', {productId: 'milk', store: 'tesco'})],
      [
        habit('milk', 'Whole milk 2L', 6),
        habit('eggs', 'Eggs', 4),
        habit('butter', 'Butter', 1, false),
      ]
    );

    const text = host.textContent ?? '';
    expect(text).toContain('Usually bought');
    expect(text).toContain('Bought on 4 trips');
    expect(text).not.toContain('Butter');
    expect(host.querySelector('button[aria-label="Add Whole milk 2L to the list"]')).toBeNull();

    host.querySelector<HTMLButtonElement>('button[aria-label="Add Eggs to the list"]')?.click();
    await settle(fixture);

    expect(api.addProduct).toHaveBeenCalledWith('eggs', null);
    expect(host.querySelector('button[aria-label="Add Eggs to the list"]')).toBeNull();
  });

  it('shows no usually bought section before any trip has made one', async () => {
    const {host} = await render([item('1', 'Spinach')]);

    expect(host.textContent).not.toContain('Usually bought');
  });

  it('shows an empty state with a prompt to add the first item', async () => {
    const {host} = await render([]);

    expect(host.textContent).toContain('Nothing on the list');
  });

  it('lists what is still to buy and what is ticked, with who added each', async () => {
    const {host} = await render([item('1', 'Spinach'), item('2', 'Butter', {checked: true})]);

    const text = host.textContent ?? '';
    expect(text).toContain('To buy · 1');
    expect(text).toContain('Ticked · 1');
    expect(text).toContain('Spinach');
    expect(text).toContain('Butter');
    expect(text).toContain('Anna');
  });

  it('marks a line picked from a shop with that shop, and leaves a free-text line bare', async () => {
    const {host} = await render([
      item('1', 'Whole milk 2L', {productId: 'p1', store: 'tesco'}),
      item('2', 'Candles'),
    ]);

    const rows = Array.from(host.querySelectorAll('cmn-list-item-row'));
    expect(rows[0]?.textContent).toContain('Tesco');
    expect(rows[1]?.textContent).not.toMatch(/Tesco|Lidl|Aldi|Dunnes/);
  });

  it('marks a ticked line with the shop it was bought at, and leaves one with no shop bare', async () => {
    const {host} = await render([
      item('1', 'Whole milk 2L', {checked: true, productId: 'p1', boughtStore: 'aldi'}),
      item('2', 'Candles', {checked: true}),
    ]);

    const rows = Array.from(host.querySelectorAll('cmn-list-item-row'));
    expect(rows[0]?.textContent).toContain('Aldi');
    expect(rows[1]?.textContent).not.toMatch(/Tesco|Lidl|Aldi|Dunnes/);
  });

  it('shows the shop it was bought at on a ticked line, not the one it was picked from', async () => {
    const {host} = await render([
      item('1', 'Whole milk 2L', {
        checked: true,
        productId: 'p1',
        store: 'tesco',
        boughtStore: 'dunnes',
      }),
    ]);

    const text = host.querySelector('cmn-list-item-row')?.textContent ?? '';
    expect(text).toContain('Dunnes');
    expect(text).not.toContain('Tesco');
  });

  it('shows the product photo on a row, and the first letter when there is none or it fails to load', async () => {
    const {fixture, host} = await render([
      item('1', 'Whole milk 2L', {
        productId: 'p1',
        store: 'tesco',
        imageUrl: 'https://img.tesco.ie/milk.jpg',
      }),
      item('2', 'Candles'),
    ]);

    const rows = Array.from(host.querySelectorAll('cmn-list-item-row'));
    expect(rows[0]?.querySelector('img')?.getAttribute('src')).toBe(
      'https://img.tesco.ie/milk.jpg'
    );
    expect(rows[1]?.querySelector('img')).toBeNull();
    expect(rows[1]?.querySelector('bk-list-photo')?.textContent?.trim()).toBe('C');

    rows[0]?.querySelector('img')?.dispatchEvent(new Event('error'));
    fixture.detectChanges();

    expect(rows[0]?.querySelector('img')).toBeNull();
    expect(rows[0]?.querySelector('bk-list-photo')?.textContent?.trim()).toBe('W');
  });

  it('adds the typed text and clears the field', async () => {
    const {fixture, api, host} = await render([]);
    api.add.mockResolvedValue(item('9', 'Eggs'));
    const input = host.querySelector('input') as HTMLInputElement;

    input.value = '  Eggs ';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (host.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle(fixture);
    fixture.detectChanges();

    expect(api.add).toHaveBeenCalledWith('Eggs');
    expect(input.value).toBe('');
    expect(host.textContent).toContain('Eggs');
  });

  it('says plainly how many changes made with no signal are still waiting', async () => {
    const {fixture, api, host} = await render([item('1', 'Milk')]);
    expect(host.textContent).not.toContain('waiting to send');
    api.setChecked.mockRejectedValue({status: 0});

    (host.querySelector('[role="checkbox"]') as HTMLButtonElement).click();
    await settle(fixture);
    fixture.detectChanges();

    expect(host.textContent).toContain('1 change waiting to send');
  });

  it('does not call the server for blank text', async () => {
    const {fixture, api, host} = await render([]);
    const input = host.querySelector('input') as HTMLInputElement;

    input.value = '   ';
    input.dispatchEvent(new Event('input'));
    (host.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await settle(fixture);

    expect(api.add).not.toHaveBeenCalled();
  });

  it('ticks an item from its checkbox', async () => {
    const {fixture, api, host} = await render([item('1', 'Spinach')]);
    api.setChecked.mockResolvedValue(item('1', 'Spinach', {checked: true, checkedBy: ANNA}));

    (host.querySelector('[role="checkbox"]') as HTMLButtonElement).click();
    await settle(fixture);

    expect(api.setChecked).toHaveBeenCalledWith('1', true);
  });

  it('deletes through the item sheet', async () => {
    const {fixture, api, drawer, host} = await render([item('1', 'Spinach')]);
    drawer.open.mockReturnValue({afterClosed: () => of('delete')});
    const more = Array.from(host.querySelectorAll('button')).find(b =>
      b.getAttribute('aria-label')?.startsWith('More for')
    );

    more?.click();
    await settle(fixture);
    fixture.detectChanges();

    expect(drawer.open).toHaveBeenCalled();
    expect(api.remove).toHaveBeenCalledWith('1');
    expect(host.textContent).not.toContain('Spinach');
  });

  it('keeps the item when the sheet is dismissed', async () => {
    const {fixture, api, drawer, host} = await render([item('1', 'Spinach')]);
    drawer.open.mockReturnValue({afterClosed: () => of(undefined)});
    const more = Array.from(host.querySelectorAll('button')).find(b =>
      b.getAttribute('aria-label')?.startsWith('More for')
    );

    more?.click();
    await settle(fixture);

    expect(api.remove).not.toHaveBeenCalled();
  });

  it('ticks an item from its swipe action', async () => {
    const {fixture, api, host} = await render([item('1', 'Spinach')]);
    api.setChecked.mockResolvedValue(item('1', 'Spinach', {checked: true, checkedBy: ANNA}));

    swipeButton(host, 'Tick Spinach').click();
    await settle(fixture);

    expect(api.setChecked).toHaveBeenCalledWith('1', true);
  });

  it('offers Untick on a ticked item and unticks it from the swipe action', async () => {
    const {fixture, api, host} = await render([
      item('1', 'Butter', {checked: true, checkedBy: ANNA, checkedAt: '2026-11-03T11:00:00Z'}),
    ]);
    api.setChecked.mockResolvedValue(item('1', 'Butter'));

    swipeButton(host, 'Untick Butter').click();
    await settle(fixture);

    expect(api.setChecked).toHaveBeenCalledWith('1', false);
  });

  it('deletes an item straight from its swipe action, without the sheet', async () => {
    const {fixture, api, drawer, host} = await render([item('1', 'Spinach')]);

    swipeButton(host, 'Delete Spinach').click();
    await settle(fixture);
    fixture.detectChanges();

    expect(drawer.open).not.toHaveBeenCalled();
    expect(api.remove).toHaveBeenCalledWith('1');
    expect(host.textContent).not.toContain('Spinach');
  });

  describe('Done shopping', () => {
    const button = (host: HTMLElement): HTMLButtonElement | undefined =>
      Array.from(host.querySelectorAll('button')).find(b =>
        b.textContent?.includes('Done shopping')
      );

    it('is offered only when something is ticked', async () => {
      const none = await render([item('1', 'Spinach')]);
      expect(button(none.host)).toBeUndefined();
      TestBed.resetTestingModule();

      const some = await render([item('1', 'Spinach'), item('2', 'Butter', {checked: true})]);
      expect(button(some.host)).toBeDefined();
    });

    it('files the trip after the confirm and leaves the list empty when everything was ticked', async () => {
      const {fixture, api, dialog, host} = await render([
        item('1', 'Spinach', {checked: true}),
        item('2', 'Butter', {checked: true}),
      ]);
      api.items.mockResolvedValue([]);

      button(host)?.click();
      await settle(fixture);
      await settle(fixture);

      expect(dialog.confirm).toHaveBeenCalledTimes(1);
      expect(dialog.confirm.mock.calls[0][0].message).toContain('2 ticked items go to History');
      expect(api.close).toHaveBeenCalledTimes(1);
      expect(host.textContent).toContain('Nothing on the list');
      expect(host.textContent).not.toContain('Spinach');
    });

    it('reloads the habits so the trip just closed counts', async () => {
      const {fixture, habitsApi, host} = await render([item('1', 'Butter', {checked: true})]);
      expect(habitsApi.all).toHaveBeenCalledTimes(1);

      button(host)?.click();
      await settle(fixture);
      await settle(fixture);

      expect(habitsApi.all).toHaveBeenCalledTimes(2);
    });

    it('says what stays on the list when some items are not ticked', async () => {
      const {fixture, dialog, host} = await render([
        item('1', 'Spinach'),
        item('2', 'Butter', {checked: true}),
      ]);

      button(host)?.click();
      await settle(fixture);

      expect(dialog.confirm.mock.calls[0][0].message).toContain(
        '1 ticked item goes to History. The 1 not ticked stay on the list.'
      );
    });

    it('does nothing when the confirm is dismissed', async () => {
      const {fixture, api, dialog, host} = await render([item('2', 'Butter', {checked: true})]);
      dialog.confirm.mockReturnValue(of(false));

      button(host)?.click();
      await settle(fixture);

      expect(api.close).not.toHaveBeenCalled();
      expect(host.textContent).toContain('Butter');
    });
  });

  describe('Shelf suggestions while typing', () => {
    const eggs = product('eggs', 'Free Range Eggs', ['Яйця']);
    const milk = product('milk', 'Whole milk 2L');

    it('offers a product by a saved Cyrillic alias, and by its English name', async () => {
      const h = await render([], [], [eggs, milk]);

      await typeText(h, 'яйц');
      expect(h.host.textContent).toContain('Free Range Eggs');
      expect(h.host.textContent).toContain('Also called “Яйця”');
      expect(h.host.textContent).not.toContain('Whole milk');

      await typeText(h, 'milk');
      expect(h.host.textContent).toContain('Whole milk 2L');
      expect(h.host.textContent).not.toContain('Free Range Eggs');
    });

    it('offers nothing while the field is empty', async () => {
      const h = await render([], [], [eggs, milk]);

      expect(h.host.textContent).not.toContain('On your Shelf');
    });

    it('adds the product line in one tap and clears the field', async () => {
      const h = await render([], [], [eggs]);
      await typeText(h, 'eggs');

      const row = h.host.querySelector<HTMLElement>(
        'cmn-list-section[heading="On your Shelf"] cmn-list-item-row [role="button"], cmn-list-section[heading="On your Shelf"] cmn-list-item-row button'
      );
      row?.click();
      await settle(h.fixture);

      expect(h.api.addProduct).toHaveBeenCalledWith('eggs', null);
    });

    it('links text that is exactly a saved alias by itself, with Undo that removes the new line', async () => {
      const h = await render([], [], [eggs]);
      h.api.add.mockResolvedValue(item('n1', 'Free Range Eggs', {productId: 'eggs'}));
      await typeText(h, 'яйця');

      h.host.querySelector('form')?.dispatchEvent(new Event('submit'));
      await settle(h.fixture);

      expect(h.toast.info).toHaveBeenCalledTimes(1);
      const [message, options] = h.toast.info.mock.calls[0] as [
        string,
        {action: {label: string; run: () => void}},
      ];
      expect(message).toContain('Free Range Eggs');
      expect(options.action.label).toBe('Undo');
      options.action.run();
      await settle(h.fixture);
      expect(h.api.remove).toHaveBeenCalledWith('n1');
    });

    it('offers no Undo for words that are not a saved alias', async () => {
      const h = await render([], [], [eggs]);
      h.api.add.mockResolvedValue(item('n2', 'Eggs'));
      await typeText(h, 'Eggs');

      h.host.querySelector('form')?.dispatchEvent(new Event('submit'));
      await settle(h.fixture);

      expect(h.toast.info).not.toHaveBeenCalled();
    });
  });
});
