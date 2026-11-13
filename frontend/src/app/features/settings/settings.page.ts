import {DOCUMENT} from '@angular/common';
import {ChangeDetectionStrategy, Component, inject} from '@angular/core';
import {Router} from '@angular/router';
import {
  ACCOUNT_SIGN_OUT_ID,
  AppearanceSettingsComponent,
  ButtonComponent,
  PageComponent,
  SettingsSectionComponent,
} from '@lifekit-hq/ui';

import {SessionStore} from '../../core/auth/session.store';
import {ShellChromeService} from '../../core/shell/shell-chrome.service';
import {APP_VERSION} from '../../core/version';
import {LidlPlusSectionComponent} from './lidl-plus.section';
import {PasskeysSectionComponent} from './passkeys.section';
import {PriceRelaySectionComponent} from './price-relay.section';

/**
 * Settings in the family order (APP-SHELL-STANDARD section 4): Account, Appearance, the owner's
 * workspace, Security, People, About, Sign out. Toggles apply at once; the page has no Save.
 */
@Component({
  selector: 'bk-settings-page',
  imports: [
    AppearanceSettingsComponent,
    ButtonComponent,
    LidlPlusSectionComponent,
    PageComponent,
    PasskeysSectionComponent,
    PriceRelaySectionComponent,
    SettingsSectionComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-page kind="settings">
      @if (session.user(); as user) {
        <cmn-settings-section heading="Account" caption="Managed by your lifekit account">
          <dl class="flex flex-col gap-cmn-3 text-cmn-base" data-testid="settings-account">
            <div class="flex flex-col">
              <dt class="text-cmn-sm text-text-secondary">Name</dt>
              <dd>{{ user.displayName }}</dd>
            </div>
            @if (user.email) {
              <div class="flex flex-col">
                <dt class="text-cmn-sm text-text-secondary">Email</dt>
                <dd>{{ user.email }}</dd>
              </div>
            }
          </dl>
        </cmn-settings-section>
      }

      <cmn-settings-section heading="Appearance" caption="The look of the app">
        <cmn-appearance-settings [appSeed]="seed" />
      </cmn-settings-section>

      @if (session.isOwner()) {
        <cmn-settings-section
          heading="Workspace"
          caption="Lidl Plus and the price reader, for the owner"
        >
          <div class="flex flex-col gap-cmn-6">
            <div class="flex flex-col gap-cmn-3">
              <h3 class="text-cmn-base font-semibold">Lidl Plus</h3>
              <p class="text-cmn-sm text-text-secondary">
                Lets basket sign in to your Lidl Plus account
              </p>
              <bk-lidl-plus-section />
            </div>
            <div class="flex flex-col gap-cmn-3">
              <h3 class="text-cmn-base font-semibold">Price reader</h3>
              <p class="text-cmn-sm text-text-secondary">Lets your PC send Tesco and Aldi prices</p>
              <bk-price-relay-section />
            </div>
          </div>
        </cmn-settings-section>
      }

      <cmn-settings-section heading="Security" caption="Sign-in is handled by your lifekit account">
        <p class="mb-cmn-3 text-cmn-sm text-text-secondary">
          Sign-in is handled by your lifekit account.
        </p>
        <bk-passkeys-section />
      </cmn-settings-section>

      @if (session.isOwner()) {
        <cmn-settings-section heading="People" caption="Who can use this basket">
          <cmn-button (clicked)="openPeople()" variant="secondary" icon="Users">
            Open People
          </cmn-button>
        </cmn-settings-section>
      }

      <cmn-settings-section heading="About" caption="This version of the app">
        <div class="flex flex-col items-start gap-cmn-3">
          <p class="text-cmn-base" data-testid="settings-version">basket v{{ version }}</p>
          <cmn-button (clicked)="chrome.openWhatsNew()" variant="secondary" icon="Sparkles">
            What's new
          </cmn-button>
        </div>
      </cmn-settings-section>

      <cmn-settings-section heading="Sign out" caption="On this device">
        <cmn-button (clicked)="signOut()" variant="secondary" icon="LogOut">Sign out</cmn-button>
      </cmn-settings-section>
    </cmn-page>
  `,
})
export class SettingsPage {
  private readonly router = inject(Router);
  protected readonly session = inject(SessionStore);
  protected readonly chrome = inject(ShellChromeService);
  protected readonly version = APP_VERSION;
  /** Basket's own colour, so the picker can offer "back to the app colour": `--app-seed` in the generated seed theme. */
  protected readonly seed = getComputedStyle(inject(DOCUMENT).documentElement)
    .getPropertyValue('--app-seed')
    .trim();

  protected async openPeople(): Promise<void> {
    await this.router.navigateByUrl('/people');
  }

  protected async signOut(): Promise<void> {
    await this.chrome.handle(ACCOUNT_SIGN_OUT_ID);
  }
}
