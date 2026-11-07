import {defineRouteScans} from '@lifekit-hq/tokens/screen';
import {expect, test} from '@playwright/test';

import {FROZEN_NOW, ROUTES} from './support/screens';

// The design judge and axe-core over every routed page: 390 and 1280 wide, light and dark. A finding
// is fixed in lifekit-common (a component, a token or a page template) and never accepted here, so
// this takes no `exceptions`.
defineRouteScans(
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
