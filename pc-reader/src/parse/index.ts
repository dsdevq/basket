import type { Page, ParseResult, StoreId } from '../types.ts';
import { parseAldi } from './aldi.ts';
import { detectBlock } from './block.ts';
import { parseTesco } from './tesco.ts';

/** What one loaded product page amounts to: a refusal, something unreadable, or a reading to post. */
export function readPage(store: StoreId, url: string, page: Page): ParseResult {
  const blocked = detectBlock(page);
  if (blocked) {
    return { kind: 'blocked', why: blocked };
  }
  if (page.status >= 400) {
    return { kind: 'unreadable', why: `HTTP ${page.status}` };
  }
  return store === 'aldi' ? parseAldi(page.html, url) : parseTesco(page.html, url);
}
