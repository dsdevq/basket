import {type ComponentFixture, TestBed} from '@angular/core/testing';
import {ToastService} from '@lifekit-hq/ui';

import {ScorecardApi} from './scorecard.api';
import type {ScorecardResponse} from './scorecard.model';
import {ScorecardPage} from './scorecard.page';
import {card, entry} from './scorecard-fixtures';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, 0));
  fixture.detectChanges();
  await Promise.resolve();
  fixture.detectChanges();
}

const RESPONSE: ScorecardResponse = card({
  trips: 4,
  compared: 10,
  notCompared: 2,
  saved: 6.5,
  topStore: 'aldi',
  stores: [
    entry({store: 'lidl', storeName: 'Lidl', wins: 3, share: 30}),
    entry({store: 'aldi', storeName: 'Aldi', wins: 6, share: 60}),
    entry({store: 'tesco', storeName: 'Tesco', wins: 1, share: 10}),
    entry({store: 'dunnes', storeName: 'Dunnes'}),
  ],
  trend: [
    {month: '2026-10', trips: 1, compared: 3, saved: 2.5},
    {month: '2026-11', trips: 3, compared: 7, saved: 4},
  ],
});

async function render(response: ScorecardResponse | Error) {
  const api = {
    get: vi.fn(() =>
      response instanceof Error ? Promise.reject(response) : Promise.resolve(response)
    ),
  };
  const toast = {error: vi.fn(), success: vi.fn()};
  TestBed.configureTestingModule({
    providers: [
      {provide: ScorecardApi, useValue: api},
      {provide: ToastService, useValue: toast},
    ],
  });
  const fixture = TestBed.createComponent(ScorecardPage);
  fixture.detectChanges();
  await settle(fixture);
  return {fixture, api, toast, host: fixture.nativeElement as HTMLElement};
}

const squash = (value: string | null | undefined): string =>
  (value ?? '').replace(/\s+/g, ' ').trim();

const section = (host: HTMLElement, heading: string): HTMLElement | undefined =>
  [...host.querySelectorAll<HTMLElement>('cmn-list-section')].find(s =>
    squash(s.textContent).startsWith(heading)
  );

const rows = (block: HTMLElement | undefined): string[] =>
  [...(block?.querySelectorAll('cmn-list-item-row') ?? [])].map(r => squash(r.textContent));

describe('ScorecardPage', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('says on top which shop wins most and what the winners saved', async () => {
    const {host} = await render(RESPONSE);

    const answer = squash(host.querySelector('[data-testid="verdict"]')?.textContent);
    expect(answer).toContain('Aldi wins most');
    expect(answer).toContain('€6.50 saved');
    expect(answer).toContain('2 not compared');
  });

  it('lists the shops by wins, the winner marked', async () => {
    const {host} = await render(RESPONSE);

    const list = rows(section(host, 'Wins by shop'));
    expect(list).toHaveLength(4);
    expect(list[0]).toContain('Aldi');
    expect(list[0]).toContain('6 wins · 60% of compared lines');
    expect(list[0]).toContain('Most wins');
    expect(list[3]).toContain('Dunnes');
    expect(list[3]).toContain('No wins');
  });

  it('lists the months newest first with what each saved', async () => {
    const {host} = await render(RESPONSE);

    const list = rows(section(host, 'Month by month'));
    expect(list[0]).toContain('Nov 2026');
    expect(list[0]).toContain('3 trips · 7 lines compared');
    expect(list[0]).toContain('€4.00');
    expect(list[1]).toContain('Oct 2026');
    expect(list[1]).toContain('€2.50');
  });

  it('states the rule with the limit the server uses', async () => {
    const {host} = await render(RESPONSE);

    const rule = squash(host.querySelector('[data-testid="rule"]')?.textContent);
    expect(rule).toContain('last 6 months');
    expect(rule).toContain('last 3 days');
  });

  it('says there is nothing to score before the first trip', async () => {
    const {host} = await render(card());

    expect(host.textContent).toContain('No trips to score yet');
    expect(host.querySelector('[data-testid="verdict"]')).toBeNull();
  });

  it('offers a retry when it did not load', async () => {
    const {host, toast} = await render(new Error('offline'));

    expect(host.textContent).toContain('The scorecard did not load');
    expect(toast.error).toHaveBeenCalled();
  });
});
