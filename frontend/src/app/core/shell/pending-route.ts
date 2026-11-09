import type {Route} from '@angular/router';
import type {PageKind, SkeletonShape} from '@lifekit-hq/ui';

import {APP_ROUTES} from '../../app.routes';

/** What the shell draws in the page's place while the session call is out. */
export interface PendingPage {
  readonly title: string;
  readonly kind: PageKind;
  readonly shape: SkeletonShape;
}

const SHELL_ROUTE = APP_ROUTES.find(route => route.path === '' && route.children);

function childFor(segment: string): Route | undefined {
  const children = SHELL_ROUTE?.children ?? [];
  const child = children.find(route => route.path === segment);
  return child?.redirectTo === undefined
    ? child
    : children.find(route => route.path === child.redirectTo);
}

/** The page to hold while the first navigation resolves; null for sign-in, invite and the like (not signed-in pages). */
export function pendingPage(path: string): PendingPage | null {
  const clean = path.split(/[?#]/)[0].replace(/^\/+|\/+$/g, '');
  const title = childFor(clean)?.title;
  return typeof title === 'string' ? {title, kind: 'list', shape: 'list'} : null;
}
