import pkg from '../../../package.json';
import {APP_VERSION} from './version';

describe('APP_VERSION', () => {
  it('is the version in package.json', () => {
    expect(APP_VERSION).toBe(pkg.version);
  });
});
