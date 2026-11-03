import {TestBed} from '@angular/core/testing';
import {provideRouter, Router} from '@angular/router';

import {AuthApi} from './auth.api';
import {SessionStore} from './session.store';
import {UnreachablePage} from './unreachable.page';

function setup(session: ReturnType<typeof vi.fn>) {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), {provide: AuthApi, useValue: {session}}],
  });
  const store = TestBed.inject(SessionStore);
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(UnreachablePage);
  return {fixture, store, navigate, host: fixture.nativeElement as HTMLElement};
}

const retryButton = (host: HTMLElement) =>
  [...host.querySelectorAll('button')].find(b => b.textContent?.includes('Try again'));

describe('UnreachablePage', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('says basket cannot be reached, why, and offers Try again', async () => {
    const {fixture, store, host} = setup(vi.fn().mockRejectedValue({status: 0}));
    await store.ensureLoaded();
    fixture.detectChanges();

    expect(host.textContent).toContain('Cannot reach basket');
    expect(host.textContent).toContain('Detail: no answer from the server');
    expect(retryButton(host)).toBeTruthy();
  });

  it('stays put while the server is still down and goes on once it answers', async () => {
    const session = vi
      .fn()
      .mockRejectedValueOnce({status: 0})
      .mockRejectedValueOnce({status: 502})
      .mockResolvedValue({authenticated: false, user: null});
    const {fixture, store, navigate, host} = setup(session);
    await store.ensureLoaded();
    fixture.detectChanges();

    retryButton(host)?.click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(navigate).not.toHaveBeenCalled();
    expect(host.textContent).toContain('HTTP 502');

    retryButton(host)?.click();
    await fixture.whenStable();
    expect(navigate).toHaveBeenCalledWith('/list');
  });
});
