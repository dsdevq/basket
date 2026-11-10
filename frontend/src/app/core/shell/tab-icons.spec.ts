import {signal} from '@angular/core';
import {TestBed} from '@angular/core/testing';
import {AppUpdateService} from '@lifekit-hq/core/pwa';

import {appConfig} from '../../app.config';
import {SessionStore} from '../auth/session.store';
import {AppShellComponent} from './app-shell.component';

describe('tab bar icons', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('draws an icon for every tab, so a missing registration fails here', () => {
    TestBed.configureTestingModule({
      providers: [
        appConfig.providers,
        {provide: AppUpdateService, useValue: {updateReady: signal(false), reload: vi.fn()}},
        {provide: SessionStore, useValue: {isOwner: signal(true), user: signal(null)}},
      ],
    });
    const fixture = TestBed.createComponent(AppShellComponent);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const icons = Array.from(host.querySelectorAll('cmn-icon'));

    expect(icons.length).toBeGreaterThan(0);
    for (const icon of icons) {
      expect(icon.querySelector('svg, img'), icon.outerHTML).not.toBeNull();
    }
  });
});
