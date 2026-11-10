import {defineScreenBaselines} from '@lifekit-hq/tokens/screen';
import {expect, test} from '@playwright/test';

import {FROZEN_NOW, ROUTES} from './support/screens';

// Every routed page at 390 and 1280, light and dark, against the Linux baselines committed beside this
// spec (`screens.spec.ts-snapshots/`). A baseline is written only by the "Update screen baselines"
// workflow; a plain run compares and never rewrites, and a screen with no baseline fails. So an empty or
// missing baseline directory fails the run. No screen is loosened.
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
      ready: async page => {
        await page.locator('cmn-page').first().waitFor();
        await route.reveal?.(page);
      },
    })),
  }
);
