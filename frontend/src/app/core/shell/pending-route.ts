import type {PageKind, SkeletonShape} from '@lifekit-hq/ui';

/** What the shell draws in the page's place while the session call is out. */
export interface PendingPage {
  readonly title: string;
  readonly kind: PageKind;
  readonly shape: SkeletonShape;
}

const LIST: PendingPage = {title: 'Shopping list', kind: 'list', shape: 'list'};

/** The signed-in pages, by address: the title and skeleton of the page that will take the place. */
const PAGES: Readonly<Record<string, PendingPage>> = {
  '/': LIST,
  '/list': LIST,
  '/prices': {title: 'Prices', kind: 'list', shape: 'list'},
  '/more': {title: 'More', kind: 'list', shape: 'list'},
  '/people': {title: 'People', kind: 'list', shape: 'list'},
  '/settings': {title: 'Settings', kind: 'list', shape: 'list'},
};

/** The page to hold while the first navigation resolves; null for sign-in, invite and the like (not signed-in pages). */
export function pendingPage(path: string): PendingPage | null {
  const clean = path.split(/[?#]/)[0].replace(/(.)\/+$/, '$1');
  return PAGES[clean] ?? null;
}
