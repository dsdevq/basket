import {signal} from '@angular/core';
import {TestBed} from '@angular/core/testing';
import {provideRouter} from '@angular/router';
import {AppUpdateService} from '@lifekit-hq/core/pwa';

import {SessionStore} from '../auth/session.store';
import {AppShellComponent} from './app-shell.component';

describe('AppShellComponent', () => {
  const updateReady = signal(false);
  const reload = vi.fn().mockResolvedValue(undefined);

  function render(): HTMLElement {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {provide: AppUpdateService, useValue: {updateReady, reload}},
        {provide: SessionStore, useValue: {isOwner: signal(false), user: signal(null)}},
      ],
    });
    const fixture = TestBed.createComponent(AppShellComponent);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => {
    updateReady.set(false);
    reload.mockClear();
  });

  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('renders the shared offline banner and update prompt in the layout notice slot', () => {
    const host = render();

    expect(host.querySelector('[cmnAppLayoutNotice] lk-offline-banner')).not.toBeNull();
    expect(host.querySelector('[cmnAppLayoutNotice] lk-update-prompt')).not.toBeNull();
  });

  it('shows the update prompt once AppUpdateService reports a downloaded version', () => {
    updateReady.set(true);
    const host = render();

    const prompt = host.querySelector('lk-update-prompt') as HTMLElement & {ready: boolean};

    expect(prompt.ready).toBe(true);
  });

  it('activates the new version when the prompt asks to reload', () => {
    const host = render();

    host
      .querySelector('lk-update-prompt')
      ?.dispatchEvent(new CustomEvent('lk-update-prompt-reload'));

    expect(reload).toHaveBeenCalledOnce();
  });
});
