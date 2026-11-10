import {
  ChangeDetectionStrategy,
  Component,
  computed,
  CUSTOM_ELEMENTS_SCHEMA,
  inject,
} from '@angular/core';
import {Router, RouterOutlet} from '@angular/router';
import {AppUpdateService} from '@lifekit-hq/core/pwa';
import {
  ACCOUNT_SIGN_OUT_ID,
  accountMenu,
  type AppLayoutAccount,
  AppLayoutComponent,
  CmnWhatsNewService,
  type MenuItem,
  type NavItem,
  WHATS_NEW_MENU_ID,
  whatsNewMenuItem,
} from '@lifekit-hq/ui';

import {SessionStore} from '../auth/session.store';

const LIST: NavItem = {label: 'List', icon: 'ShoppingBasket', route: '/list'};
const PRICES: NavItem = {label: 'Prices', icon: 'Tag', route: '/prices'};
const PROMOTIONS: NavItem = {label: 'Promotions', icon: 'BadgePercent', route: '/promotions'};
const PEOPLE: NavItem = {label: 'People', icon: 'Users', route: '/people'};

/** The signed-in frame: the lifekit app layout with List, Prices and Promotions as the tabs and People/Settings behind More. */
@Component({
  selector: 'bk-app-shell',
  imports: [AppLayoutComponent, RouterOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  schemas: [CUSTOM_ELEMENTS_SCHEMA],
  template: `
    <cmn-app-layout
      [navItems]="navItems()"
      [tabRoutes]="tabRoutes"
      [account]="account()"
      [phoneOverlay]="true"
      (avatarMenuSelect)="onMenu($event)"
      brand="basket"
      moreRoute="/more"
    >
      <div cmnAppLayoutNotice>
        <lk-offline-banner />
        <lk-update-prompt
          [ready]="update.updateReady()"
          (lk-update-prompt-reload)="update.reload()"
        />
      </div>
      <router-outlet />
    </cmn-app-layout>
  `,
})
export class AppShellComponent {
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly whatsNew = inject(CmnWhatsNewService);

  protected readonly update = inject(AppUpdateService);

  protected readonly tabRoutes = [LIST.route, PRICES.route, PROMOTIONS.route];
  protected readonly navItems = computed<NavItem[]>(() =>
    this.session.isOwner() ? [LIST, PRICES, PROMOTIONS, PEOPLE] : [LIST, PRICES, PROMOTIONS]
  );
  protected readonly account = computed<AppLayoutAccount | undefined>(() => {
    const user = this.session.user();
    return user
      ? {
          label: user.displayName,
          name: user.displayName,
          menuItems: accountMenu({
            settingsRoute: '/settings',
            extras: [whatsNewMenuItem(this.whatsNew.hasUnread())],
          }),
        }
      : undefined;
  });

  protected async onMenu(item: MenuItem): Promise<void> {
    if (item.id === WHATS_NEW_MENU_ID) {
      this.whatsNew.open();
      return;
    }
    if (item.id === ACCOUNT_SIGN_OUT_ID) {
      await this.session.signOut();
      await this.router.navigateByUrl('/sign-in');
    }
  }
}
