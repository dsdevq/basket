import assert from 'node:assert/strict';
import { test } from 'node:test';
import { loadProfileSettings } from '../src/config.ts';
import { hasDisplay, profileLaunch } from '../src/profile-open.ts';
import { parseStoreArg } from '../src/worklist.ts';

test('the opener is a plain browser on the profile directory and nothing else', () => {
  assert.deepEqual(profileLaunch('chrome', '/p/profile'), {
    command: 'google-chrome',
    args: ['--user-data-dir=/p/profile', 'https://www.tesco.ie'],
  });
  assert.equal(profileLaunch('msedge', '/p').command, 'microsoft-edge');
  assert.throws(() => profileLaunch('firefox', '/p'), /cannot be opened by hand/);
});

test('a window needs a display', () => {
  assert.equal(hasDisplay({}), false);
  assert.equal(hasDisplay({ DISPLAY: '' }), false);
  assert.equal(hasDisplay({ DISPLAY: ':0' }), true);
  assert.equal(hasDisplay({ WAYLAND_DISPLAY: 'wayland-0' }), true);
});

test('the opener resolves the profile exactly as the reader does, with no token needed', () => {
  const none = (): string => {
    throw new Error('no file');
  };
  assert.deepEqual(loadProfileSettings({ XDG_STATE_HOME: '/s' }, none), { profileDir: '/s/basket-pc-reader/profile', channel: 'chrome' });
  assert.equal(loadProfileSettings({ BASKET_READER_PROFILE: '/x/prof' }, none).profileDir, '/x/prof');
  assert.equal(loadProfileSettings({}, () => 'BASKET_READER_PROFILE=/from/file\nBASKET_READER_CHANNEL=msedge').profileDir, '/from/file');
});

test('--store picks one shop and nothing else is accepted', () => {
  assert.equal(parseStoreArg([]), undefined);
  assert.equal(parseStoreArg(['--store', 'tesco']), 'tesco');
  assert.equal(parseStoreArg(['--store=aldi']), 'aldi');
  assert.throws(() => parseStoreArg(['--store', 'lidl']), /Usage/);
  assert.throws(() => parseStoreArg(['--store']), /Usage/);
  assert.throws(() => parseStoreArg(['tesco']), /Usage/);
  assert.throws(() => parseStoreArg(['--store', 'tesco', 'x']), /Usage/);
});
