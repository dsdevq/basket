import {ChangeDetectionStrategy, Component, inject} from '@angular/core';
import {AppearanceSettingsComponent, PageComponent, SettingsSectionComponent} from '@lifekit-hq/ui';

import {SessionStore} from '../../core/auth/session.store';
import {PasskeysSectionComponent} from './passkeys.section';
import {PriceRelaySectionComponent} from './price-relay.section';

/** Basket's own colour, so the picker can offer "back to the app colour". */
export const BASKET_SEED = '#7c3aed';

@Component({
  selector: 'bk-settings-page',
  imports: [
    AppearanceSettingsComponent,
    PageComponent,
    PasskeysSectionComponent,
    PriceRelaySectionComponent,
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

      @if (session.isOwner()) {
        <cmn-settings-section
          heading="Price reader"
          caption="Lets your PC send Tesco and Aldi prices"
        >
          <bk-price-relay-section />
        </cmn-settings-section>
      }
    </cmn-page>
  `,
})
export class SettingsPage {
  protected readonly session = inject(SessionStore);
  protected readonly seed = BASKET_SEED;
}
