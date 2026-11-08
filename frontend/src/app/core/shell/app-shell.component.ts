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
  type MenuItem,
  type NavItem,
} from '@lifekit-hq/ui';

import {SessionStore} from '../auth/session.store';

const LIST: NavItem = {label: 'List', icon: 'ShoppingBasket', route: '/list'};
const PRICES: NavItem = {label: 'Prices', icon: 'Tag', route: '/prices'};
const PEOPLE: NavItem = {label: 'People', icon: 'Users', route: '/people'};

/** The signed-in frame: the lifekit app layout with List and Prices as the tabs and People/Settings behind More. */
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

  protected readonly update = inject(AppUpdateService);

  protected readonly tabRoutes = [LIST.route, PRICES.route];
  protected readonly navItems = computed<NavItem[]>(() =>
    this.session.isOwner() ? [LIST, PRICES, PEOPLE] : [LIST, PRICES]
  );
  protected readonly account = computed<AppLayoutAccount | undefined>(() => {
    const user = this.session.user();
    return user
      ? {
          label: user.displayName,
          name: user.displayName,
          menuItems: accountMenu({settingsRoute: '/settings'}),
        }
      : undefined;
  });

  protected async onMenu(item: MenuItem): Promise<void> {
    if (item.id === ACCOUNT_SIGN_OUT_ID) {
      await this.session.signOut();
      await this.router.navigateByUrl('/sign-in');
    }
  }
}
