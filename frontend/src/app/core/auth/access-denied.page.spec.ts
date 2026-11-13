import {TestBed} from '@angular/core/testing';

import {AccessDeniedPage} from './access-denied.page';
import {OidcSignIn} from './oidc-sign-in';

describe('AccessDeniedPage', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('says access was denied and restarts the lifekit sign-in on Try again', () => {
    const start = vi.fn();
    TestBed.configureTestingModule({providers: [{provide: OidcSignIn, useValue: {start}}]});
    const fixture = TestBed.createComponent(AccessDeniedPage);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.textContent).toContain('does not have access to basket');
    (host.querySelector('cmn-button button') as HTMLButtonElement).click();

    expect(start).toHaveBeenCalledOnce();
  });
});
