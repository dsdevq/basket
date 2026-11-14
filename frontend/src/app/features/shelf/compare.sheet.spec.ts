import {signal} from '@angular/core';
import {TestBed} from '@angular/core/testing';
import {CMN_DRAWER_DATA, CmnDrawerRef} from '@lifekit-hq/ui';

import type {ListItem} from '../list/list.model';
import {ListStore} from '../list/list.store';
import type {Product, ProductStore, StoreId} from '../prices/prices.model';
import {CompareSheetComponent} from './compare.sheet';
import {dublinToday} from './compare-format';

function entry(store: StoreId, observedOn: string | null, linked = true): ProductStore {
  return {
    store,
    name: store,
    readBy: 'pc',
    listing: linked
      ? {
          url: `https://example.test/${store}`,
          title: `${store} milk`,
          checkedAt: null,
          error: null,
          imageUrl: null,
        }
      : null,
    price:
      linked && observedOn
        ? {
            observedOn,
            state: 'current',
            price: 1.29,
            wasPrice: null,
            loyaltyPrice: null,
            loyaltyScheme: null,
            promoText: null,
            validFrom: null,
            validUntil: null,
            best: 1.29,
            inStock: true,
          }
        : null,
    lastPaid: null,
  };
}

function line(id: string, store: StoreId | null): ListItem {
  return {
    id,
    text: 'Whole milk 2L',
    checked: false,
    addedAt: '2026-11-03T10:00:00Z',
    addedBy: {id: 'u1', name: 'Anna'},
    checkedAt: null,
    checkedBy: null,
    productId: 'milk',
    store,
    imageUrl: null,
  };
}

async function render(onList: ListItem[] = [], setProductStore = vi.fn().mockResolvedValue(true)) {
  const ref = {close: vi.fn()};
  const today = dublinToday();
  const product: Product = {
    id: 'milk',
    name: 'Whole milk 2L',
    stores: [
      entry('lidl', today),
      entry('aldi', '2000-01-01'),
      entry('tesco', null),
      entry('dunnes', null, false),
    ],
  };
  TestBed.configureTestingModule({
    providers: [
      {provide: ListStore, useValue: {toBuy: signal(onList), setProductStore}},
      {provide: CmnDrawerRef, useValue: ref},
      {provide: CMN_DRAWER_DATA, useValue: {product}},
    ],
  });
  const fixture = TestBed.createComponent(CompareSheetComponent);
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
  return {ref, host, rows, press, setProductStore, fixture};
}

describe('CompareSheetComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('shows the four shops, each with its price or the reason it has none, and flags the old and missing ones', async () => {
    const {rows} = await render();
    const text = rows().map(row => (row.textContent ?? '').replace(/\s+/g, ' ').trim());

    expect(text).toHaveLength(5);
    expect(text[0]).toContain('Leave it to the plan');
    expect(text[1]).toContain('lidl');
    expect(text[1]).toContain('€1.29');
    expect(text[1]).not.toContain('Old price');
    expect(text[2]).toContain('Old price');
    expect(text[2]).toContain('Seen 1 Jan');
    expect(text[3]).toContain('Not read yet');
    expect(text[3]).not.toContain('€');
    expect(text[4]).toContain('No page there');
  });

  it('fades the rows whose figure is old and the shop with no page, and no other', async () => {
    const {rows} = await render();

    const faded = rows().map(row => row.querySelector('div')?.classList.contains('opacity-55'));

    expect(faded).toEqual([false, false, true, false, true]);
  });

  it('makes every shop with something to choose a button and leaves the shop with no page inert', async () => {
    const {rows} = await render();

    const buttons = rows().map(row => row.querySelector('button') !== null);

    expect(buttons).toEqual([true, true, true, true, false]);
  });

  it('puts the product on the list at the shop that was tapped and closes', async () => {
    const {rows, press, setProductStore, ref} = await render();

    await press(rows()[2]?.querySelector('button') ?? undefined);

    expect(setProductStore).toHaveBeenCalledWith('milk', 'aldi');
    expect(ref.close).toHaveBeenCalledWith(true);
  });

  it('leaves the shop to the plan with the first row, which sets no shop', async () => {
    const {rows, press, setProductStore, ref} = await render();

    await press(rows()[0]?.querySelector('button') ?? undefined);

    expect(setProductStore).toHaveBeenCalledWith('milk', null);
    expect(ref.close).toHaveBeenCalledWith(true);
  });

  it('stays open when the list refused the line', async () => {
    const {rows, press, ref} = await render([], vi.fn().mockResolvedValue(false));

    await press(rows()[1]?.querySelector('button') ?? undefined);

    expect(ref.close).not.toHaveBeenCalled();
  });

  it('marks the shop the product already waits at on the list', async () => {
    const {rows} = await render([line('x', 'aldi')]);

    expect(rows()[2]?.textContent).toContain('On the list');
    expect(rows()[1]?.textContent).not.toContain('On the list');
    expect(rows()[0]?.textContent).not.toContain('On the list');
  });

  it('marks the plan row when the product waits on the list with no shop', async () => {
    const {rows} = await render([line('x', null)]);

    expect(rows()[0]?.textContent).toContain('On the list');
    expect(rows()[1]?.textContent).not.toContain('On the list');
  });
});
