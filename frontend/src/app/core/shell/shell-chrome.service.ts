import {computed, inject, Injectable} from '@angular/core';
import {Router} from '@angular/router';
import {
  ACCOUNT_SIGN_OUT_ID,
  accountMenu,
  APPEARANCE_PALETTE_ITEMS,
  type AppLayoutAccount,
  CmnWhatsNewService,
  type CommandPaletteItem,
  type NavItem,
  WHATS_NEW_MENU_ID,
  whatsNewMenuItem,
} from '@lifekit-hq/ui';

import {BrowserNavigation} from '../auth/browser-navigation';
import {SessionStore} from '../auth/session.store';
import {APP_VERSION} from '../version';

const LIST: NavItem = {label: 'List', icon: 'ShoppingBasket', route: '/list'};
const SHELF: NavItem = {label: 'Shelf', icon: 'Store', route: '/shelf'};
const PLAN: NavItem = {label: 'Plan', icon: 'ListChecks', route: '/plan'};
const PRICES: NavItem = {label: 'Prices', icon: 'Tag', route: '/prices'};
const PROMOTIONS: NavItem = {label: 'Promotions', icon: 'BadgePercent', route: '/promotions'};
const CARDS: NavItem = {label: 'Cards', icon: 'WalletCards', route: '/cards'};
const PEOPLE: NavItem = {label: 'People', icon: 'Users', route: '/people'};
const SETTINGS_ROUTE = '/settings';

/** The routes shown as phone tabs; everything else in the nav lists on the More page. */
export const PHONE_TAB_ROUTES: readonly string[] = [
  LIST.route,
  SHELF.route,
  PLAN.route,
  CARDS.route,
];

/** Up to two initials for the avatar: the first letters of the first and last word of the name. */
function initialsOf(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean);
  if (words.length === 0) {
    return '?';
  }
  const first = words[0].charAt(0);
  const last = words.length > 1 ? words[words.length - 1].charAt(0) : '';
  return (first + last).toUpperCase();
}

/**
 * What the shell chrome shows and does, shared by the layout wrapper and the phone More page so the
 * two present the same nav, the same account, the same palette and the same version line: the
 * pages the signed-in person may open, the account with its menu, the command palette, and the
 * handling of a selected account item or palette action (APP-SHELL-STANDARD sections 5 to 7).
 */
@Injectable({providedIn: 'root'})
export class ShellChromeService {
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly navigation = inject(BrowserNavigation);
  private readonly whatsNew = inject(CmnWhatsNewService);

  /** The sidebar and More footer line; the click opens What's new. */
  public readonly versionLabel = `v${APP_VERSION} · What's new`;
  public readonly versionDot = this.whatsNew.hasUnread;
  public readonly tabRoutes = [...PHONE_TAB_ROUTES];

  public readonly navItems = computed<NavItem[]>(() =>
    this.session.isOwner()
      ? [LIST, SHELF, PLAN, PRICES, CARDS, PROMOTIONS, PEOPLE]
      : [LIST, SHELF, PLAN, PRICES, CARDS, PROMOTIONS]
  );

  public readonly account = computed<AppLayoutAccount | undefined>(() => {
    const user = this.session.user();
    return user
      ? {
          label: initialsOf(user.displayName),
          name: user.displayName,
          email: user.email ?? undefined,
          menuItems: accountMenu({
            settingsRoute: SETTINGS_ROUTE,
            extras: [whatsNewMenuItem(this.whatsNew.hasUnread())],
          }),
        }
      : undefined;
  });

  /** The pages, Settings, the appearance entries and Sign out. The layout navigates to a page id itself. */
  public readonly paletteItems = computed<CommandPaletteItem[]>(() => [
    ...this.navItems().map(item => ({
      id: item.route,
      label: item.label,
      icon: item.icon,
      group: 'Pages',
    })),
    {id: SETTINGS_ROUTE, label: 'Settings', icon: 'Settings2', group: 'Pages'},
    ...APPEARANCE_PALETTE_ITEMS,
    {id: ACCOUNT_SIGN_OUT_ID, label: 'Sign out', icon: 'LogOut', group: 'Actions'},
  ]);

  public openWhatsNew(): void {
    this.whatsNew.open();
  }

  /**
   * Handles an account item or palette action the app owns: What's new and Sign out. Pages, Settings
   * and the appearance entries are applied by the layout itself. Returns false for any other id.
   */
  public async handle(id: string): Promise<boolean> {
    if (id === WHATS_NEW_MENU_ID) {
      this.openWhatsNew();
      return true;
    }
    if (id === ACCOUNT_SIGN_OUT_ID) {
      const endSessionUrl = await this.session.signOut();
      if (endSessionUrl) {
        // The lifekit account's own session ends there, then it returns to the sign-in page.
        this.navigation.assign(endSessionUrl);
      } else {
        await this.router.navigateByUrl('/sign-in');
      }
      return true;
    }
    return false;
  }
}
