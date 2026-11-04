import {ChangeDetectionStrategy, Component, computed, inject} from '@angular/core';
import {Router} from '@angular/router';
import {
  ACCOUNT_SIGN_OUT_ID,
  accountMenu,
  type AppLayoutAccount,
  type MenuItem,
  MorePageComponent as MoreKit,
  type NavItem,
  PageComponent,
} from '@lifekit-hq/ui';

import {SessionStore} from '../auth/session.store';

/** Phone "More" tab: the pages past the tab bar, then the account. */
@Component({
  selector: 'bk-more-page',
  imports: [MoreKit, PageComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cmn-page kind="list">
      <cmn-more-page
        [items]="items()"
        [tabRoutes]="tabRoutes"
        [account]="account()"
        (itemSelect)="select($event)"
      />
    </cmn-page>
  `,
})
export class MorePage {
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);

  protected readonly tabRoutes = ['/list'];
  protected readonly items = computed<NavItem[]>(() => {
    const list: NavItem = {label: 'List', icon: 'ShoppingBasket', route: '/list'};
    return this.session.isOwner()
      ? [list, {label: 'People', icon: 'Users', route: '/people'}]
      : [list];
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
