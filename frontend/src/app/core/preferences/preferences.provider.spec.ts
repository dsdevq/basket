import {provideHttpClient} from '@angular/common/http';
import {HttpTestingController, provideHttpClientTesting} from '@angular/common/http/testing';
import {TestBed} from '@angular/core/testing';

import type {SessionUser} from '../auth/session.model';
import {SessionStore} from '../auth/session.store';
import {AppPreferencesStore} from './app-preferences.store';
import {provideAppPreferences} from './preferences.provider';

const HER: SessionUser = {id: '2', displayName: 'Her', role: 'member'};
const SERVER_COPY = {appearance: {theme: 'dark', seed: '#112233'}};

function setup(): {session: InstanceType<typeof SessionStore>; http: HttpTestingController} {
  TestBed.configureTestingModule({
    providers: [provideHttpClient(), provideHttpClientTesting(), provideAppPreferences()],
  });
  return {session: TestBed.inject(SessionStore), http: TestBed.inject(HttpTestingController)};
}

describe('provideAppPreferences', () => {
  it('asks the account for nothing while nobody is signed in', () => {
    const {http} = setup();

    TestBed.tick();

    http.expectNone('/api/me/preferences');
    expect(TestBed.inject(AppPreferencesStore).isLoaded()).toBe(false);
  });

  it('reads the signed-in person’s appearance, and the server copy is what the store then holds', async () => {
    const {session, http} = setup();

    session.setUser(HER);
    TestBed.tick();
    http.expectOne('/api/me/preferences').flush(SERVER_COPY);

    const store = TestBed.inject(AppPreferencesStore);
    await vi.waitFor(() => expect(store.isLoaded()).toBe(true));
    expect(store.appearance()).toEqual(SERVER_COPY.appearance);
  });

  it('forgets the copy when the session ends, so the next person starts clean', async () => {
    const {session, http} = setup();
    session.setUser(HER);
    TestBed.tick();
    http.expectOne('/api/me/preferences').flush(SERVER_COPY);
    const store = TestBed.inject(AppPreferencesStore);
    await vi.waitFor(() => expect(store.appearance()).not.toBeNull());

    session.setUser(null);
    TestBed.tick();

    expect(store.appearance()).toBeNull();
    expect(store.isLoaded()).toBe(false);
  });

  it('reads again for the next person who signs in', async () => {
    const {session, http} = setup();
    session.setUser(HER);
    TestBed.tick();
    http.expectOne('/api/me/preferences').flush(SERVER_COPY);
    const store = TestBed.inject(AppPreferencesStore);
    await vi.waitFor(() => expect(store.isLoaded()).toBe(true));
    session.setUser(null);
    TestBed.tick();

    session.setUser({...HER, id: '3', displayName: 'Him'});
    TestBed.tick();

    http.expectOne('/api/me/preferences').flush({appearance: {theme: 'light'}});
    await vi.waitFor(() => expect(store.appearance()).toEqual({theme: 'light'}));
  });
});
