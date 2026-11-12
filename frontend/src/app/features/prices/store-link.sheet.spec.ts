import {TestBed} from '@angular/core/testing';
import {CMN_DRAWER_DATA, CmnDrawerRef} from '@lifekit-hq/ui';

import type {Product, ProductStore} from './prices.model';
import {PricesStore} from './prices.store';
import {StoreLinkSheetComponent} from './store-link.sheet';

const ENTRY: ProductStore = {
  store: 'lidl',
  name: 'Lidl',
  readBy: 'nightly',
  listing: null,
  price: null,
  lastPaid: null,
};
const PRODUCT: Product = {id: 'p1', name: 'Milk', stores: [ENTRY]};

function render(prices: Partial<InstanceType<typeof PricesStore>>, entry: ProductStore = ENTRY) {
  const ref = {close: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      {provide: PricesStore, useValue: prices},
      {provide: CmnDrawerRef, useValue: ref},
      {provide: CMN_DRAWER_DATA, useValue: {product: PRODUCT, entry}},
    ],
  });
  const fixture = TestBed.createComponent(StoreLinkSheetComponent);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const input = host.querySelector('input') as HTMLInputElement;
  const submit = async (value: string): Promise<void> => {
    input.value = value;
    input.dispatchEvent(new Event('input'));
    host.querySelector('form')?.dispatchEvent(new Event('submit'));
    await new Promise(resolve => setTimeout(resolve, 0));
    fixture.detectChanges();
  };
  return {ref, host, submit};
}

describe('StoreLinkSheetComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('saves the pasted address and closes', async () => {
    const link = vi.fn().mockResolvedValue(null);
    const {ref, submit} = render({link} as never);

    await submit(' https://www.lidl.ie/p/aubergine/p11214436 ');

    expect(link).toHaveBeenCalledWith(PRODUCT, 'lidl', 'https://www.lidl.ie/p/aubergine/p11214436');
    expect(ref.close).toHaveBeenCalledWith(true);
  });

  it("stays open and shows the server's reason when the address is refused", async () => {
    const link = vi.fn().mockResolvedValue("Paste the address of the product's Lidl page.");
    const {ref, host, submit} = render({link} as never);

    await submit('https://example.com');

    expect(ref.close).not.toHaveBeenCalled();
    expect(host.textContent).toContain("Paste the address of the product's Lidl page.");
  });

  it('asks for an address before calling the server, and offers to remove an existing link', async () => {
    const link = vi.fn();
    const unlink = vi.fn().mockResolvedValue(undefined);
    const linked = {
      ...ENTRY,
      listing: {
        url: 'https://www.lidl.ie/p/x/p11000',
        title: 'X',
        checkedAt: null,
        error: null,
        imageUrl: null,
      },
    };
    const {ref, host, submit} = render({link, unlink} as never, linked);

    await submit('');
    expect(link).not.toHaveBeenCalled();
    expect(host.textContent).toContain('Paste the address of the Lidl page first.');

    const remove = Array.from(host.querySelectorAll('button')).find(b =>
      b.textContent?.includes('Remove link')
    );
    remove?.click();
    await new Promise(resolve => setTimeout(resolve, 0));

    expect(unlink).toHaveBeenCalledWith(PRODUCT, 'lidl');
    expect(ref.close).toHaveBeenCalledWith(true);
  });
});
