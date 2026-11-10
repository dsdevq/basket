import {DOCUMENT} from '@angular/common';
import {ChangeDetectionStrategy, Component, inject} from '@angular/core';
import {AppearanceSettingsComponent, PageComponent, SettingsSectionComponent} from '@lifekit-hq/ui';

import {SessionStore} from '../../core/auth/session.store';
import {PasskeysSectionComponent} from './passkeys.section';
import {PriceRelaySectionComponent} from './price-relay.section';

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
      <cmn-settings-section heading="Appearance" caption="The look of the app">
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
  /** Basket's own colour, so the picker can offer "back to the app colour": `--app-seed` in the generated seed theme. */
  protected readonly seed = getComputedStyle(inject(DOCUMENT).documentElement)
    .getPropertyValue('--app-seed')
    .trim();
}
