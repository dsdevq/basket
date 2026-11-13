import {TestBed} from '@angular/core/testing';
import {provideRouter} from '@angular/router';
import {CmnWhatsNewService} from '@lifekit-hq/ui';

import {AuthApi} from '../../core/auth/auth.api';
import {SessionStore} from '../../core/auth/session.store';
import {ShellChromeService} from '../../core/shell/shell-chrome.service';
import {LidlPlusApi} from './lidl-plus.api';
import {PriceRelayApi} from './price-relay.api';
import {SettingsPage} from './settings.page';

function sessionStub(role: 'owner' | 'member') {
  const user = {id: 'u1', displayName: 'Ada Lovelace', email: 'ada@example.org', role};
  return {user: () => user, isOwner: () => role === 'owner'};
}

const pending = () => new Promise<never>(() => undefined);

function render(role: 'owner' | 'member') {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      {provide: AuthApi, useValue: {passkeys: pending}},
      {provide: LidlPlusApi, useValue: {status: pending}},
      {provide: PriceRelayApi, useValue: {tokens: pending}},
      {provide: SessionStore, useValue: sessionStub(role)},
      {provide: ShellChromeService, useValue: {openWhatsNew: vi.fn(), handle: vi.fn()}},
      {provide: CmnWhatsNewService, useValue: {open: vi.fn(), hasUnread: () => false}},
    ],
  });
  const fixture = TestBed.createComponent(SettingsPage);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function headings(host: HTMLElement): string[] {
  return Array.from(host.querySelectorAll('cmn-settings-section h2')).map(h =>
    (h.textContent ?? '').trim()
  );
}

describe('SettingsPage', () => {
  it('lists the owner sections in the family order', () => {
    const host = render('owner');
    expect(headings(host)).toEqual([
      'Account',
      'Appearance',
      'Workspace',
      'Security',
      'People',
      'About',
      'Sign out',
    ]);
  });

  it('hides the workspace and People sections from a member', () => {
    const host = render('member');
    expect(headings(host)).toEqual(['Account', 'Appearance', 'Security', 'About', 'Sign out']);
  });

  it('shows the name and email read-only', () => {
    const host = render('member');
    const account = host.querySelector('[data-testid="settings-account"]');
    expect(account?.textContent).toContain('Ada Lovelace');
    expect(account?.textContent).toContain('ada@example.org');
  });
});
