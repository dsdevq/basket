/** The browser binaries a BASKET_READER_CHANNEL names when it is opened by hand. */
const BINARIES: Record<string, string> = { chrome: 'google-chrome', msedge: 'microsoft-edge' };

export interface ProfileLaunch {
  command: string;
  args: string[];
}

export const PROFILE_START_PAGE = 'https://www.tesco.ie';

/**
 * A plain browser on the reader's profile directory: only `--user-data-dir` and the page to open. No Playwright, no
 * automation flags, no remote debugging, so what the owner browses in is an ordinary Chrome session.
 */
export function profileLaunch(channel: string, profileDir: string): ProfileLaunch {
  const command = BINARIES[channel];
  if (!command) {
    throw new Error(`BASKET_READER_CHANNEL "${channel}" cannot be opened by hand here (expected ${Object.keys(BINARIES).join(' or ')}).`);
  }
  return { command, args: [`--user-data-dir=${profileDir}`, PROFILE_START_PAGE] };
}

/** A window needs a display; agent shells have none, the owner's WSL terminal has one through WSLg. */
export function hasDisplay(env: NodeJS.ProcessEnv): boolean {
  return Boolean(env['DISPLAY'] || env['WAYLAND_DISPLAY']);
}
