import {signal} from '@angular/core';
import {type ComponentFixture, TestBed} from '@angular/core/testing';
import {CmnDialogService, CmnDrawerService, ToastService} from '@lifekit-hq/ui';
import {of} from 'rxjs';

import {SessionStore} from '../../core/auth/session.store';
import {HabitsApi} from '../habits/habits.api';
import type {Habit} from '../habits/habits.model';
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
  readonly dialog: {confirm: ReturnType<typeof vi.fn>};
  readonly drawer: {open: ReturnType<typeof vi.fn>};
  readonly host: HTMLElement;
}

function habit(productId: string, name: string, boughtIn: number, usual = true): Habit {
  return {productId, name, boughtIn, lastBoughtAt: '2026-11-05T12:00:00Z', usual};
}

async function render(items: ListItem[], habits: Habit[] = []): Promise<Harness> {
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
  const drawer = {open: vi.fn()};
  const dialog = {confirm: vi.fn().mockReturnValue(of(true))};
  TestBed.configureTestingModule({
    providers: [
      {provide: ListApi, useValue: api},
      {provide: HabitsApi, useValue: habitsApi},
      {provide: CmnDrawerService, useValue: drawer},
      {provide: CmnDialogService, useValue: dialog},
      {provide: ToastService, useValue: {error: vi.fn(), success: vi.fn()}},
      {
        provide: SessionStore,
        useValue: {user: signal({id: 'u1', displayName: 'Denys', role: 'owner'})},
      },
    ],
  });
  const fixture = TestBed.createComponent(ListPage);
  fixture.detectChanges();
  await settle(fixture);
  return {fixture, api, habitsApi, dialog, drawer, host: fixture.nativeElement as HTMLElement};
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
});
