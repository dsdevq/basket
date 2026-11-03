import {TestBed} from '@angular/core/testing';

import {AuthApi} from './auth.api';
import type {SessionUser} from './session.model';
import {SessionStore} from './session.store';

const OWNER: SessionUser = {id: '1', displayName: 'Denys', role: 'owner'};

function setup(api: Partial<AuthApi>): InstanceType<typeof SessionStore> {
  TestBed.configureTestingModule({providers: [{provide: AuthApi, useValue: api}]});
  return TestBed.inject(SessionStore);
}

describe('SessionStore', () => {
  it('starts unknown and loads the signed-in user once', async () => {
    const session = vi.fn().mockResolvedValue({authenticated: true, user: OWNER});
    const store = setup({session});

    expect(store.loaded()).toBe(false);
    await store.ensureLoaded();
    await store.ensureLoaded();

    expect(store.user()).toEqual(OWNER);
    expect(store.isSignedIn()).toBe(true);
    expect(store.isOwner()).toBe(true);
    expect(session).toHaveBeenCalledTimes(1);
  });

  it('treats a failed session call as signed out', async () => {
    const store = setup({session: vi.fn().mockRejectedValue(new Error('offline'))});

    await store.ensureLoaded();

    expect(store.loaded()).toBe(true);
    expect(store.isSignedIn()).toBe(false);
  });

  it('knows a member is not an owner', async () => {
    const store = setup({
      session: vi.fn().mockResolvedValue({authenticated: true, user: {...OWNER, role: 'member'}}),
    });

    await store.ensureLoaded();

    expect(store.isSignedIn()).toBe(true);
    expect(store.isOwner()).toBe(false);
  });

  it('signs out locally even when the server call fails', async () => {
    const store = setup({signOut: vi.fn().mockRejectedValue(new Error('offline'))});
    store.setUser(OWNER);

    await expect(store.signOut()).rejects.toThrow('offline');

    expect(store.isSignedIn()).toBe(false);
  });
});
