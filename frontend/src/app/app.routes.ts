import type {Routes} from '@angular/router';
import type {PageChromeData} from '@lifekit-hq/ui';

import {authGuard, guestGuard, ownerGuard} from './core/auth/guards';

export const APP_ROUTES: Routes = [
  {
    path: 'sign-in',
    title: 'Sign in',
    canActivate: [guestGuard],
    loadComponent: () => import('./features/sign-in/sign-in.page').then(m => m.SignInPage),
  },
  {
    path: 'join',
    title: 'Join',
    canActivate: [guestGuard],
    data: {tolerateOffline: true},
    loadComponent: () => import('./features/join/join.page').then(m => m.JoinPage),
  },
  {
    path: 'unreachable',
    title: 'Cannot reach basket',
    loadComponent: () => import('./core/auth/unreachable.page').then(m => m.UnreachablePage),
  },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./core/shell/app-shell.component').then(m => m.AppShellComponent),
    children: [
      {path: '', pathMatch: 'full', redirectTo: 'list'},
      {
        path: 'list',
        title: 'Shopping list',
        data: {title: 'Shopping list'} satisfies PageChromeData,
        loadComponent: () => import('./features/list/list.page').then(m => m.ListPage),
      },
      {
        path: 'prices',
        title: 'Prices',
        data: {title: 'Prices'} satisfies PageChromeData,
        loadComponent: () => import('./features/prices/prices.page').then(m => m.PricesPage),
      },
      {
        path: 'promotions',
        title: 'Promotions',
        data: {title: 'Promotions'} satisfies PageChromeData,
        loadComponent: () =>
          import('./features/promotions/promotions.page').then(m => m.PromotionsPage),
      },
      {
        path: 'cards',
        title: 'Cards',
        data: {title: 'Cards'} satisfies PageChromeData,
        loadComponent: () => import('./features/cards/cards.page').then(m => m.CardsPage),
      },
      {
        path: 'cards/:store',
        title: 'Loyalty card',
        data: {title: 'Loyalty card', parent: '/cards'} satisfies PageChromeData,
        loadComponent: () => import('./features/cards/card.page').then(m => m.CardPage),
      },
      {
        path: 'more',
        title: 'More',
        data: {title: 'More'} satisfies PageChromeData,
        loadComponent: () => import('./core/shell/more.page').then(m => m.MorePage),
      },
      {
        path: 'people',
        title: 'People',
        canActivate: [ownerGuard],
        data: {title: 'People', parent: '/more'} satisfies PageChromeData,
        loadComponent: () => import('./features/people/people.page').then(m => m.PeoplePage),
      },
      {
        path: 'settings',
        title: 'Settings',
        data: {title: 'Settings', parent: '/more'} satisfies PageChromeData,
        loadComponent: () => import('./features/settings/settings.page').then(m => m.SettingsPage),
      },
    ],
  },
  {path: '**', redirectTo: ''},
];
