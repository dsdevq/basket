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
// comparison is deferred (the directory holds no `*-linux.png`) and a notice says so in the CI output.
// The update workflow sets SCREEN_BASELINES=update to register the screens and write the first images.
// The follow-up pull request that commits those first baselines removes this condition and makes the
// gate unconditional: an empty or missing baseline directory then fails CI.
const snapshots = join(__dirname, 'screens.spec.ts-snapshots');
const hasBaselines =
  existsSync(snapshots) && readdirSync(snapshots).some(file => file.endsWith('-linux.png'));

if (!hasBaselines && process.env['SCREEN_BASELINES'] !== 'update') {
  console.warn(
    'screen baselines: comparison deferred until the first Linux baselines (*-linux.png) are committed to e2e/screens.spec.ts-snapshots'
  );
} else {
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
