import {signal} from '@angular/core';
import {TestBed} from '@angular/core/testing';
import {Router} from '@angular/router';
import {
  ACCOUNT_SIGN_OUT_ID,
  APPEARANCE_PALETTE_ITEMS,
  CmnWhatsNewService,
  WHATS_NEW_MENU_ID,
} from '@lifekit-hq/ui';

import {BrowserNavigation} from '../auth/browser-navigation';
import type {SessionUser} from '../auth/session.model';
import {SessionStore} from '../auth/session.store';
import {APP_VERSION} from '../version';
import {ShellChromeService} from './shell-chrome.service';

describe('ShellChromeService', () => {
  const user = signal<SessionUser | null>(null);
  const signOut = vi.fn().mockResolvedValue(undefined);
  const navigateByUrl = vi.fn().mockResolvedValue(true);
  const assign = vi.fn();
  const open = vi.fn();
  const hasUnread = signal(false);

  function service(): ShellChromeService {
    TestBed.configureTestingModule({
      providers: [
        {
          provide: SessionStore,
          useValue: {user, isOwner: () => user()?.role === 'owner', signOut},
        },
        {provide: Router, useValue: {navigateByUrl}},
        {provide: BrowserNavigation, useValue: {assign}},
        {provide: CmnWhatsNewService, useValue: {open, hasUnread}},
      ],
    });
    return TestBed.inject(ShellChromeService);
  }

  beforeEach(() => {
    user.set({id: '1', displayName: 'Ada Lovelace', email: 'ada@example.com', role: 'member'});
    hasUnread.set(false);
    signOut.mockClear();
    navigateByUrl.mockClear();
    assign.mockClear();
    signOut.mockResolvedValue(null);
    open.mockClear();
  });

  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('shows People in the nav to the owner only', () => {
    const chrome = service();
    expect(chrome.navItems().map(item => item.label)).not.toContain('People');

    user.set({id: '1', displayName: 'Ada', role: 'owner'});

    expect(chrome.navItems().map(item => item.label)).toContain('People');
  });

  it('builds the account from the session: initials, name and email', () => {
    const account = service().account();

    expect(account?.label).toBe('AL');
    expect(account?.name).toBe('Ada Lovelace');
    expect(account?.email).toBe('ada@example.com');
  });

  it('leaves email out while the session has none, and has no account when signed out', () => {
    const chrome = service();
    user.set({id: '1', displayName: 'Ada', role: 'member'});
    expect(chrome.account()?.email).toBeUndefined();

    user.set(null);

    expect(chrome.account()).toBeUndefined();
  });

  it('puts the pages, Settings, the appearance entries and Sign out in the palette', () => {
    const items = service().paletteItems();
    const ids = items.map(item => item.id);

    expect(ids).toEqual(
      expect.arrayContaining([
        '/list',
        '/shelf',
        '/settings',
        ...APPEARANCE_PALETTE_ITEMS.map(item => item.id),
        ACCOUNT_SIGN_OUT_ID,
      ])
    );
    expect(ids).not.toContain('/people');
    expect(items.find(item => item.id === '/list')?.group).toBe('Pages');
  });

  it("labels the version with What's new", () => {
    expect(service().versionLabel).toBe(`v${APP_VERSION} · What's new`);
  });

  it("opens What's new and reports the id handled", async () => {
    const handled = await service().handle(WHATS_NEW_MENU_ID);

    expect(handled).toBe(true);
    expect(open).toHaveBeenCalledOnce();
  });

  it('signs out and returns to the sign-in page', async () => {
    const handled = await service().handle(ACCOUNT_SIGN_OUT_ID);

    expect(handled).toBe(true);
    expect(signOut).toHaveBeenCalledOnce();
    expect(navigateByUrl).toHaveBeenCalledWith('/sign-in');
    expect(assign).not.toHaveBeenCalled();
  });

  it("leaves for the lifekit account's end-session address when sign-out returns one", async () => {
    signOut.mockResolvedValue('https://id.example/oidc/session/end?x=1');

    await service().handle(ACCOUNT_SIGN_OUT_ID);

    expect(assign).toHaveBeenCalledWith('https://id.example/oidc/session/end?x=1');
    expect(navigateByUrl).not.toHaveBeenCalled();
  });

  it('leaves any other id to the layout', async () => {
    expect(await service().handle('/settings')).toBe(false);
  });
});
