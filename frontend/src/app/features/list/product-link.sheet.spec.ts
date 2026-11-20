import {signal} from '@angular/core';
import {TestBed} from '@angular/core/testing';
import {CMN_DRAWER_DATA, CmnDrawerRef} from '@lifekit-hq/ui';

import type {Product} from '../prices/prices.model';
import {PricesStore} from '../prices/prices.store';
import type {ListItem} from './list.model';
import {ListStore} from './list.store';
import {ProductLinkSheetComponent} from './product-link.sheet';

const LINE: ListItem = {
  id: 'l1',
  text: 'Яйця',
  checked: false,
  addedAt: '2026-11-03T10:00:00Z',
  addedBy: {id: 'u1', name: 'Anna'},
  checkedAt: null,
  checkedBy: null,
  productId: null,
  store: null,
  imageUrl: null,
  boughtStore: null,
};

const EGGS: Product = {id: 'p1', name: 'Free Range Eggs', stores: []};
const MILK: Product = {
  id: 'p2',
  name: 'Whole milk 2L',
  stores: [],
  aliases: [{id: 'a', text: 'Яйця'}],
};
const BUTTER: Product = {id: 'p3', name: 'Butter 250g', stores: []};

async function render(
  products: Product[],
  linkProduct = vi.fn().mockResolvedValue(true),
  line: ListItem = LINE
) {
  const ref = {close: vi.fn()};
  const load = vi.fn().mockResolvedValue(undefined);
  TestBed.configureTestingModule({
    providers: [
      {provide: PricesStore, useValue: {products: signal(products), status: signal('ready'), load}},
      {provide: ListStore, useValue: {linkProduct}},
      {provide: CmnDrawerRef, useValue: ref},
      {provide: CMN_DRAWER_DATA, useValue: {item: line}},
    ],
  });
  const fixture = TestBed.createComponent(ProductLinkSheetComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const settle = async (): Promise<void> => {
    await fixture.whenStable();
    fixture.detectChanges();
  };
  const rows = () => Array.from(host.querySelectorAll('cmn-list-item-row'));
  const press = async (el: Element | null | undefined): Promise<void> => {
    (el as HTMLElement).click();
    await settle();
  };
  const type = async (value: string): Promise<void> => {
    const input = host.querySelector('input') as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await settle();
  };
  const buttonLabelled = (text: string) =>
    Array.from(host.querySelectorAll('button')).find(b => (b.textContent ?? '').includes(text));
  return {ref, host, rows, press, type, settle, buttonLabelled, load};
}

describe('ProductLinkSheetComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('lists the products and narrows them by name or by another name, saying which name matched', async () => {
    const {rows, type, host} = await render([EGGS, MILK, BUTTER]);
    expect(rows()).toHaveLength(3);

    await type('butt');
    expect(rows()).toHaveLength(1);

    await type('яйця');
    expect(rows()).toHaveLength(1);
    expect(host.textContent).toContain('Whole milk 2L');
    expect(host.textContent).toContain('Also called “Яйця”');
  });

  it('says so when nothing matches', async () => {
    const {rows, type, host} = await render([EGGS]);

    await type('zzz');

    expect(rows()).toHaveLength(0);
    expect(host.textContent).toContain('Nothing on the Shelf matches');
  });

  it('asks whether to remember the words, pre-ticked, and links with it', async () => {
    const linkProduct = vi.fn().mockResolvedValue(true);
    const {rows, press, host, buttonLabelled, ref, load} = await render([EGGS], linkProduct);

    await press(rows()[0]?.querySelector('button'));
    expect(host.textContent).toContain('Remember “Яйця” for Free Range Eggs');
    await press(buttonLabelled('Link'));

    expect(linkProduct).toHaveBeenCalledWith(LINE, 'p1', true);
    expect(load).toHaveBeenCalled();
    expect(ref.close).toHaveBeenCalledWith(true);
  });

  it('links once only when the remembering is unticked', async () => {
    const linkProduct = vi.fn().mockResolvedValue(true);
    const {rows, press, host, buttonLabelled} = await render([EGGS], linkProduct);

    await press(rows()[0]?.querySelector('button'));
    expect(host.querySelector('[role="checkbox"]')?.getAttribute('aria-checked')).toBe('true');
    await press(host.querySelector('[role="checkbox"]'));
    await press(buttonLabelled('Link'));

    expect(linkProduct).toHaveBeenCalledWith(LINE, 'p1', false);
  });

  it('goes back from the question to the search', async () => {
    const {rows, press, host, buttonLabelled} = await render([EGGS, BUTTER]);

    await press(rows()[0]?.querySelector('button'));
    await press(buttonLabelled('Back'));

    expect(rows()).toHaveLength(2);
    expect(host.textContent).not.toContain('Remember');
  });

  it('links at once, with no question, when the words already are a name of the product', async () => {
    const linkProduct = vi.fn().mockResolvedValue(true);
    const {rows, press, ref} = await render([MILK], linkProduct);

    await press(rows()[0]?.querySelector('button'));

    expect(linkProduct).toHaveBeenCalledWith(LINE, 'p2', false);
    expect(ref.close).toHaveBeenCalledWith(true);
  });

  it('stays open when the server refused the link', async () => {
    const {rows, press, buttonLabelled, ref} = await render(
      [EGGS],
      vi.fn().mockResolvedValue(false)
    );

    await press(rows()[0]?.querySelector('button'));
    await press(buttonLabelled('Link'));

    expect(ref.close).not.toHaveBeenCalled();
  });
});
