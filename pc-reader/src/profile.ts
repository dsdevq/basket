import { spawn } from 'node:child_process';
import { mkdir } from 'node:fs/promises';
import { loadProfileSettings } from './config.ts';
import { hasDisplay, profileLaunch } from './profile-open.ts';

/**
 * `npm run profile`: opens the reader's own Chrome profile as a plain browser so the owner can browse tesco.ie in it by
 * hand once (and sign in to Clubcard if they want Clubcard prices). Returns when Chrome is closed.
 */
async function main(): Promise<number> {
  const { profileDir, channel } = loadProfileSettings();
  if (!hasDisplay(process.env)) {
    console.error('No display here (DISPLAY is unset): run `npm run profile` from your own WSL terminal, where WSLg gives Chrome a window.');
    return 1;
  }
  const launch = profileLaunch(channel, profileDir);
  await mkdir(profileDir, { recursive: true });
  console.error(`Browse tesco.ie in the window, accept the cookie banner, sign in to Clubcard only if you want Clubcard prices, then close Chrome (profile: ${profileDir}).`);

  const child = spawn(launch.command, launch.args, { stdio: 'ignore' });
  return await new Promise<number>((done, fail) => {
    child.once('error', (error: NodeJS.ErrnoException) =>
      error.code === 'ENOENT' ? fail(new Error(`${launch.command} is not installed or not on PATH.`)) : fail(error),
    );
    child.once('close', () => done(0));
  });
}

main().then(
  (code) => {
    process.exitCode = code;
  },
  (error: unknown) => {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  },
);
