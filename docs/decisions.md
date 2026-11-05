# Decisions

Why basket is shaped the way it is. The owner's calls (Q1-Q10, all answered with the recommended option A) are mirrored from the
planning record so this repo carries its own reasons; the M1 section holds the calls made while building.

## Owner's calls (plan questions)

| # | Call | Effect on the code |
|---|---|---|
| Q1 | Her phone reaches basket over a **public Tailscale Funnel**, and the app does its own sign-in. | The app is on the open internet: sign-in, invite and write routes are hardened and rate limited (see M1 below). Funnel serves HTTPS on port **10000** (443 stays with xui). |
| Q2 | Sign-in is a **one-time invite link, then a passkey** (ASP.NET Core Identity, .NET 10). | No passwords anywhere. Invites last 24 hours, work once and are stored hashed. |
| Q3 | **One shared list.** Plan-a-shop is later a view of it, not a second list. | A single `list_item` table, no per-user lists. |
| Q4 | A Lidl price is **this week's offer plus the last price paid**, shown with its date. | M3-M4. Lidl publishes no everyday prices online. |
| Q5 | The **Lidl refresh token lives encrypted in basket's database**, seeded by one paste in Settings - **not in SOPS**. | M4. The token rotates, so a static SOPS file would go stale; ASP.NET Data Protection (keys already persisted for the cookie) is the intended encryption. SOPS still holds the deploy secrets. |
| Q6 | Matching uses **barcodes, store search and size rules**. **No LLM inside the app**; a model can join later through MCP. | M2-M3. The app stays deterministic and cheap to run; cognition stays outside, as in lifekit-dashboard. |
| Q7 | Loyalty prices (Clubcard, Lidl Plus) count. | M2+. |
| Q8 | Backend **.NET 10 minimal API + EF Core + Postgres + Hangfire**, frontend **Angular 22 on lifekit-common**. | The whole of M1. |
| Q9 | Palette seed **Violet `#7c3aed`**. | `frontend/src/seeds/basket.css` is generated from it; Settings offers "back to the app colour". |
| Q10 | **Her list first** (M1), then prices, Plan, Lidl. | M1 has the list, accounts and nothing else. |

## M1 decisions

- **One API project.** `Basket.Api` holds endpoints, Identity, EF and jobs as folders (`Auth`, `Lists`, `Data`,
  `Infrastructure`). M1 has no domain logic worth a separate assembly; split when M2 brings matching.
- **Tables in M1:** the Identity tables (`users`, `roles`, `user_roles`, `user_claims`, `user_logins`, `user_tokens`,
  `role_claims`, `user_passkeys`), `list_item` and `invite`. Snake-case names. Nothing for prices yet.
- **Passkey-only, no recovery in M1.** Identity's built-in passkey support (`MakePasskeyCreationOptionsAsync`,
  `PasskeySignInAsync`) does the WebAuthn work. A lost device is handled by the owner issuing a new invite; there is no
  "add another passkey" screen in M1. Synced passkeys (iCloud Keychain, Google Password Manager) cover the usual case.
  Revisited in M2: see "Several passkeys per person" below.
- **Owner bootstrap without a secret in git.** On first start, while no user exists, the app turns the env value
  `Auth__BootstrapToken` (32+ characters) into an owner invite valid for 24 hours. The first person to open
  `<origin>/join#<token>` becomes the owner. Once a user exists the value is inert.
- **Invite tokens live in the URL fragment** (`/join#<token>`), which browsers never send to servers, so they stay out
  of access logs and proxies. The page reads the fragment, then wipes it from the address bar. The server stores only a
  SHA-256 hash and consumes the invite with one conditional `UPDATE`, so two people cannot use the same link.
- **Cookie and session hardening.** Cookie `__Host-basket`: HttpOnly, Secure, SameSite=Strict, path `/`, no domain,
  30-day sliding expiry; API calls answer 401/403 instead of redirecting. Data Protection keys are persisted on a volume
  so a redeploy does not sign everyone out. The keys sit unencrypted on that private volume (no certificate on the box
  to wrap them); anyone who can read the volume can already read the database.
- **CSRF without tokens.** Every state-changing request must carry an `Origin` header on the allow-list
  (`Auth__Origins`); the cookie is SameSite=Strict as the second layer.
- **No user enumeration.** An unknown, used, expired or revoked invite all answer the same `404 invite_invalid`; a
  failed passkey sign-in answers the same `401 sign_in_failed` whatever the reason.
- **Rate limits** (built-in limiter): sign-in/register endpoints 20 per minute per client address, invite creation 10
  per hour per signed-in user. The client address comes from `X-Forwarded-For` only when the peer is on a private
  network.
- **Security headers** on every response, with a strict CSP; the one inline script (the pre-paint theme script from
  `lifekit-theme-sync`) is allowed by its pinned SHA-256, kept in step by `npm run lint`.
- **Cookies are per host, not per port.** Other services on `lifekit-vps.tail1cb676.ts.net` (other ports) receive
  basket's cookie and could in principle send it back. The `__Host-` prefix and the unique name stop them from setting or
  overriding it, and the cookie value is an encrypted ticket they cannot forge. Accepted for M1; a custom domain
  (later) removes the caveat.
- **Funnel on port 10000, not a path on 443.** Funnel supports 443, 8443 and 10000. 443 is xui's, and a `/basket` path
  on that mapping is not acceptable, so basket takes 10000. WebAuthn's relying-party id is the host without the port;
  the allowed origin includes `:10000`.
- **One container serves both.** The .NET API serves the Angular build from `wwwroot` (one image, one port 3310, no
  nginx). Hashed assets are cached immutably; `index.html` is not.
- **Deploy secrets are an env file outside git** (`~/.config/basket/env`, mode 600) holding the database password and
  the bootstrap token. SOPS/age was left out for M1: the file has three values, all generated on the box, and none
  needs to be shared or recovered from git. Move to SOPS if the set grows or a second operator appears.
- **Memory budget:** app container 256 MB, Postgres 192 MB (`shared_buffers=48MB`, 30 connections). Hangfire runs 2
  workers; its only M1 job purges dead invites nightly at 04:30 Europe/Dublin.
- **No service worker in M1.** The list must show the other person's changes, so it polls every 15 seconds while the
  page is visible (two people do not need SignalR); offline support and push belong with a later milestone.
- **Generic lifekit icons.** The app uses the library's `lk` brand set until basket gets a mark of its own.

## Several passkeys per person

- **Settings > Passkeys** lists the signed-in person's passkeys (name, date added), adds another through the browser's
  passkey prompt (a new phone, a laptop) and removes one. It uses the existing `user_passkeys` table and Identity's
  passkey APIs, so there is no migration. Every endpoint (`/api/passkeys`) acts only on the caller's own passkeys;
  members and the owner are treated alike.
- **Never the last one.** The server refuses to remove a person's only passkey (`409 last_passkey`); the count and the
  delete run under a row lock on the user, so two removals sent together cannot empty the account. The screen also
  hides the remove button while only one is left. Losing every device is still handled by a new owner invite.
- **Adding needs the existing session, no fresh sign-in.** Someone holding a stolen session cookie could register a
  passkey of their own. That is the same reach they already have (the 30-day cookie can mint invites if it is the
  owner's), and the cookie is `__Host-`, HttpOnly and SameSite=Strict; a re-authentication step before adding is the
  hardening to add if the threat model changes.
- **A credential belongs to one account.** Identity rejects a credential id that is already registered, and the
  creation options list the person's existing passkeys as excluded so the same device is not registered twice. At most
  10 passkeys per person; names are free text, trimmed to 40 characters, "Passkey" when left blank.

## M2 decisions (nightly prices)

- **Lidl is read by the server, Tesco and Aldi by the owner's PC.** tesco.ie and aldi.ie answer every request from the
  VPS with Akamai's "Access Denied", plain HTTP clients and an ordinary headless browser alike (checked 2026-11-05,
  `robots.txt` included). The owner decided (2026-11-05): they are read through the PC relay queue, an ordinary browser on
  his home connection, when the PC is on; basket builds no VPS-side Tesco/Aldi scraping and does not work round the block.
  Until a reading arrives their rows say "Waiting for the next PC read". The PC side and a write-only ingest endpoint are a
  separate task; `PriceRecorder` is the write path both the Lidl job and that endpoint use.
- **Lidl's price comes from its offer pages.** lidl.ie shows no everyday prices, only the week's offers. The job reads
  three offer/category pages (`Prices:LidlOfferPages`), whose product tiles carry the price, the Lidl Plus price (no
  login needed), the old price, the promo text and the dates. A linked product on none of them is read from its own page
  (JSON-LD price and stock). Lidl's offers start and end on dates, so a reading keeps `ValidFrom`/`ValidUntil` (the price's own window, the tile's
  store dates only when the price has none) and the page
  shows "from 12 Nov" or "ended" instead of an out-of-date price.
- **Products are linked to shop pages by pasted address, not matched.** M2 stores `product` and one `store_listing` per
  shop (address checked against that shop's product-page shape, nothing else is ever fetched). Barcode/search matching
  with his confirmation is M3.
- **History.** `price_observation` holds one row per listing per Dublin day (a second reading the same day replaces it);
  `price_pull_run` holds how each shop's part of each night went; a listing keeps `last_error` when its latest reading
  failed while the last good price stays on screen with its date.
- **A shop never takes the others down.** The job reads and saves each shop in its own scope; a shop that throws, is
  down, changed its pages or refuses us ends as a `failed`/`blocked` run for that shop only. A refusal (403, 429, an
  access-denied page) stops that shop's requests at once and is never retried.
- **Polite requests.** One request at a time per site with at least 5 seconds between them (`Prices:MinRequestIntervalSeconds`),
  an honest `basket-price-check/1.0` user agent, no retries, no redirects off the site, 4 MB cap. A night is a few dozen
  requests at most. Shops with nothing linked are not asked at all. Job runs at 03:00 Europe/Dublin.
- **Last paid price.** The `paid_price` table and the "Last paid" line exist; with no receipts feed they show "not yet
  connected" for Lidl. The feed needs his one-off Lidl Plus login (M4) and is not built. Tesco Clubcard: the Tesco site
  could not be read, so whether Clubcard prices need a login is not known yet.
