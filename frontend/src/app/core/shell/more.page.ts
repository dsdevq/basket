import {ChangeDetectionStrategy, Component, computed, inject} from '@angular/core';
import {Router} from '@angular/router';
import {
  ACCOUNT_SIGN_OUT_ID,
  accountMenu,
  type AppLayoutAccount,
  type MenuItem,
  MorePageComponent as MoreKit,
  type NavItem,
} from '@lifekit-hq/ui';

import {SessionStore} from '../auth/session.store';

/** Phone "More" tab: the pages past the tab bar, then the account. The kit is its own `cmn-page`. */
@Component({
  selector: 'bk-more-page',
  imports: [MoreKit],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-more-page
      [items]="items()"
      [tabRoutes]="tabRoutes"
      [account]="account()"
      (itemSelect)="select($event)"
    />
  `,
})
export class MorePage {
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);

  protected readonly tabRoutes = ['/list', '/prices', '/cards'];
  protected readonly items = computed<NavItem[]>(() => {
    const list: NavItem = {label: 'List', icon: 'ShoppingBasket', route: '/list'};
    const prices: NavItem = {label: 'Prices', icon: 'Tag', route: '/prices'};
    const cards: NavItem = {label: 'Cards', icon: 'WalletCards', route: '/cards'};
    return this.session.isOwner()
      ? [list, prices, cards, {label: 'People', icon: 'Users', route: '/people'}]
      : [list, prices, cards];
  });
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

  protected async select(item: MenuItem): Promise<void> {
    if (item.id === ACCOUNT_SIGN_OUT_ID) {
      await this.session.signOut();
      await this.router.navigateByUrl('/sign-in');
    }
  }
}
