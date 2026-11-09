import {pendingPage} from './pending-route';

describe('pendingPage', () => {
  it('names the page the address will land on', () => {
    expect(pendingPage('/')).toMatchObject({title: 'Shopping list', shape: 'list'});
    expect(pendingPage('/prices/')).toMatchObject({title: 'Prices', kind: 'list'});
    expect(pendingPage('/settings?x=1')).toMatchObject({title: 'Settings'});
  });

  it('draws no shell for sign-in, invite, unreachable or unknown addresses', () => {
    for (const path of ['/sign-in', '/join', '/unreachable', '/nope']) {
      expect(pendingPage(path)).toBeNull();
    }
  });
});
