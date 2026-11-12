import {pendingPage} from './pending-route';

describe('pendingPage', () => {
  it('names the page the address will land on', () => {
    expect(pendingPage('/')).toMatchObject({title: 'Shopping list', shape: 'list'});
    expect(pendingPage('/prices/')).toMatchObject({title: 'Prices', kind: 'list'});
    expect(pendingPage('/settings?x=1')).toMatchObject({title: 'Settings'});
    expect(pendingPage('/plan')).toMatchObject({title: 'Plan', kind: 'list'});
    expect(pendingPage('/plan/by-shop')).toMatchObject({title: 'By shop', kind: 'list'});
    expect(pendingPage('/cards')).toMatchObject({title: 'Cards'});
    expect(pendingPage('/cards/tesco')).toMatchObject({title: 'Loyalty card'});
  });

  it('draws no shell for sign-in, invite, unreachable or unknown addresses', () => {
    for (const path of ['/sign-in', '/join', '/unreachable', '/nope', '/cards/tesco/extra']) {
      expect(pendingPage(path)).toBeNull();
    }
  });
});
