import {ChangeDetectionStrategy, Component} from '@angular/core';
import {AppearanceSettingsComponent, SettingsSectionComponent} from '@lifekit-hq/ui';

/** Basket's own colour, so the picker can offer "back to the app colour". */
export const BASKET_SEED = '#7c3aed';

@Component({
  selector: 'bk-settings-page',
  imports: [AppearanceSettingsComponent, SettingsSectionComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {class: 'block px-cmn-4 pb-cmn-4'},
  template: `
    <cmn-settings-section heading="Appearance" caption="On this device">
      <cmn-appearance-settings [appSeed]="seed" />
    </cmn-settings-section>
  `,
})
export class SettingsPage {
  protected readonly seed = BASKET_SEED;
}
