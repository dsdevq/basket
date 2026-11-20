import {signal} from '@angular/core';
import {TestBed} from '@angular/core/testing';
import {CMN_DRAWER_DATA, CmnDrawerRef} from '@lifekit-hq/ui';

import type {Product} from './prices.model';
import {PricesStore} from './prices.store';
import {ProductActionsSheetComponent} from './product-actions.sheet';

const EGGS: Product = {
  id: 'p1',
  name: 'Free Range Eggs',
  stores: [],
  aliases: [{id: 'a1', text: 'Яйця'}],
};

async function render(over: Record<string, unknown> = {}) {
  const ref = {close: vi.fn()};
  const prices = {
    products: signal([EGGS]),
    addAlias: vi.fn().mockResolvedValue(null),
    renameAlias: vi.fn().mockResolvedValue(null),
    removeAlias: vi.fn().mockResolvedValue(true),
    ...over,
  };
  TestBed.configureTestingModule({
    providers: [
      {provide: PricesStore, useValue: prices},
      {provide: CmnDrawerRef, useValue: ref},
      {provide: CMN_DRAWER_DATA, useValue: {product: EGGS}},
    ],
  });
  const fixture = TestBed.createComponent(ProductActionsSheetComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const settle = async (): Promise<void> => {
    await fixture.whenStable();
    fixture.detectChanges();
  };
  const submit = async (value: string): Promise<void> => {
    const input = host.querySelector('input') as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    host.querySelector('form')?.dispatchEvent(new Event('submit'));
    await settle();
  };
  const press = async (el: Element | null | undefined): Promise<void> => {
    (el as HTMLElement).click();
    await settle();
  };
  return {ref, prices, host, submit, press};
}

describe('ProductActionsSheetComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('lists the product’s other names', async () => {
    const {host} = await render();

    expect(host.textContent).toContain('Other names');
    expect(host.textContent).toContain('Яйця');
  });

  it('adds a name', async () => {
    const {prices, submit} = await render();

    await submit('  Яйця курячі ');

    expect(prices.addAlias).toHaveBeenCalledWith(EGGS, 'Яйця курячі');
  });

  it('shows the server’s reason and keeps the words when a name is refused', async () => {
    const {submit, host} = await render({
      addAlias: vi.fn().mockResolvedValue('“яйця” is already a name of Whole milk 2L.'),
    });

    await submit('яйця');

    expect(host.textContent).toContain('already a name of Whole milk 2L');
    expect((host.querySelector('input') as HTMLInputElement).value).toBe('яйця');
  });

  it('renames a name tapped in the list', async () => {
    const {prices, host, press, submit} = await render();

    await press(host.querySelector('cmn-list-item-row button'));
    expect((host.querySelector('input') as HTMLInputElement).value).toBe('Яйця');
    await submit('Яйця домашні');

    expect(prices.renameAlias).toHaveBeenCalledWith(EGGS, EGGS.aliases?.[0], 'Яйця домашні');
    expect(prices.addAlias).not.toHaveBeenCalled();
  });

  it('deletes a name', async () => {
    const {prices, host, press} = await render();

    await press(host.querySelector('button[aria-label="Delete the name Яйця"]'));

    expect(prices.removeAlias).toHaveBeenCalledWith(EGGS, EGGS.aliases?.[0]);
  });

  it('still deletes the product', async () => {
    const {host, press, ref} = await render();

    await press(
      Array.from(host.querySelectorAll('button')).find(b =>
        (b.textContent ?? '').includes('Delete product')
      )
    );

    expect(ref.close).toHaveBeenCalledWith('delete');
  });
});
