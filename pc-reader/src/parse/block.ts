import type { Page } from '../types.ts';

const CHALLENGE_TITLES = [/access denied/i, /pardon our interruption/i, /just a moment/i, /attention required/i, /are you a (human|robot)/i, /performing security verification/i];
const CHALLENGE_BODY = [/errors\.edgesuite\.net/i, /captcha/i, /press (&|&amp;)? ?hold/i, /unusual traffic/i, /verify you are (a )?human/i, /challenges\.cloudflare\.com/i, /cf-chl/i];

/**
 * Whether the store refused this load or put a bot challenge in front of it (Akamai's "Access Denied" page, Cloudflare's
 * "Just a moment..." challenge, a 403/429, a CAPTCHA or an interstitial). The reader stops that store for the run and
 * never works round it. Returns why, or null when the page looks like an ordinary answer.
 */
export function detectBlock(page: Page): string | null {
  if (page.status === 403 || page.status === 429) {
    return `HTTP ${page.status}`;
  }
  const title = /<title[^>]*>([\s\S]*?)<\/title>/i.exec(page.html)?.[1]?.trim() ?? '';
  const titleHit = CHALLENGE_TITLES.find((re) => re.test(title));
  if (titleHit) {
    return `challenge page "${title}"`;
  }
  // A real product page is far larger than a challenge; the body markers only count on a short document.
  if (page.html.length < 20_000 && CHALLENGE_BODY.some((re) => re.test(page.html))) {
    return 'challenge page';
  }
  return null;
}
