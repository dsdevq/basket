import {TestBed} from '@angular/core/testing';
import {CmnDrawerService, ToastService} from '@lifekit-hq/ui';
import {of} from 'rxjs';

import {AuthApi} from '../../core/auth/auth.api';
import {PeoplePage} from './people.page';

const MEMBERS = [
  {id: '1', displayName: 'Denys', role: 'owner', createdAt: '2026-11-03T09:00:00Z'},
  {id: '2', displayName: 'Anna', role: 'member', createdAt: '2026-11-03T09:30:00Z'},
];
const INVITES = [
  {
    id: 'i1',
    label: 'Her sister',
    createdAt: '2026-11-03T10:00:00Z',
    expiresAt: '2026-11-04T10:00:00Z',
  },
];

async function render(api: Partial<AuthApi>, drawer: Partial<CmnDrawerService> = {}) {
  TestBed.configureTestingModule({
    providers: [
      {provide: AuthApi, useValue: api},
      {provide: CmnDrawerService, useValue: drawer},
      {provide: ToastService, useValue: {error: vi.fn()}},
    ],
  });
  const fixture = TestBed.createComponent(PeoplePage);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return {fixture, host: fixture.nativeElement as HTMLElement};
}

describe('PeoplePage', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('lists the household and the invite links still open', async () => {
    const {host} = await render({
      members: vi.fn().mockResolvedValue(MEMBERS),
      pendingInvites: vi.fn().mockResolvedValue(INVITES),
    });

    const text = host.textContent ?? '';
    expect(text).toContain('Denys');
    expect(text).toContain('Owner');
    expect(text).toContain('Anna');
    expect(text).toContain('Her sister');
  });

  it('cancels an open link', async () => {
    const api = {
      members: vi.fn().mockResolvedValue(MEMBERS),
      pendingInvites: vi.fn().mockResolvedValue(INVITES),
      revokeInvite: vi.fn().mockResolvedValue(undefined),
    };
    const {fixture, host} = await render(api);
    const cancel = Array.from(host.querySelectorAll('button')).find(b =>
      b.textContent?.includes('Cancel link')
    );

    cancel?.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(api.revokeInvite).toHaveBeenCalledWith('i1');
    expect(host.textContent).not.toContain('Her sister');
  });

  it('reloads after the invite sheet closes, so the new link shows', async () => {
    const api = {
      members: vi.fn().mockResolvedValue(MEMBERS),
      pendingInvites: vi.fn().mockResolvedValueOnce([]).mockResolvedValueOnce(INVITES),
    };
    const drawer = {open: vi.fn().mockReturnValue({afterClosed: () => of(true)})};
    const {fixture, host} = await render(api, drawer);
    const invite = Array.from(host.querySelectorAll('button')).find(b =>
      b.textContent?.includes('Invite someone')
    );

    invite?.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(drawer.open).toHaveBeenCalled();
    expect(host.textContent).toContain('Her sister');
  });

  it('offers a retry when the people do not load', async () => {
    const {host} = await render({
      members: vi.fn().mockRejectedValue(new Error('offline')),
      pendingInvites: vi.fn().mockResolvedValue([]),
    });

    expect(host.textContent).toContain('Could not load people');
  });
});
