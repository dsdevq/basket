import {expect, type Page, test} from '@playwright/test';

// The invitee's path: an invite link opened in a browser where passkey creation does not work, such as an
// app's built-in browser on a phone. Playwright cannot drive a real iPhone webview, so the stand-in is a page
// whose WebAuthn API exists (so the feature check passes) but whose `navigator.credentials.create` is refused
// the way such a webview refuses it: a `NotAllowedError` with no prompt ever shown.

const TOKEN = 'invite-token-1234567890';

interface Failure {
  [key: string]: unknown;
}

/** Gives the page a WebAuthn API whose create() settles after `afterMs` with `name`. */
async function stubPasskeys(page: Page, how: {afterMs: number; name: string} | 'missing') {
  await page.addInitScript(arg => {
    if (arg === 'missing') {
      Reflect.deleteProperty(window, 'PublicKeyCredential');
      return;
    }
    class FakeCredential {
      public static parseCreationOptionsFromJSON(json: unknown): unknown {
        return json;
      }
      public static parseRequestOptionsFromJSON(json: unknown): unknown {
        return json;
      }
      public static isUserVerifyingPlatformAuthenticatorAvailable(): Promise<boolean> {
        return Promise.resolve(false);
      }
    }
    Object.defineProperty(window, 'PublicKeyCredential', {
      value: FakeCredential,
      configurable: true,
    });
    Object.defineProperty(navigator, 'credentials', {
      configurable: true,
      value: {
        create: () =>
          new Promise((_, reject) =>
            setTimeout(() => reject(new DOMException('not allowed', arg.name)), arg.afterMs)
          ),
      },
    });
  }, how);
}

async function inviteApi(page: Page): Promise<Failure[]> {
  const reports: Failure[] = [];
  await page.route('**/api/session', r => r.fulfill({json: {authenticated: false, user: null}}));
  await page.route('**/api/auth/invite', r => r.fulfill({json: {role: 'member', label: null}}));
  await page.route('**/api/auth/register/options', r => r.fulfill({json: {challenge: 'c'}}));
  await page.route('**/api/auth/passkey-failure', async r => {
    reports.push(r.request().postDataJSON() as Failure);
    await r.fulfill({status: 204});
  });
  return reports;
}

async function createPasskey(page: Page): Promise<void> {
  await page.goto(`/join#${TOKEN}`);
  await page.getByLabel('Your name').fill('Anna');
  await page.getByRole('button', {name: 'Create passkey'}).click();
}

const GENERIC = 'The passkey was not created. Try again when you are ready.';

test.describe('join from a browser that cannot make passkeys', () => {
  test.use({viewport: {width: 390, height: 844}});

  test('a webview that refuses at once is told to open the link in the phone browser', async ({
    page,
  }) => {
    const reports = await inviteApi(page);
    await stubPasskeys(page, {afterMs: 0, name: 'NotAllowedError'});

    await createPasskey(page);

    await expect(page.getByRole('heading', {name: 'Open this link in your browser'})).toBeVisible();
    await expect(page.getByText('Safari or Chrome').first()).toBeVisible();
    await expect(page.getByRole('button', {name: 'Copy link'})).toBeVisible();
    await expect(page.getByText(GENERIC)).toHaveCount(0);
    await expect(page.getByRole('button', {name: 'Create passkey'})).toHaveCount(0);
    await page.screenshot({path: process.env['SHOT'] ?? undefined, fullPage: true});

    await expect.poll(() => reports.length).toBe(1);
    expect(reports[0]).toMatchObject({
      kind: 'blocked',
      errorName: 'NotAllowedError',
      stage: 'create',
      platformAuthenticator: false,
      elapsedBucket: 'instant',
      credentialNull: false,
    });
    expect(JSON.stringify(reports)).not.toContain(TOKEN);
  });

  test('a browser without the passkey API gets the instruction instead of the button', async ({
    page,
  }) => {
    const reports = await inviteApi(page);
    await stubPasskeys(page, 'missing');

    await page.goto(`/join#${TOKEN}`);

    await expect(page.getByRole('heading', {name: 'Open this link in your browser'})).toBeVisible();
    await expect(page.getByRole('button', {name: 'Create passkey'})).toHaveCount(0);
    await expect.poll(() => reports.length).toBe(1);
    expect(reports[0]).toMatchObject({kind: 'no-api', stage: 'page'});
  });

  test('a person who dismisses the prompt still gets the plain try-again notice', async ({
    page,
  }) => {
    const reports = await inviteApi(page);
    await stubPasskeys(page, {afterMs: 1500, name: 'NotAllowedError'});

    await createPasskey(page);

    await expect(page.getByText(GENERIC)).toBeVisible();
    await expect(page.getByRole('heading', {name: 'Open this link in your browser'})).toHaveCount(
      0
    );
    await expect(page.getByRole('button', {name: 'Create passkey'})).toBeVisible();
    await expect(page.getByText('Open this page in Safari or Chrome')).toBeVisible();
    await expect(page.getByRole('button', {name: 'Copy link'})).toBeVisible();
    await expect.poll(() => reports.length).toBe(1);
    expect(reports[0]).toMatchObject({
      kind: 'cancelled',
      errorName: 'NotAllowedError',
      elapsedBucket: 'later',
      credentialNull: false,
    });
  });
});
