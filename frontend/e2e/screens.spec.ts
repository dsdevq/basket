import {existsSync, readdirSync} from 'node:fs';
import {join} from 'node:path';

import {defineScreenBaselines} from '@lifekit-hq/tokens/screen';
import {expect, test} from '@playwright/test';

import {FROZEN_NOW, ROUTES} from './support/screens';

// Every routed page at 390 and 1280, light and dark, against the Linux baselines committed beside this
// spec (`screens.spec.ts-snapshots/`). A baseline is written only by the "Update screen baselines"
// workflow; a plain run compares and never rewrites. No screen is loosened.
//
// The fixture fails a screen that has no baseline, so until the first Linux baselines are committed the
// screens are not registered (the directory holds no `*-linux.png`). The update workflow sets
// SCREEN_BASELINES=update to register them and write the first images; once they are committed every run
// compares. Delete a baseline and its screen fails; delete all of them and the gate silently switches off,
// which CODEOWNERS on `*-linux.png` makes a reviewed change.
const snapshots = join(__dirname, 'screens.spec.ts-snapshots');
const hasBaselines =
  existsSync(snapshots) && readdirSync(snapshots).some(file => file.endsWith('-linux.png'));

if (hasBaselines || process.env['SCREEN_BASELINES'] === 'update') {
  defineScreenBaselines(
    {test, expect},
    {
      routes: ROUTES.map(route => ({
        name: route.name,
        url: route.path,
        prepare: async page => {
          await page.clock.install({time: FROZEN_NOW});
          await route.setup(page);
        },
        ready: page => page.locator('cmn-page').first().waitFor(),
      })),
    }
  );
}
