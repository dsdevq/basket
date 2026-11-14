import { loadConfig } from './config.ts';
import { checkIdentity } from './identity-check.ts';

/** `npm run check`: exit 0 when the issuer, the token and basket all work from this machine, 1 otherwise. */
async function main(): Promise<number> {
  const steps = await checkIdentity(loadConfig());
  for (const step of steps) {
    console.log(`${step.ok ? 'ok  ' : 'FAIL'} ${step.name}: ${step.detail}`);
  }
  return steps.length > 0 && steps.every((step) => step.ok) ? 0 : 1;
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
