import {TestBed} from '@angular/core/testing';
import {ActivatedRouteSnapshot, type RouterStateSnapshot, UrlTree} from '@angular/router';
import {provideRouter} from '@angular/router';

import {AuthApi} from './auth.api';
import {authGuard, guestGuard, ownerGuard} from './guards';
import type {SessionUser} from './session.model';

class HttpError extends Error {
  constructor(public readonly status: number) {
    super('http');
  }
}

const route: ActivatedRouteSnapshot = new ActivatedRouteSnapshot();
route.data = {};
const state: RouterStateSnapshot = {url: '/', root: route};

async function run(
  guard: typeof authGuard,
  user: SessionUser | null,
  guarded: ActivatedRouteSnapshot = route
): Promise<unknown> {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      {provide: AuthApi, useValue: {session: () => Promise.resolve({authenticated: !!user, user})}},
    ],
  });
  return TestBed.runInInjectionContext(() => guard(guarded, state));
}

async function runOffline(guard: typeof authGuard, guarded = route): Promise<unknown> {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      {provide: AuthApi, useValue: {session: () => Promise.reject(new HttpError(0))}},
    ],
  });
  return TestBed.runInInjectionContext(() => guard(guarded, state));
}

describe('route guards', () => {
  it('sends a signed-out visitor to sign-in', async () => {
    const result = await run(authGuard, null);

    expect(result).toBeInstanceOf(UrlTree);
    expect(String(result)).toBe('/sign-in');
  });

  it('lets a signed-in person through', async () => {
    expect(await run(authGuard, {id: '1', displayName: 'A', role: 'member'})).toBe(true);
  });

  it('keeps signed-in people off the sign-in and join pages', async () => {
    const result = await run(guestGuard, {id: '1', displayName: 'A', role: 'member'});

    expect(String(result)).toBe('/list');
  });

  it('lets guests onto the sign-in and join pages', async () => {
    expect(await run(guestGuard, null)).toBe(true);
  });

  it('keeps members off the owner pages and lets the owner in', async () => {
    expect(String(await run(ownerGuard, {id: '1', displayName: 'A', role: 'member'}))).toBe(
      '/list'
    );
    TestBed.resetTestingModule();
    expect(await run(ownerGuard, {id: '2', displayName: 'B', role: 'owner'})).toBe(true);
  });

  it('sends everyone to the unreachable page when the server cannot be asked', async () => {
    for (const guard of [authGuard, guestGuard, ownerGuard]) {
      TestBed.resetTestingModule();
      expect(String(await runOffline(guard))).toBe('/unreachable');
    }
  });

  it('lets the invite page through when the server cannot be asked, so its #token survives', async () => {
    const join = new ActivatedRouteSnapshot();
    join.data = {tolerateOffline: true};

    expect(await runOffline(guestGuard, join)).toBe(true);
  });
});
