import {signal} from '@angular/core';
import {TestBed} from '@angular/core/testing';
import {CMN_DRAWER_DATA, CmnDrawerRef} from '@lifekit-hq/ui';

import type {StoreId} from '../prices/prices.model';
import {ItemActionsSheetComponent} from './item-actions.sheet';
import type {ListItem} from './list.model';
import {ListStore} from './list.store';

const ANNA = {id: 'u1', name: 'Anna'};

function item(over: Partial<ListItem> = {}): ListItem {
  return {
    id: 'a',
    text: 'Whole milk 2L',
    checked: false,
    addedAt: '2026-11-03T10:00:00Z',
    addedBy: ANNA,
    checkedAt: null,
    checkedBy: null,
    productId: null,
    store: null,
    imageUrl: null,
    boughtStore: null,
    ...over,
  };
}

async function render(line: ListItem, setBoughtStore = vi.fn().mockResolvedValue(true)) {
  const ref = {close: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      {provide: ListStore, useValue: {items: signal([line]), setBoughtStore}},
      {provide: CmnDrawerRef, useValue: ref},
      {provide: CMN_DRAWER_DATA, useValue: {item: line}},
    ],
  });
  const fixture = TestBed.createComponent(ItemActionsSheetComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const rows = () => Array.from(host.querySelectorAll('cmn-list-item-row'));
  const press = async (el: Element | undefined): Promise<void> => {
    (el as HTMLElement).click();
    await fixture.whenStable();
    fixture.detectChanges();
  };
  return {ref, host, rows, press, setBoughtStore};
}

describe('ItemActionsSheetComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('offers no shop for an item that is still to buy', async () => {
    const {rows, host} = await render(item());

    expect(rows()).toHaveLength(0);
    expect(host.textContent).not.toContain('Bought at');
  });

  it('lists the four shops for a ticked item and marks the one it was bought at', async () => {
    const {rows} = await render(item({checked: true, boughtStore: 'aldi'}));
    const text = rows().map(row => (row.textContent ?? '').replace(/\s+/g, ' ').trim());

    expect(text.map(t => t.replace(' Bought here', ''))).toEqual([
      'Lidl',
      'Aldi',
      'Tesco',
      'Dunnes',
    ]);
    expect(text[1]).toContain('Bought here');
    expect(text.filter(t => t.includes('Bought here'))).toHaveLength(1);
  });

  it('marks no shop when none was recorded', async () => {
    const {host} = await render(item({checked: true}));

    expect(host.textContent).not.toContain('Bought here');
  });

  it('changes the shop and closes once the server took it', async () => {
    const line = item({checked: true, boughtStore: 'lidl'});
    const {rows, press, setBoughtStore, ref} = await render(line);

    await press(rows()[3]?.querySelector('button') ?? undefined);

    expect(setBoughtStore).toHaveBeenCalledWith(line, 'dunnes' satisfies StoreId);
    expect(ref.close).toHaveBeenCalled();
  });

  it('stays open when the change did not go through', async () => {
    const {rows, press, ref} = await render(
      item({checked: true, boughtStore: 'lidl'}),
      vi.fn().mockResolvedValue(false)
    );

    await press(rows()[2]?.querySelector('button') ?? undefined);

    expect(ref.close).not.toHaveBeenCalled();
  });

  it('does nothing when the shop on record is chosen again', async () => {
    const {rows, press, setBoughtStore} = await render(item({checked: true, boughtStore: 'lidl'}));

    await press(rows()[0]?.querySelector('button') ?? undefined);

    expect(setBoughtStore).not.toHaveBeenCalled();
  });
});
