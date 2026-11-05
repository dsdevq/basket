import {ChangeDetectionStrategy, Component} from '@angular/core';
import {AppearanceSettingsComponent, PageComponent, SettingsSectionComponent} from '@lifekit-hq/ui';

import {PasskeysSectionComponent} from './passkeys.section';

/** Basket's own colour, so the picker can offer "back to the app colour". */
export const BASKET_SEED = '#7c3aed';

@Component({
  selector: 'bk-settings-page',
  imports: [
    AppearanceSettingsComponent,
    PageComponent,
    PasskeysSectionComponent,
    SettingsSectionComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-page kind="list">
      <cmn-settings-section heading="Appearance" caption="On this device">
        <cmn-appearance-settings [appSeed]="seed" />
      </cmn-settings-section>

      <cmn-settings-section heading="Passkeys" caption="How you sign in, one per device">
        <bk-passkeys-section />
      </cmn-settings-section>
    </cmn-page>
  `,
})
export class SettingsPage {
  protected readonly seed = BASKET_SEED;
}
