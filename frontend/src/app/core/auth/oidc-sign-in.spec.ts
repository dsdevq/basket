import {TestBed} from '@angular/core/testing';

import {BrowserNavigation} from './browser-navigation';
import {OidcSignIn} from './oidc-sign-in';

describe('OidcSignIn', () => {
  const assign = vi.fn();

  function service(): OidcSignIn {
    TestBed.configureTestingModule({providers: [{provide: BrowserNavigation, useValue: {assign}}]});
    return TestBed.inject(OidcSignIn);
  }

  beforeEach(() => {
    sessionStorage.clear();
    assign.mockClear();
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.unstubAllGlobals();
  });

  it('is available when the start address redirects, without following it', async () => {
    const fetchMock = vi.fn().mockResolvedValue({type: 'opaqueredirect', status: 0});
    vi.stubGlobal('fetch', fetchMock);

    expect(await service().isAvailable()).toBe(true);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/auth/oidc/start',
      expect.objectContaining({redirect: 'manual'})
    );
  });

  it('is not available when the server answers 404 or cannot be reached', async () => {
    const oidc = service();
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({type: 'basic', status: 404}));
    expect(await oidc.isAvailable()).toBe(false);

    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('offline')));
    expect(await oidc.isAvailable()).toBe(false);
  });

  it('starts at the server, carrying only a local return address', () => {
    const oidc = service();

    oidc.start('/shelf');
    oidc.start('https://evil.example');
    oidc.start();

    expect(assign.mock.calls.map(c => c[0])).toEqual([
      '/api/auth/oidc/start?returnUrl=%2Fshelf',
      '/api/auth/oidc/start',
      '/api/auth/oidc/start',
    ]);
  });

  it('starts the link flow once until it is forgotten', () => {
    const oidc = service();

    expect(oidc.startLinkOnce('/list')).toBe(true);
    expect(oidc.startLinkOnce('/list')).toBe(false);
    oidc.forgetLinkAttempt();
    expect(oidc.startLinkOnce('/list')).toBe(true);
    expect(assign).toHaveBeenCalledTimes(2);
  });
});
