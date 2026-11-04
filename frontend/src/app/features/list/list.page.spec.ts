import {signal} from '@angular/core';
import {type ComponentFixture, TestBed} from '@angular/core/testing';
import {CmnDrawerService, ToastService} from '@lifekit-hq/ui';
import {of} from 'rxjs';

import {SessionStore} from '../../core/auth/session.store';
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
  };
  readonly drawer: {open: ReturnType<typeof vi.fn>};
  readonly host: HTMLElement;
}

async function render(items: ListItem[]): Promise<Harness> {
  const api = {
    items: vi.fn().mockResolvedValue(items),
    add: vi.fn(),
    setChecked: vi.fn(),
    remove: vi.fn().mockResolvedValue(undefined),
  };
  const drawer = {open: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      {provide: ListApi, useValue: api},
      {provide: CmnDrawerService, useValue: drawer},
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
  return {fixture, api, drawer, host: fixture.nativeElement as HTMLElement};
}

describe('ListPage', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
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
});
