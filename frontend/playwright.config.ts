import {defineConfig} from '@playwright/test';

// Not 4200 (ng serve) and not 3310 (the API): the box also runs other apps' e2e servers.
const port = Number(process.env['E2E_PORT'] ?? 4301);

export default defineConfig({
  testDir: './e2e',
  reporter: process.env['CI'] ? 'github' : 'list',
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    // page.route answers the API; a registered worker would fetch around it.
    serviceWorkers: 'block',
    launchOptions: {args: ['--no-sandbox', '--disable-dev-shm-usage']},
  },
  // Runs against the production build: `npm run build:prod` first.
  webServer: {
    command: 'node e2e/serve.mjs',
    env: {PORT: String(port)},
    url: `http://127.0.0.1:${port}`,
    reuseExistingServer: false,
    timeout: 15_000,
  },
});
