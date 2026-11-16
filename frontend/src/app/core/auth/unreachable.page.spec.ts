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

function configureOrigin(content: string | null) {
  document.head.querySelector('meta[name="basket-origin"]')?.remove();
  if (content === null) {
    return;
  }
  const meta = document.createElement('meta');
  meta.name = 'basket-origin';
  meta.content = content;
  document.head.append(meta);
}

describe('UnreachablePage', () => {
  afterEach(() => {
    configureOrigin(null);
    TestBed.resetTestingModule();
  });

  it('says the app moved and links the configured address when run on another origin', () => {
    configureOrigin('https://basket.example.ts.net:3310');
    const {fixture, host} = setup(vi.fn().mockRejectedValue({status: 0}));
    fixture.detectChanges();

    expect(host.textContent).toContain('basket moved');
    expect(host.querySelector('a')?.getAttribute('href')).toBe(
      'https://basket.example.ts.net:3310'
    );
  });

  it('stays unchanged on the configured origin', () => {
    configureOrigin(`${location.origin}/`);
    const {fixture, host} = setup(vi.fn().mockRejectedValue({status: 0}));
    fixture.detectChanges();

    expect(host.textContent).not.toContain('moved');
    expect(host.querySelector('a')).toBeNull();
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
