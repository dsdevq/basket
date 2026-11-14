# Decisions

Why basket is shaped the way it is. The owner's calls (Q1-Q10, all answered with the recommended option A) are mirrored from the
planning record so this repo carries its own reasons; the M1 section holds the calls made while building.

## Owner's calls (plan questions)

| # | Call | Effect on the code |
|---|---|---|
| Q1 | **Superseded 2026-11-12 (see "Org sign-in").** Her phone reaches basket over a **public Tailscale Funnel**, and the app does its own sign-in. | The app is on the open internet: sign-in, invite and write routes are hardened and rate limited (see M1 below). Funnel serves HTTPS on port **10000** (443 stays with xui). |
| Q2 | **Superseded 2026-11-12 (see "Org sign-in"): basket signs in through the org Logto.** Sign-in is a **one-time invite link, then a passkey** (ASP.NET Core Identity, .NET 10). | No passwords anywhere. Invites last 24 hours, work once and are stored hashed. |
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
- **Deploy secrets are SOPS + age** (`docker/.env.sops`, committed encrypted; `docker/deploy.sh` decrypts it on the box
  with firstmate's key, as finance-sentry does). M1 kept an env file outside git (`~/.config/basket/env`) because it had
  three values, all generated on the box, and named the OIDC client secret (issued in Logto, not generated on the box) as
  the trigger to move. The move started on 2026-11-12 as part of deploying like the rest of the family; until the encrypted
  file is committed `deploy.sh` still falls back to the old env file (temporary, `docs/vps-setup.md`).
- **The image is built once by CI and pulled by SHA.** The `image builds` job in `ci.yml` builds `ghcr.io/dsdevq/basket`
  natively on arm64 and, on a push to `main`, publishes `:<full commit SHA>`. The deploy workflow starts when that CI run
  succeeds and pulls that SHA, so the box builds nothing and needs no `NODE_AUTH_TOKEN`; a rollback is a deploy of an
  older SHA. The prod compose file has no `build:` block; `IMAGE_TAG` is required.
- **Memory budget:** app container 256 MB, Postgres 192 MB (`shared_buffers=48MB`, 30 connections). Hangfire runs 2
  workers; its only M1 job purges dead invites nightly at 04:30 Europe/Dublin.
- **Polling, not SignalR or push.** The list must show the other person's changes, so it polls every 15 seconds while the
  page is visible (two people do not need SignalR); push belongs with a later milestone. M1 had no service worker; the
  shell-only one is "Offline shell and update prompt" below.
- **Generic lifekit icons.** The app uses the library's `lk` brand set until basket gets a mark of its own.

## Org sign-in: basket is a Logto client like the rest of the family (2026-11-12, supersedes Q2)

Owner, 2026-11-12: "Currently we have some passkeys there, but my expectation for basket was that we have logto gateway, why dont
we use it ? Why do we invent login ? Basket should be just in our ecosystem, reflecting 90% of stuff, including login, settings
and so on. Design and stuff like that. The only difference is domain basket application is about. Everything else - it should be
the same as our apps. Like its one family bro, family of products. Same UI, same UX, same library, everything is the same. Only
difference is basket use cases and business logic." And on reach: "my gf is on tailnet bro, we have her account for finance
sentry." Q2 (invite link, then a passkey) was chosen because her phone was assumed to be off the tailnet; it is not, so the
reason is gone and the org identity provider is the sign-in. This is the backend step (A2), and A3 below links the two existing
passkey accounts; the family sign-in pages (step A4, below) have landed, while the tailnet-only cutover and removing the passkey code are separate steps that follow.

- **Off until configured.** `Auth:Oidc` (compose: `AUTH_OIDC_AUTHORITY`, `AUTH_OIDC_CLIENT_ID`, `AUTH_OIDC_CLIENT_SECRET`,
  `AUTH_OIDC_PUBLIC_BASE_URL`, `AUTH_OIDC_BACKCHANNEL_AUTHORITY`, all empty by default) switches it on only when the first four
  are set. Until then nothing is registered, the `/api/auth/oidc/*` endpoints answer 404 and sign-in is passkey-only exactly as
  before. The fleet path (`FleetAgent:*`) is a separate scheme and is unchanged.
- **The same shape as finance-sentry.** An OpenID Connect scheme named `lifekit` (code flow with PKCE, confidential client, scopes
  `openid email profile roles`, response mode `query` so sign-in stays a GET navigation and the CSP's `form-action 'self'` needs
  no exception). `OidcLoginOptions`, `OidcLoginExtensions` and `OidcAuthorityRewriteHandler` are ported from there: the container
  reaches Logto at `http://logto:3001/oidc` (`AUTH_OIDC_BACKCHANNEL_AUTHORITY`) for discovery, keys, token and userinfo, while the
  issuer and the browser's authorize redirect stay on the tailnet address (`AUTH_OIDC_AUTHORITY`). The accepted issuer is pinned to
  that address, as the fleet path pins its own.
- **Endpoints.** `GET /api/auth/oidc/start?returnUrl=` challenges the scheme; the provider returns to `/api/auth/oidc/signin` (the
  redirect URI to register; the handler completes the code exchange) and the handler hands over to `GET /api/auth/oidc/callback`,
  which decides the account. A return URL must be an absolute path on this app (`//host` and backslash forms are ignored).
  `POST /api/auth/sign-out` ends the basket session as before; when the session came from the provider it answers `200
  {endSessionUrl}` (RP-initiated logout with the `id_token_hint` kept at sign-in) for the SPA to navigate to, otherwise `204`.
  A refused sign-in redirects to `/access-denied`; a failed handshake to `/sign-in?error=OIDC_FAILED`; the provider returns to
  `/sign-in?info=signed_out` after sign-out. Those pages are the frontend step's (A4, below).
- **Which account.** A linked `sub` (an Identity `user_logins` row, provider `lifekit`) signs in, with or without the role. Otherwise,
  if the request also carries a **valid basket session** for an account with no `lifekit` login, that account takes the `sub` and the
  person continues as it (the passkey accounts that predate the provider; see "Linking the passkey accounts" below). Otherwise a
  **verified** email that matches exactly one account links to it, unless that account is already linked to another `sub`.
  Otherwise holding the provider role `basket` (`RequiredRole`) creates a Member, display name from `name`, else the email's local
  part, **but only while every usable account already has a `lifekit` login**. Otherwise access is denied. An unverified email never links
  and never creates, even with the role; two accounts holding the same email are never guessed between. Existing accounts are not
  role-checked, as in finance-sentry.
- **The email.** It is stored on `users.email` when an account is created or linked from the provider, and a linked account that
  has none takes the provider's verified one (it never replaces one). `/api/session` returns `email` (null for an account that has
  none, such as a passkey-only one). No migration: `users.email` and `user_logins` are already in the Identity schema.
- **Basket's own session stays.** After the callback the person holds the same `__Host-basket` Identity cookie (30-day sliding) as
  a passkey sign-in; finance-sentry's access JWT and refresh cookie are not ported, they serve its JwtBearer default scheme and
  MCP tokens. The only extra cookie that outlives the handshake is `__Host-basket-oidc-id`, the ID token (data-protected,
  HttpOnly, Strict) kept so sign-out can send the hint; sign-out deletes it.
- **Cookies of their own.** Browsers share cookies across the ports of one host, and finance-sentry lives on the same host, so the
  external-login cookie (`basket-oidc-external`) and the OIDC correlation (`basket-oidc-correlation.`) and nonce
  (`basket-oidc-nonce.`) cookies are renamed from the defaults other apps use. The correlation and nonce cookies are SameSite=Lax
  because the provider returns by a cross-site GET.
- **Linking the passkey accounts (2026-11-12, step A3).** The two existing accounts have no email, so the email match cannot find
  them, and a role-holder who matched nothing would have been created as a second account beside the old one. Instead the
  account's own session is the proof of whose it is (Logto vouches for the person, the `__Host-basket` cookie for the account),
  the standard Identity "add an external login" pair. The cookie is SameSite=Strict, and the provider and basket share a site
  (the same tailnet host, different ports), so it reaches the callback on the redirect from the provider.
  - **At the callback**, a valid session for an account with no `lifekit` login gets the provider login added and continues as that
    account, passkeys untouched. An account that already has a `lifekit` login never takes a second, and a locked-out account takes
    none; the `sub` is then judged as if there were no session. A `sub` already linked to another account signs in as that account.
  - **No duplicates.** While any usable (not locked-out) account has no `lifekit` login, a role-holder who matches no account is not created: the callback
    redirects to `/link-required`, a page (the frontend step's, A4 below, like `/access-denied`) that says to open basket on the phone they
    already use, or to sign in with the passkey first. A locked-out account never blocks: it cannot link, so nobody could clear it. Once every usable account is linked, creation works as before.
  - **`/api/session` reports `linkRequired: true`** for a session whose account has no `lifekit` login once the provider path is
    configured (the passkey registration response does too: a new passkey account has none). It is always `false` when the path is
    off. The SPA uses it to send the person through the provider once.
  - No migration: the link is an Identity `user_logins` row.
- **Family sign-in pages (step A4).** The frontend follows finance-sentry's `/login`. `/sign-in` asks `/api/auth/oidc/start` (without
  following the redirect: a redirect means on, `404` means off) and, when on, forwards to it. It is held instead of forwarding by
  `?info=` (`signed_out`, `link_required`) or `?error=`, so a signed-out or failed attempt shows its message and cannot loop. While
  the provider is not live for everyone it keeps a secondary "Sign in with a passkey"; with the provider off it is the primary action
  and the page is as before. `/access-denied` says the account has no access and its "Try again" restarts the sign-in. `/link-required`
  points to the phone already in use or to the passkey sign-in (`/sign-in?info=link_required`). `linkRequired` from the session makes the
  route guard start the same flow once per browser tab session, returning to the page asked for. Sign-out navigates to the returned
  `endSessionUrl` and clears the session store; with no `endSessionUrl` it goes to `/sign-in?info=signed_out`, which holds, so a
  Logto session that is still alive (finance-sentry on the same Logto) cannot sign the person straight back in. A `401` redirect goes
  to plain `/sign-in` (an expiry, so forwarding is right). Screen baselines are re-rendered by the "Update screen baselines" workflow.
- **Tests** use an in-process fake provider (`FakeIdentityProvider`): a signing key generated per run, discovery, keys, the token
  exchange with the client secret and PKCE checked, and userinfo, all on the back-channel host through the real rewrite handler.
  They run the real OpenID Connect handler end to end and cover role present, role absent, unverified email, a verified email
  linking, a linked `sub`, a second `sub` refused, duplicate emails, a session linking exactly its own account, nothing created while
  an account is unlinked (a locked-out one not counting), `linkRequired` on the session, a forged or foreign-issuer callback, the cookie names, the
  back channel, the logout URL with the hint, and the path off.

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
  Until a reading arrives their rows say "Waiting for the next PC read". The PC side is a separate fleet task; the write-only
  ingest endpoint is below, and `PriceRecorder` is the write path both the Lidl job and that endpoint use.
- **Lidl's price comes from its offer pages.** lidl.ie shows no everyday prices, only the week's offers. The job reads
  three offer/category pages (`Prices:LidlOfferPages`), whose product tiles carry the price, the Lidl Plus price (no
  login needed), the old price, the promo text and the dates. A linked product on none of them is read from its own page
  (JSON-LD price and stock). Lidl's offers start and end on dates, so a reading keeps `ValidFrom`/`ValidUntil` (the
  price's own window, the tile's store dates only when the price has none) and the page shows "from 12 Nov" or "ended"
  instead of an out-of-date price.
  If an offer page could not be read or came back with no priced products, a product page without a price is a failed
  reading (the last good price stays), not "no price", since the product may be on offer on the missing page.
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

## Price ingest decisions (the PC relay's door)

- **One write-only endpoint, `POST /api/ingest/prices`.** The PC-side reader posts `{"prices": [...]}`, up to 200 readings
  (256 KB), each naming `store` (`tesco` or `aldi`), the product page `url` that is already linked to a product, and
  what the page said (`price`, `wasPrice`, `loyaltyPrice`, `loyaltyScheme`, `promoText`, `validFrom`, `validUntil`,
  `inStock`, `title`, and optionally `imageUrl` and `category`, see "The product photo" and "Each store page keeps its category" below). Readings go through `PriceRecorder`, so they become the same one-per-listing-per-Dublin-day
  `price_observation` rows as the nightly Lidl job's, and the Prices page shows them the same way. Two products may link
  the same page; a reading for that URL is recorded against each of them and counts as one accepted reading.
- **Dated by the server.** The reader cannot set `observedAt`: the server stamps the moment of the post. A reader that
  was off for days and posts a stale batch therefore cannot overwrite a newer reading of the same day with an older one,
  and a replayed batch just replaces that day's reading with identical values.
- **Each reading stands alone.** A bad one is refused with a stable reason code (`unknown_store`, `store_not_ingested`
  for Lidl, `url_invalid`, `unknown_listing`, `price_invalid` for zero, negative, over 9999.99 or sub-cent amounts,
  `no_price`, `dates_invalid`, `duplicate` for a URL already in the same batch) and the rest of the batch is kept.
  The answer is 200, or 422 when nothing in the batch was usable. Each shop's part of a batch also becomes a
  `price_pull_run` (`ok`, `partial`, `failed`), so a misconfigured reader shows as a warning on the Prices page. An
  out-of-stock reading with no price is valid; an in-stock one with no price is not.
- **Nothing is readable with the token.** The endpoint's answer carries only per-reading accept/refuse codes. Telling the
  reader which URLs are tracked (its work list) is deliberately not part of this endpoint; the one thing a token holder
  can learn is whether a URL it posted is linked (`unknown_listing`).
- **The token.** The owner makes it in Settings > Price reader (owner only). 256 random bits behind a `bsk_`
  prefix, shown once; only its SHA-256 is stored (`ingest_token`, the one migration of this change, additive). It has one
  scope: it opens this endpoint and nothing else, and every other endpoint ignores it. Revoking deletes the row and takes
  effect on the next request. Requests are rate limited per client
  address (30 a minute) before the token is even checked.
- **No cookie, no Origin.** The reader is a program: it sends no `Origin`, so the origin check skips `/api/ingest/...`
  (and, for a request carrying a bearer header, `/api/products`; see "Fleet identity on the machine endpoints"). The endpoint never reads the session cookie and answers 401 without a valid token, so a forged
  cross-site request has nothing to ride on, and a signed-in browser session alone is refused. A token sent to any other
  endpoint is refused too: nothing else looks at it.
- **Test data.** Tesco and Aldi block the VPS, so the ingest fixture (`Fixtures/ingest-batch.json`) is a batch written to
  the contract with realistic prices, not a page recorded from a shop.

## Tesco profile decision

- **2026-11-07: one accepted manual step, like the Lidl Plus login.** tesco.ie refuses every fresh automation-driven Chrome
  profile with Akamai "Access Denied", while a long-used profile was served a real page. The reader keeps its own profile;
  the owner opens it by hand once with `npm run profile` (plain Chrome on that directory, no Playwright or automation
  flags), browses tesco.ie, accepts the cookie banner and signs in to Clubcard only if they want Clubcard prices, then
  closes Chrome. `npm start -- --store tesco` then retries Tesco once from that profile. If Akamai still refuses, the
  reader stops and records it as before; using the owner's personal Chrome with an Allow click is the owner's call, not
  the reader's. Nothing warms, imitates or hides the automation, and the reader never handles credentials.

## PC reader decisions (2026-11-07)

- **A separate small program, `pc-reader/`.** Node/TypeScript run by Node 24 directly (no build step), two dependencies:
  `playwright-core` to drive the PC's installed Chrome and `node-html-parser`. The parsers take a page's HTML and know
  nothing of the browser, so they are tested against saved pages (`pc-reader/test/fixtures`, recorded or marked
  hand-written). It posts through the same `POST /api/ingest/prices` as any client; nothing server-side changed.
- **The work list is a local file the owner keeps** (for a `bsk_` token; with the fleet identity it comes from the catalog,
  see "The PC reader signs in as the fleet"). The endpoint tells a token holder nothing about what is tracked, so
  the reader reads `{store, url}` entries from `pc-reader/urls.local.json` (ignored by git), checked against the same
  product-page shape the server accepts. A page not linked in basket comes back `unknown_listing`. A server-provided work
  list (a token-scoped read endpoint) would widen what the token can learn and is the owner's separate decision.
- **Ordinary browser, nothing hidden.** Installed Chrome with a profile of its own (never the owner's everyday one), visible by
  default, no stealth plugin, fingerprint or user-agent change, proxy or CAPTCHA solving. Playwright's automation is not
  concealed; a shop may refuse it.
- **One request at a time per shop, at least 5 seconds apart, no retries**, shops one after the other: the same politeness
  as the nightly Lidl job. A shop's readings are posted when its pages are done; a batch is cut at 200 readings; a refused
  batch (bad token, 429) ends that shop's posting and is not retried, since the next run is the retry.
- **A refusal stops that shop for the run.** A 403/429, Akamai's "Access Denied", a CAPTCHA or an interstitial ends the
  shop's part (the rest of its pages are not asked, the refused one is not retried), what was read before it is still
  posted, the other shop carries on, and the summary names the blocked page and how many were not asked (exit code 2).
  This is reported, never worked round.
- **Checked live on 2026-11-07.** aldi.ie served a clean Chrome profile on this PC (and the reader itself, run headless); its product pages carry the price,
  stock and name as schema.org JSON-LD and the was price and discount badge in the `base-price` block (parsers built on
  two recorded pages, one on offer). tesco.ie answered the same clean Chrome with Akamai's "Access Denied", so the Tesco
  parser (JSON-LD price, name and stock only; no was price, Clubcard price or offer dates, since text on a page that
  cannot be checked may belong to a carousel or footer) is unverified against a live page and was not retried. Whether Tesco serves the owner's own PC with an ordinary visible window is not known until he runs it.
- **Not scheduled.** Nothing is installed to run the reader; the README notes what a systemd user timer would need.

## Tesco unreadable from the shop profile too (2026-11-07)

- **What was tried, read-only and politely (5 s or more between loads, no sign-in):** a real Windows Chrome 156 on the
  owner's home connection, started by hand from WSL on a profile made for the reader (a new `--user-data-dir`, nothing of
  the owner's in it, remote debugging on that profile only, no Allow dialog) and driven over DevTools. tesco.ie
  answered the product page `https://www.tesco.ie/shop/en-IE/products/323785291` and then its home page with Akamai's
  "Access Denied" (HTTP 403); a real Linux Chrome on its own profile (headless, there is no display in the agent shell)
  got the same. The same PC's Aldi pages load fine. Earlier the same day the owner's own, long-used Chrome profile was
  served a real Tesco product page (title, JSON-LD and a Clubcard string present), so the home connection is not what is
  refused: a fresh automation-driven profile is.
- **Decision: nothing more is built to get Tesco.** The reader does not warm profiles up, imitate a person, hide that it
  is driven, reuse the owner's browser or try other machines; the only road left would be one of those. The shop-profile
  driver was not written. Tesco stays "Waiting for the next PC read" and the reader stops Tesco on its first refusal, as
  built. Whether the owner wants to run the reader against a profile he has used himself (his call, and a new decision) or
  to leave Tesco unread is open.
- **Found on the way:** Tesco's canonical product path is now `/shop/en-IE/products/<id>`; the `/groceries/en-IE/` form
  redirects to it. At the time basket accepted only the `/groceries/en-IE/products/<id>` shape, so a `/shop/` address
  was refused as `url_invalid`; resolved in "Tesco product addresses: /shop and /groceries are one key" below.

## Tesco product addresses: /shop and /groceries are one key (2026-11-07)

- **Both path forms are accepted, normalised to the stored `/groceries` form.** tesco.ie's canonical product address is
  now `https://www.tesco.ie/shop/en-IE/products/<id>`; the older `/groceries/en-IE/products/<id>` redirects to it.
  `Stores.TryNormalizeProductUrl` takes either for Tesco and returns the `/groceries` form, so a pasted or ingested
  `/shop` address matches the `store_listing` row that already exists and no stored row moves.
- **Moving stored rows to the `/shop` form would be a migration** (listings, and any reader holding the old form) and
  is a separate owner decision, not part of this change.


## Dunnes: the server cannot read it, the PC reader does (2026-11-08)

- **Owner's words:** "I shop in 4 different stores. Lidl, aldi, tesco, dunnes. I want to have one app where I could sort
  things out." This adds Dunnes to the stores (it changes the 2026-10-29 call of Lidl, Aldi and Tesco for now). Epic #21,
  checked in #28 before assuming, as Tesco was.
- **Borrowed pattern: the PC relay already built for Tesco and Aldi** (an ordinary browser on the owner's home
  connection reads the pages and posts them to the write-only `POST /api/ingest/prices`). Nothing new is invented; no page
  or screen changes, so no baseline moves.
- **What was requested, from this VPS (IPv6 `2a03:4000:63:431:...`, 2026-11-08 01:23-01:25 UTC), read-only, one request
  at a time, 6 s apart or more, honest `basket-price-check/1.0` user agent, no cookies kept, no sign-in:**

  | Request | What came back |
  |---|---|
  | `https://www.dunnesstores.com/robots.txt` | 200, 729 bytes. Disallows search, account, basket and filter paths; product pages (`/<name>-<id>/p`) are not disallowed. |
  | `https://www.dunnesstores.com/` | 200, 2.8 MB page (VTEX storefront). |
  | `https://www.dunnesstores.com/sitemap.xml`, `sitemap/category-0.xml` | 200 both. |
  | `https://www.dunnesstores.com/paul-costelloe-living-studio-brown-faux-fur-hat-3647114/p` | 200; schema.org `Product` JSON-LD with `offers` (`lowPrice` 30, `priceCurrency` EUR). |
  | `https://www.dunnesstoresgrocery.com/` and `/robots.txt` | **403**, header `cf-mitigated: challenge`, Cloudflare "Just a moment..." page with a Turnstile widget. |
  | the same grocery address in an ordinary headless Chrome (chrome-devtools-axi) | the same "Performing security verification" page; nothing past it was asked. |

- **Two different sites.** `dunnesstores.com` is open to the VPS but is Dunnes' clothing and home shop: its category
  sitemap (the 6 top levels: kids, home, women, men, school, seasonal) has no food or grocery category, and the only
  grocery-like path is `home/kitchen-and-dining/food-storage`. The groceries the owner buys are sold on
  `dunnesstoresgrocery.com`, and that is the site a Dunnes product link in basket points at. It answers this VPS, plain
  client and browser alike, with Cloudflare's managed challenge. **Result: blocked from the VPS.**
- **Decision: Dunnes is read by the PC reader (issue #29), not by a nightly server job like Lidl.** The server does not
  solve the challenge, change its user agent, spoof a browser, use a proxy or a third-party solving service, and does not
  read `dunnesstores.com` instead (it carries no grocery products, so it would price the wrong thing). No protection is
  bypassed. Until a reading arrives a Dunnes row says "Waiting for the next PC read", as Tesco does.
- **Not known yet, and not assumed:** whether Cloudflare serves the PC reader's fresh Chrome profile. Tesco's Akamai
  refused a fresh automation-driven profile on the owner's own connection (see "Tesco unreadable from the shop profile
  too"), so Dunnes may end the same way; #29 finds out by one polite read, stops on the first refusal and reports it. If it
  is refused, the same ruling applies: nothing is built to get past it, and whether Dunnes stays unread is the owner's call.
- **Not part of this change:** the `dunnes` store, its address shape, listings and the reader's parser; they arrive with
  #29 (see "Dunnes: the store, its address shape and the reader's parser" below).

## Dunnes: the store, its address shape and the reader's parser (2026-11-08, issue #29)

- **The `dunnes` store is added, read by the PC reader.** `Stores` (`Store.cs`) gains `Dunnes` (host
  `www.dunnesstoresgrocery.com`, `ReadBy` `pc`, no loyalty scheme), so a `dunnes` store route, link and ingest reading now
  work like Tesco's; until a reading arrives a Dunnes row says "Waiting for the next PC read". The reader gains the same:
  `dunnes` in the URL file and `--store`, the host check, the same pacing, blocked-page handling and ingest shape. This is
  the only server change.
- **No migration.** `Store` is persisted as a string (`HasConversion<string>().HasMaxLength(16)`, `"Dunnes"` fits), there is
  no seed data and no check constraint on it, and the EF model snapshot does not list enum members, so adding the member
  changes no schema. Checked by reading `AppDbContext` and the migrations, and by the backend test suite running green.
- **Address shape: `https://www.dunnesstoresgrocery.com/product/<name>-<number>`**, `<number>` 6 to 12 digits, with the
  `-id-` form (`.../product/dunnes-stores-hot-sauce-250g-id-100322749`) matched by the same rule. Taken from product
  addresses of the shop that a public search lists; query, fragment and trailing slash are dropped. `dunnesstores.com` (the
  clothing and home site) is not accepted.
- **Not verified against a live page.** Cloudflare answered a plain request from the author's PC with the same "Just a
  moment..." challenge it gives the VPS (2026-11-08, HTTP 403), so no real product page was read and nothing was done to get
  past it. The parser reads the schema.org JSON-LD offer only (price, name, stock), as Tesco's does, and is tested on
  hand-written fixtures marked as such in their first comment, plus the recorded challenge page, which must stop the shop.
  The was price and promo text are not posted until a live page shows where they can be read. Whether Cloudflare serves the
  reader's Chrome profile is found out by the first run, which stops on a refusal; whether Dunnes stays unread then is the
  owner's call.
- **Block detection also knows Cloudflare**: the "Just a moment..." and "Performing security verification" titles and a short
  page that loads `challenges.cloudflare.com` count as a refusal, as the 403 itself already does.
- **The reader's JSON-LD-only parse is shared** (`parseJsonLdOnly`) by Tesco and Dunnes instead of copied.
- **Follow-up, deliberately not here:** the Settings copy ("Lets your PC send Tesco and Aldi prices", and the same in the
  token sheet and relay section) still names two shops. Changing it moves the settings screen baselines, which this change
  keeps still; the app's own copy should name Dunnes when those baselines are next regenerated.

## Phone fixes from the 390px audit (issue #51, items 1-4)

Page-side only, on the current design system: no token, palette, type-scale or lifekit-common component changes. Each
change borrows one named pattern; the audit is the 2026-11-07 phone UX audit (390px emulation on fixtures).

- **The Add field is a bottom bar, not a top form (Apple HIG Toolbars; Reminders' bottom "New Reminder").** List and
  Prices end with a `bk-composer-bar` (`core/shell/composer-bar.component.ts`): sticky to the bottom of the scroll
  area, so it sits just above the tab bar on a long list and rests at the bottom of a short page (the page uses
  `cmn-page` `fill`). `cmn-dialog-actions` was the audit's suggestion, but it is the sheet footer (negative sheet
  margins, a sticky offset made for a sheet's padding) and does nothing in a page, so the bar uses the same tokens and
  classes on its own host instead. The `cmnAppLayoutFab` slot was not used either: it belongs to the shell, and a page
  cannot project into it.
- **No promise of a gesture that does not exist (HIG Writing).** The list load error no longer says "Pull down to try
  again". It says "Check your connection and try again", which is what the error state's "Try again" button already does.
  The library's pull to refresh has landed, and the copy stays as it is: the wording describes the button, not the gesture.
- **Pull to refresh on the List and Prices tab roots (Apple HIG Refreshing content; lifekit-common `cmn-page [reload]`).**
  Both pages bind `[reload]` to `store.load()`, which returns a promise, so the shell's indicator stays until the fetch
  settles and no `reloading` flag is needed. Failure and offline stay with the error empty state's "Try again"; the pull
  is not the only way to reload, since the page also re-reads every 15 seconds and on returning to the tab. More is a kit
  menu with nothing to fetch, and People and Settings are pushed screens, so none of them registers a reload.
  `PricesStore.load` drops a response that a later load or a local remove has overtaken (as `ListStore.load` does), so a
  slow pull cannot bring a deleted product back.
- **Sign-in leads with the explanation and one primary action (Carbon Empty states; HIG Onboarding).** A `cmn-empty-state`
  (icon, "The shopping list for the two of you", "Sign in with the passkey on this phone. There is no password.", the
  passkey button in its `cta` slot) in the middle of the screen, the invite hint under it. The join and unreachable
  pages keep `bk-auth-intro`; they are not part of this change. (Step A4 later made the lifekit account the primary action; see "Family sign-in pages".)
- **A Prices row is a value on the right and two short lines on the left (HIG Lists and tables; Carbon content density).**
  `detailLines` replaces the run-on `detail` string: line one is what the price is (card price, old price, promo, end
  date), line two is the reading (seen date, last paid, a failed read). A shop with no price yet ("Waiting for the next PC
  read") has one line saying why, plus the last-paid line when there is one. The row keeps the library's `cmn-list-item-row`; the line break is a `\n` shown with `pre-line`.
- **Out of scope here:** the offline shell / service worker (own task) and every library-side item (44px hit areas,
  skeleton before auth, swipe rows, tab-bar text, startup image) stay with lifekit-common. The Linux
  baselines for List, Prices and Sign-in change with this and are re-rendered by the "Update screen baselines" workflow on
  CI, not committed from a local run.

## Offline shell and update prompt (2026-11-08)

Item 5 of #51 (the phone UX audit's fix 13, basket half): the installed app opens without a network and offers a new release
instead of running an old one for days. It is the pattern finance-sentry, lifekit-dashboard and the DevClaw console already use,
not a new one.

- **Angular's service worker, registered as in the other apps.** `ngsw-config.json` plus `serviceWorker` on the production
  build (`ng serve` has no worker); `provideAppServiceWorker()` registers it once the app is stable (30 s at most), so first
  paint never waits for it, with `updateViaCache: 'none'` so the browser re-fetches the worker script itself. The first visit
  installs it; the shell is cached from the second request on, so "works offline" starts on the second visit.
- **The shell and nothing else.** The `app` group (index, manifest, favicon, the JS and CSS bundles including lazy page chunks) is
  prefetched; the `assets` group (icons, fonts) is cached when first used. There are **no `dataGroups`**, and `/api` is
  excluded from both asset caching and the `index.html` navigation fallback. A request the config does not name is not
  touched by the worker: it goes straight to the network.
- **Why a stale shell cannot show someone else's data.** The cached files are the build output, identical for every visitor and
  holding no list, price, name or session. Everything personal arrives through `/api` with the `__Host-basket` cookie, is never
  stored by the worker, and is asked for again on every load. Offline, the shell opens and the session call fails, so the app
  shows "Cannot reach basket" rather than a remembered session or list. Signing out or in as someone else needs no cache
  clearing for the same reason. `ngsw-config.spec.ts` runs Angular's real config generator over the file and pins "no data
  groups, nothing under `/api` cached, `/api` is not a navigation fallback"; `offline-shell.spec.ts` runs the built app with the
  worker, goes offline, reloads, and lists Cache Storage for any `/api` entry.
- **The update prompt is the shared one.** `AppUpdateService` (`@lifekit-hq/core/pwa`, the only owner of `SwUpdate`) and the
  `lk-update-prompt` and `lk-offline-banner` elements (`@lifekit-hq/elements`, registered in `main.ts`) sit in the app
  layout's notice slot, in flow under the top bar, so there is no design-system change. The prompt appears once a new version is
  downloaded and Reload activates it; the service re-checks when the app returns to the foreground, and is created at start-up
  so an update found on the sign-in page is not lost. The prompt does not interrupt: nothing reloads by itself, so a half-typed
  item is never lost.
- **Not done here.** The install hint (`lk-install-hint`), push, background sync and the iOS launch screen are separate; the
  hint was left out because it would add a banner to every browser visit and is not part of "offline shell and update prompt".
  It was added to the notice slot on 2026-11-12 with the shared shell chrome (`ShellChromeService`).
  The e2e config keeps `serviceWorkers: 'block'` for every other spec (the worker fetches around `page.route`); only
  `offline-shell.spec.ts` allows it, against its own server that answers `/api`.

## Fleet identity on the machine endpoints (2026-11-08, #62)

Owner decision 2026-11-08 ("Agree, thats good"): the fleet gets a service identity on his own apps. In the owner's Logto it is
the machine-to-machine application `fleet-agent` with one API resource (`https://fleet.lifekit.internal/api`) and one scope per
action class. People stay passkey-only on basket; this only adds a machine path.

- **Four endpoints, three scopes, nothing else.** `POST /api/ingest/prices` needs `basket:price-ingest`, `GET /api/products`
  needs `basket:catalog-read`, `POST /api/products` and `PUT /api/products/{id}/stores/{store}` (the store-page link, added
  2026-11-09, see "The fleet links store pages") need `basket:catalog-add`. Sign-in, invites, members, passkeys, ingest
  tokens, lists, product deletion and store unlinking take a signed-in person only, as before. A fleet token on any of them is
  refused (401, or 403 where the origin check answers first), and a fleet token never makes a session.
- **How a token is accepted.** The token is a JWT from the identity service. Basket checks the signature against the issuer's
  published keys (discovery at `FleetAgent:Authority`, or `FleetAgent:BackchannelAuthority` when the container cannot reach
  the issuer's own address; the issuer checked stays `Authority`), the issuer, the audience (`FleetAgent:Audience`, default the
  resource above), expiry (30 s skew) and an asymmetric algorithm; `alg: none` and shared-secret tokens never pass. Then the
  `scope` claim must hold the endpoint's scope. No secret is held by basket: it verifies, the fleet signs in at Logto.
- **Why a backchannel address exists.** The identity service's issuer is the tailnet address (`https://<name>:3001/oidc`), and per
  lifekit-stack `docs/runbook.md` ("How the pieces connect", Grafana OIDC) the host firewall does not reliably let a container
  reach the host's own tailnet address; on-box OIDC clients reach Logto at `http://logto:3001/oidc` over the external Docker
  network `identity-oidc`. The basket container is therefore expected not to reach the issuer's public address, and
  `FleetAgent:BackchannelAuthority` is how it fetches discovery and signing keys. It is optional and empty by default; use only
  `Authority` when the container can reach it. The accepted issuer is pinned to the trimmed `Authority` by an issuer validator, so
  whatever issuer the fetched metadata reports can never change which issuer is trusted. Whether the basket container can reach
  the backchannel address is a deploy check (see "Switching it on" below).
- **Off until configured.** With `FleetAgent:Authority` empty (the default, and the production compose passes it empty unless
  `FLEET_AGENT_AUTHORITY` is set in the env file) no fleet token validates anywhere. Turning it on is an env-file change and
  a redeploy, not a code change.
- **Switching it on (2026-11-08, lifekit-stack#491 item 3).** The production compose joins `basket-app` (not `basket-db`) to
  the external network `identity-oidc`, alongside its own `default` network, so `http://logto:3001/oidc` resolves from the
  container. The network is created by lifekit-stack's `deploy.sh`, not by this stack; `up` fails if it is missing. The two
  values are deploy secrets-file settings (`docker/.env.sops`, or `~/.config/basket/env` until the move), not plaintext in git: `FLEET_AGENT_AUTHORITY=<IDENTITY_ENDPOINT>/oidc` (the issuer
  the tokens carry, the same value the dashboard and the edge use) and `FLEET_AGENT_BACKCHANNEL_AUTHORITY=http://logto:3001/oidc`.
  The deploy recreates `basket-app` only. Check after it: a `basket:catalog-read` token on `GET /api/products` at
  `127.0.0.1:3310` answers 200, a token without that scope 403, no token 401 (lifekit-stack `docs/runbook.md`, "Fleet agent on
  the dashboard and basket"). **Rollback:** empty both values in the env file and redeploy (every bearer is then refused, 401);
  the `identity-oidc` membership can stay, it only lets the app read public keys.
- **A disabled application.** Logto has no per-application switch: `fleet-agent.sh revoke` takes the role away, so the app still
  gets a token but with no scope, and every endpoint here refuses it (403). `revoke --delete` stops tokens being issued at all.
  A token already issued stays valid until it expires (an hour at most); basket does not call the identity service per request.
- **The `bsk_` flow is untouched.** The ingest endpoint judges the owner-minted token exactly as before, with the same 401 body
  and `WWW-Authenticate: Bearer`; only a request whose bearer is not a valid `bsk_` token is then offered to the fleet check.
  Nothing about issuing, listing or revoking those tokens changed.
- **A bearer header means the request stands on the token alone.** The catalog routes accept a signed-in person or a fleet token;
  when the request carries a bearer header the cookie is ignored, and only then does the origin check let a request without an
  `Origin` through (and only on the catalog add and the store-page link, as `/api/ingest` already did; see "The fleet links store pages"). A cross-site page cannot set that header, so a
  forged request still has nothing to ride on, and a signed-in browser with a bad bearer is refused, not served by its cookie.
- **No migration.** `product.AddedByUserId` is a required foreign key to a user, and the fleet identity is not one. A product the
  fleet adds is therefore recorded against the owner (the oldest owner account) rather than adding a nullable column or a service
  user; the `Fleet agent {ClientId} authorised for {Scope}` log line is where the fleet's own actions are told apart. If the
  owner wants "added by the fleet" shown on the product, that is a nullable column and a migration, which waits for his go.
- **Tests** use a signing key generated per run and tokens minted in the test; no real credential or Logto call is involved.
  They cover a valid token per endpoint, a wrong scope, an expired token, a token for another audience or issuer, a forged
  signature, an unsigned token, a scope-less token (the disabled application), every non-machine endpoint, and the path being off.

## The PC reader signs in as the fleet (2026-11-09)

Owner's goal: no owner-minted token and no hand-kept URL list. `pc-reader` now takes the fleet identity from its environment
(`BASKET_FLEET_CLIENT_ID`, `BASKET_FLEET_CLIENT_SECRET`, `BASKET_FLEET_ISSUER`; never in the repo, never logged) and fetches a
token by OAuth client credentials at `<issuer>/token` for `basket:price-ingest` and `basket:catalog-read` on the resource basket
already checks, renewing it a minute before expiry. It pushes prices with that token and reads its page list from
`GET /api/products` (PC-read shops only, each address re-checked against the shop's own product-page shape). The server side is
unchanged: same endpoints, scopes and validation as "Fleet identity on the machine endpoints". `urls.local.json` stays as a fallback
only when the reader has no fleet identity or the catalog call fails; the `bsk_` token path is unchanged and used when no fleet
setting is present. A fleet identity set alongside a `bsk_` token wins. No migration.

## The PC reader reaches the identity service over https, no tunnel (2026-11-14)

The reader's documented issuer was `http://127.0.0.1:3001/oidc`, reachable from the PC only through a reverse ssh tunnel held open by
hand. Scouted from the PC on 2026-11-14: Logto's discovery document at the tailnet address reports
`issuer: https://<vps>.<tailnet>.ts.net:3001/oidc`, the same string its tokens carry as `iss` and basket's `FleetAgent:Authority`
(`FLEET_AGENT_AUTHORITY`) pins, so a token fetched over that address validates without any change on the VPS. With
`BASKET_FLEET_ISSUER` set to it (and `BASKET_API_ORIGIN` on the tailnet address) the reader gets its token, and basket answers
`GET /api/products` 200, with no tunnel running and nothing listening on `127.0.0.1:3001`. Not verified (it would need a tunnel up): whether Logto stamps a different `iss` when reached as `127.0.0.1`. The reader only uses the value as the base of `<issuer>/token`, but it is kept equal to the issuer Logto reports so there is one string everywhere. This change is therefore repo-side only: `.env.example` and the README now give the tailnet https issuer, and
`npm run check` (`pc-reader/src/identity-check.ts`) proves the four links (issuer not loopback, discovery reports the configured
issuer, token `iss`/audience/scopes, basket accepts it) and exits non-zero otherwise. The loopback `http` allowance in the config
stays for local development against a local Logto. No Funnel is needed for Logto: the PC is on the tailnet, and the PC reader
therefore depends on Tailscale being up, which the scheduled run should treat as "skip quietly" when it is not.

## The fleet links store pages (2026-11-09)

Owner's goal: the Tesco and Aldi reader gets its work list without the owner linking each product by hand. The fleet identity
could already create a product; it can now also link a store page to one, with the scope it already holds.

- **One more route, the same policy.** `PUT /api/products/{id}/stores/{store}` uses the `basket:catalog-add` policy, the one
  `POST /api/products` uses. No new scope, no change to the Logto role. `DELETE /api/products/{id}` and
  `DELETE /api/products/{id}/stores/{store}` stay people-only: a fleet token on either is refused (401, or 403 where the origin
  check answers first), as the people-only table test asserts.
- **Same checks as for a person.** The shop name, the product's existence and the address's shape (`Stores.TryNormalizeProductUrl`)
  are unchanged; relinking a different page still forgets the old readings.
- **No user id to handle.** Linking writes a `StoreListing`, which carries no author, so unlike the product add there is no owner
  to record it against and no migration. The `Fleet agent {ClientId} authorised for basket:catalog-add` log line tells the
  fleet's link apart.
- **Origin check.** A bearer request skips the origin check only for `POST /api/products` and `PUT /api/products/{id}/stores/{store}`
  (previously the path `/api/products` alone). A bearer on any other product route is still judged on its origin, and with the
  bearer the cookie is ignored, as before.

## What's new comes from lifekit-common (2026-11-09)

Owner's goal: the same layout and the same things across the lifekit apps, reused rather than rebuilt per app. Basket adopts
the What's new feature of `@lifekit-hq/ui` and `@lifekit-hq/tokens` (convention: lifekit-common `docs/WHATS-NEW.md`, the pattern
finance-sentry first had), bumped to 4.7.1.

- **No app-local copy.** `provideWhatsNew` gets basket's version (`src/app/core/version.ts`, checked against `package.json`
  by a spec) and a `basket.whatsNew.lastSeen` marker. The sheet, the list, the dot and the seen marker are the library's.
- **Notes.** `src/whats-new/notes.md`, plain second person; `lifekit-whats-new` runs before `start`, `build` and `build:prod`
  to generate `public/whats-new.json` (no longer committed or linted, and fed by `CHANGELOG.md`: see "Releases: release-please and the
  weekly release").
- **Where it shows.** An account-menu item (`whatsNewMenuItem`) in the app shell and on the More page, with the dot until the
  sheet is opened. A first run on a device counts as seen, so a new person is not greeted with a dot.
- No database change.

## Swipe actions on list rows (2026-11-09)

Owner's goal: ticking and removing a list item should be one gesture on a phone (issue #63).

- **Borrowed pattern: Apple HIG Lists and tables, swipe actions**, through the `cmn-list-item-row` swipe slot in lifekit-common
  (`@lifekit-hq/ui` 4.7.1, bumped with the rest of `@lifekit-hq/*`). Tick (or Untick on a ticked line) is the leading action,
  Delete the trailing one.
- **The visible controls stay.** The checkbox and the "More" button remain the keyboard and screen-reader path (the revealed
  actions are buttons and focus slides the row open); the swipe is a shortcut, never the only way.
- **Delete skips the sheet.** The sheet's Delete button was already the confirmation; a swipe reveals a labelled Delete that must
  be pressed, so it is the same two deliberate steps. The store still rolls the row back if the server refuses.
- No server change, no migration.

## Promotions: ranked by unit price (2026-11-10, issue #48)

Owner's words: "I am as a customer should understand what needs to be bought. I want to know where certain position is better. I want
to know what promotions are in the store and which promotion is better." Epic #24. A **Promotions** tab lists the current promos of
every tracked product and ranks the shops by the price of one kilo, one litre or one piece, so "2 for €4", "20% off" and the Lidl Plus
price can be set side by side.

- **Borrowed pattern: the unit-price shelf label** (the "€4.92/kg" printed under the price in a shop, and the per-unit column of
  price-comparison apps). Nothing is invented: the label's number is the ranking key. The screen is the existing list template
  (`cmn-page` kind `list`, `cmn-list-section` cards, `cmn-list-item-row`), a `cmn-segmented` control (HIG Segmented controls) for the
  two groupings, and a `cmn-tag` for the place. It is a third tab (List, Prices, Promotions), also in the More page's list.
- **A view over what is already collected; no migration, no new collection.** `GET /api/promotions` (signed-in people only; a
  fleet token is refused like on every route not named as a machine route) reads the latest `price_observation` of every linked
  listing: price, was-price, loyalty price and scheme, promo text, valid dates, in-stock. It needs no list-item product link and
  adds no column. The calculation is pure (`PromotionBuilder`, `OfferCalculator`, `PackSizes`, `PromoText` under
  `Basket.Api/Prices/Promotions`) and tested without a database.

### The rule (also printed on the page)

1. **One price per pack.** The reading's candidate prices are the shelf price, the card price (when it is lower) and a multi-buy; the
   deal is the one with the lowest price per pack for a customer who meets its terms. "Each" below means per pack.
   - **Multi-buy.** `N for €X` is `X / N` each ("Mix 'n' Match 2 for €4" is €2.00). `N for M` with no € sign and M below N means pay
     for M of N ("3 for 2" is `2 x price / 3` each); a whole number above N with no € sign is euros ("2 for 3" is €3 for two);
     `2 for 2` says nothing and is ignored. "Buy 1 get 1 free", "buy two get one free" and "BOGOF" are pay-for-M offers. A multi-buy
     dearer than the single price is not the deal (the unit price assumes you buy the whole set, and the row says how many).
   - **Percentage.** The shown price is the price: a was-price above it makes a price cut, and the saving is `(was - price) / was`.
     A "20% off" in the text with no was-price is shown as the deal's name but **never subtracted again** (the shop's price may
     already include it), and its saving is unknown, not guessed.
   - **Loyalty price.** A card price below the shelf price is a deal that **needs the card** (Q7: loyalty prices count); the row says
     "needs Lidl Plus". It ranks like any other price, flagged, because the owner counts card prices. A card price with no shelf
     price is still a card price; there is then no saving to show.
   - **Card and multi-buy together.** The promo text sits in the card's block on Lidl's tiles and Clubcard offers, so when a reading
     has a card price and a scheme, a multi-buy in its text is the card's (it needs the card and counts the card price for "3 for 2").
     With no card price it is for everyone. This is a reading of the shops' pages, not a guarantee; the row always names the card.
   - **Saving** compares with the was-price when the shop gives one, else the shelf price; it is shown only when positive.
2. **Divide by the pack size.** Pack size is read from the shop's own name for the product (`store_listing.title`), then from the
   household's product name ("Whole milk 2L"): `500g`, `1.5kg`, `75cl`, `1,5L`, `6 x 330ml` (a multipack counts every part), `pack of
   6`, `9 pack`, `each`. Weights become kilograms, volumes litres, counts pieces, and the unit price is `pack price / size`, rounded
   to the cent away from zero. A size taken from the product name rather than the shop's title is marked, since the shop may sell a
   different pack. **No size found: no unit price and no rank** (flag "no pack size"), never a guessed one; the pack price is shown.
   **An ambiguous text gives no size either**: a weight or volume beside a separate pack count ("Heinz Beans 415g x 4",
   "Coca-Cola 330ml 6 pack") could be one pack or several, so it reads as no size, and the search stops there: the product name
   is not consulted in its place.
3. **Rank within one product across the shops, cheapest first.** A kilo of mince is not a litre of milk, so the ranking key is the unit
   price of the *same* product in each shop, and equal unit prices share a place. A product is listed when at least one shop has a
   promo on it, and its ordinary shelf prices elsewhere are ranked beside the promos, so "Aldi's plain price beats Lidl's 2 for 3"
   is visible. The "By store" grouping lists each shop's promos (not shelf prices) with the same place, e.g. "#2 of 3".
4. **What is shown but not ranked** (each with its reason on the row, never dropped silently):
   - a reading **older than 3 days** (`PromotionBuilder.StaleAfterDays`; the nightly Lidl job and the PC reader should beat that, and
     an offer's price can move) with its "Seen" date;
   - an **out-of-stock** reading;
   - a pack with **no size**;
   - **mixed units**: when a product's shops measure it differently (kg here, pieces there) the unit most shops share is ranked and
     the others are flagged "sold by a different unit". They are not converted: that would need a weight per piece, which is a guess.
5. **A shop with nothing usable today is named with the reason**, not skipped and not filled in: no reading yet, no price listed,
   the offer starts on a date, the offer ended on a date. An offer is *current* by the same rule the Prices page uses
   (`PriceStates`, shared): not before `valid_from`, not after `valid_until`, with a price.

### Edge cases and calls

- **Size changes (shrinkflation).** Basket stores no pack size and keeps no title history, so a changed pack shows from the next
  reading, when the listing's title changes; the unit price then moves with it. The view does not compare a product with its own
  past size. Remembering sizes per observation would be a column and a migration, left for the owner's go if wanted.
- **Prices carry dates.** Every row shows when it was seen and when the offer ends. Nothing is carried forward past an ended offer
  or filled in for a shop that was not read.
- **Unit prices are to the cent**, which is also what ranks, so two offers that print the same figure share a place.
- **Pieces need the household to say so.** Only "pack of N", "N pack/pk/pcs/pieces/count" and "each" count as a piece size; a bare
  "10 Chicken Breast Skewers" has no size (Lidl's own tiles carry none), so it is listed unranked until the product name carries one.
- **What this cannot see yet.** Shop-printed unit prices ("€2.50/kg") are not collected, so the app's own division is the number;
  Dunnes and Tesco promos depend on what the PC reader can post (see the Tesco and Dunnes sections) and show as "No price read yet"
  until then.
- **Not part of this change** (out of scope in the issue): the plan API, basket totals and the shelf screen.
- **Screens.** The new Promotions page and the extra tab change every signed-in screen's Linux baseline; they are re-rendered by the
  "Update screen baselines" workflow on CI, not committed from a local run. The route scan, drift check and screen composition check
  pass locally on the new page at 390 and 1280 px, light and dark.

## Loyalty cards as barcodes at the till (2026-11-10, issue #46, epic #27)

Owner's words: "I shop in 4 different stores. Lidl, aldi, tesco, dunnes. I want to have one app where I could sort things out."
The four chains' loyalty card numbers are stored once and each is shown as a scannable code on the phone, so the phone replaces
the wallet. Borrowed pattern: **Stocard's card list and add-card form** (a list of cards, the number typed once, the code format
chosen from a short list) and **the Lidl Plus card screen** (one card, the code as wide as the screen on a white ground, the
number under it).

- **One card per shop, shared by the household (Q3).** `loyalty_card` has a unique index on `store`, so "added once" is the
  database's rule, not the client's. `PUT /api/cards/{store}` adds the card or replaces the one there (201 / 200); two people
  adding the same shop in one moment get the second as `409 card_exists`. `DELETE` removes it. The shop is one of the four
  names (`Stores`); anything else is `404 unknown_store`. People only: the fleet identity's token is not accepted on `/api/cards`
  (the default policy takes the cookie alone), and a test pins that.
- **Encrypted with Data Protection, the mechanism Q5 chose for secrets.** The number is `Protect`ed with ASP.NET Data Protection
  (purpose `basket.loyalty-card.number.v1`, the app's existing key ring on the `/keys` volume) before it reaches the database;
  the column holds only the protected text and a test reads it back raw. Consequences, stated so they are not a surprise: the
  keys are unencrypted on that volume (as already recorded for the cookie), so whoever can read the volume and the database can
  read the numbers; and if the key ring is lost, the numbers are unreadable. basket then says "Cannot be read. Add it again."
  on the card (`readable: false`, number `null`) rather than showing garbage, and adding the card again replaces it.
- **Never logged, never cached.** The number travels in a request body and a response body, never in a path or query, and the
  request log line carries only method, path and status. A refusal (`number_invalid`) states the rule and never repeats the
  input. Card responses carry `Cache-Control: no-store`. A test registers a log sink, exercises every card route (including
  refusals) and asserts neither the number nor its protected form appears in any rendered log event.
- **The list hides the number; one route returns it.** `GET /api/cards` returns each card with the number masked to its last
  four characters. `GET /api/cards/{store}` returns it whole, and is called only when a card is opened for the till.
- **The code is chosen per card, with a default per chain.** The shop's own card decides what scans, so the default follows the
  chain and the owner can change it per card (Stocard does the same): Code 128 or QR code.
  - **Lidl: QR code.** The Lidl Plus card is a QR code in the app, scanned at the Irish till (Lidl Ireland's pages). What goes
    in the field is the text behind that QR code, not a printed number.
  - **Tesco Clubcard and Dunnes ValueClub: Code 128.** These are long digit strings, and Code 128 draws any of them. The
    web checks made for this change found no official statement of either card's symbology, so this is a default and not a
    verified fact: the first time the owner opens a card on a real till, a failed scan means switching the code in the sheet.
  - **Aldi: Code 128, as a placeholder.** Aldi Ireland has no points scheme (an Irish Examiner column names it as the exception
    among the main retailers); the only card found is its Savings Card, for topping up with deposit return vouchers. basket
    stores whatever number the owner gives for Aldi and does not claim what it is. Raised with firstmate to confirm with the owner.
  - **Normalisation.** Spaces and dashes typed for readability are dropped for the linear codes (a Clubcard number is printed in
    groups); a QR text is kept as typed, trimmed. Code 128 takes 4 to 48 printable ASCII characters; QR up to 300.
- **Drawn client-side by a maintained library, not by hand.** `bwip-js` 4.11.4 (MIT, released 2026-08), through its `browser`
  entry (the package root has no condition TypeScript resolves). The number never leaves the phone to be drawn. The canvas
  carries its own white ground and quiet zone, so the code scans the same in the dark theme and no colour is set in CSS. bwip-js
  is imported on first use: the initial bundle is unchanged (1.40 MB raw), the extra lazy chunk is 184 kB over the wire and
  the service worker prefetches it like any page chunk. Module size is 4 px (10 for QR) so a phone scales the picture down and
  never up. A number the format cannot draw shows an error and no code; it is never drawn wrong.
- **Where it lives: a fourth tab, and one screen per card.** `Cards` joins List, Prices and Promotions in the tab bar (at the till it must be
  one tap). `/cards` is a list of the four shops (name, masked number and code, or "Not added yet"); a row opens the code or,
  for a shop with no card, the add sheet. `/cards/:store` shows the card: the code and the number in groups of four as a preview,
  then "Show at till", "Change number or code" and "Remove card" (with a confirmation). Both are on `cmn-page` (`list` and
  `detail` kinds) with lifekit-common components only; no token, template or colour was added, and the drift check passes.
- **"Show at till" is full-screen, at every width.** The owner asked for the code as a full-screen barcode, so the button opens
  it in `CmnDialogService` with `CmnDialogBareContainerComponent` and a pane stretched to the whole viewport (`panelClass`), on a
  phone and on a desktop alike: the library's own `size: 'full'` is a 95% dialog from `md` up, which is not a takeover. The bare
  container keeps the dialog behaviour (`role="dialog"` named "<shop> card at the till", focus moved in and trapped, Escape closes,
  focus returns to the button); the till component draws its own header (the shop's name and a close button, the first tabbable
  element) because the bare container has none. The code is fitted to the smaller of the width and the height left, white on its
  own ground, with the number under it, so the number and the close button are visible without scrolling at 1280x720 and on a
  short phone (375x667). The code's canvas stays `role="img"` labelled "<shop> card <format>", which never reads the number
  out. It does not raise the brightness or hold the screen awake (not asked for).
- **Gates.** `cards`, `card` and `card-till` (the card page with the till dialog open, brought up by `reveal` in
  `e2e/support/screens.ts`) are covered by the screen check, the route scan and the 390 / 1280 baselines. The Linux baselines
  for the new screens, and for More and every other screen that now has a Cards tab, are rendered by the "Update screen
  baselines" workflow on CI (`update-baselines` label), not from a local run.
- **One migration, additive: `20261110100000_LoyaltyCard`.** It creates `loyalty_card` (and its unique index on `store`) and nothing else;
  it touches no existing table. It sorts after `ListingImage`, the latest on main when this was regenerated (the timestamp is set by hand
  to order after it). Other ships' migrations must order after whichever merges first.
- **Out of scope.** The Lidl Plus token connection and receipts (their own tasks). Scanning a card with the camera to add it
  (Stocard has it): `BarcodeDetector` is missing on iOS Safari, and the number is typed once per shop. Whether the Lidl Plus
  QR text is static or rotates is not verified here: a rotating one would not scan from a stored copy.

## The product photo (2026-11-10, issue #30, part of epic #21)

Owner's words: "lets have tiles, photos, price (promotion if applicable) and so on - that would be the card." The shelf shows a
product as a card with a photo, so the nightly read keeps each store listing's photo address.

- **One nullable column, `store_listing.image_url` (`StoreListing.ImageUrl`).** The address only, never the picture: basket
  fetches, resizes and stores no image bytes, and the shelf's browser loads it from the shop's own image host. Up to 500
  characters (`ImageUrls.MaxLength`). Per listing, not per product, because each shop shows its own photo.
- **Addresses are checked once, in `ImageUrls.TryNormalize`:** absolute `https` only, at most 500 characters, no login in the
  address, no control characters. Nothing else (data:, javascript:, http:, a relative path) is stored. Which host it points
  at is not restricted: shops serve photos from their own CDNs (lidl.ie, aldi.cx, tesco.com) and an allow-list would break
  the first time one moves.
- **An absent photo never clears one.** `PriceRecorder` sets `ImageUrl` only when the reading carries a usable address, so a
  page that shows no photo this night, or a reader that does not send the field, leaves the stored one alone. A changed
  address replaces it, which is the "refreshed when the store changes it". Relinking a listing to a different page clears
  it with the title and readings (another page is another product).
- **Ingest takes `imageUrl` as an optional field** on `POST /api/ingest/prices`, for the owner's `bsk_` token and the fleet
  identity alike (it is the same endpoint and the same validation). Absent or blank is fine. An address that is not a plain
  https address within the cap is ignored exactly as `PriceRecorder` ignores it, and the reading's price, was-price and promo
  are still recorded: a photo address never costs a price. Any stored photo stays. The reader's own check (`toImageUrl`)
  uses the same rules as `ImageUrls.TryNormalize` (https only, no login, no control characters, within the cap), so a
  shop quirk is dropped at the reader rather than sent.
- **Capture.** Lidl, read by the VPS job: the offer tile's `image` (else the first of its `imageList`), and on a product page
  the JSON-LD `image` (first of the gallery; a string, a list or an object with a `url`), else the page's `og:image`. Aldi
  answers the VPS with "Access Denied", so it is read by the PC reader, not the VPS job: `pc-reader` takes the JSON-LD `image`,
  else `og:image`, and sends it as `imageUrl`. The brief for this change said both were VPS reads; Aldi's capture is in the
  reader that actually reads Aldi. Tesco and Dunnes are read by the PC reader too: their shared JSON-LD parser
  (`parseJsonLdOnly`) takes the same Product `image`, else `og:image`, and sends it as `imageUrl`; like the rest of those two
  parsers it is checked against hand-written fixtures, not a live page. Aldi's JSON-LD image is a 300-pixel-wide rendition; no larger one is on the page.
- **Not here:** no screen, no matching, no search-add, and the product list API does not return the address yet (the shelf
  ship adds `ImageUrl` to its responses and, if it landed first, owns the column: this change checked main for an existing
  `ImageUrl` before adding its migration, and none was there).
- **Migration `20261110090000_ListingImage`:** additive, one nullable `character varying(500)` column, no backfill, no index.
  Existing rows are null and fill in on the next read (Lidl that night, Aldi, Tesco and Dunnes at the next PC run). Rollback is dropping the
  column. It needs the owner's go before merge.
- **Tests** cover the Lidl tile and product-page capture (including `og:image` and bad addresses), the recorder's keep/replace
  rule, ingest with the field present, absent, blank, and a bad address (over the cap, not https) ignored with the price kept and any
  stored photo left alone, relinking clearing it, and the reader's address check and the Aldi, Tesco and Dunnes parsers.

## Done shopping and the history of trips (2026-11-10)

Owner's words: "I want have buy lists. And the history of lists." and, after ticking everything, "it should be in the past".
Issues 37 (close the trip) and 38 (history and reload), built on the list as it is today: no dependency on the compare sheet,
a planned store or the shelf. Patterns borrowed: AnyList and OurGroceries ("clear crossed-off items" and the list history).

- **A closed trip is a date, the ticked items and who ticked them, nothing more.** Two new tables, `trip` (`id`, `closed_at`) and
  `trip_item` (`id`, `trip_id`, `text`, `checked_at`, `checked_by_user_id`; `product_id` and `store` were added later, see the
  2026-11-10 follow-up below). No prices or planned store: marking a planned
  store on a trip (issue 36) stays out. `trip_item` rows go with their trip; a deleted account leaves `checked_by_user_id` empty
  and the history then shows the item without a name.
- **The close is an explicit action.** "Done shopping" sits on the Ticked section of the List tab and asks for a confirm that says
  what happens. It is not triggered when the last item is ticked (the default taken in issue 37; a ticked row can still be
  unticked by a mis-tap). `POST /api/list/close` copies the ticked items into a new trip and deletes them from `list_item` in one
  transaction. It refuses with 409 `nothing_ticked` when nothing is ticked, and with 409 `list_changed` (nothing filed) when the
  delete does not touch every row that was copied, which is what a second device closing at the same moment, or someone
  unticking an item, looks like. Two taps at once therefore file one trip.
- **What is not ticked stays on the list.** "Empties the list" is read the way AnyList reads it: the ticked items leave, the rest
  waits for the next shop. When everything was ticked, which is the case the owner described, the list is empty and ready for a
  new one. The confirm names the count that stays so this is never a surprise. Clearing unticked items too would lose things he
  has not bought; that is the call to veto if he wants the other reading.
- **History is a page, not a tab.** `/history` (title "History", back to the list) is reached from the clock icon in the List
  tab's top bar, the lifekit page-action contract for a trailing action; it is not a third tab. Trips are listed newest first with
  the day they were closed, the item count, each item and "Ticked by <name>". `GET /api/trips` returns 20 trips per page with a
  `hasMore` flag; "Show older trips" asks for the next page with `before=<closedAt of the last trip shown>`, so a trip closed
  while he is paging cannot shift or repeat a page.
- **"Same as last time" adds, it does not replace.** `POST /api/trips/{id}/reload` puts the trip's items on the list as new
  to-buy lines added by whoever tapped, in the trip's order. An item whose text (any letter case) is already waiting on the list is
  skipped, so a second tap adds nothing; a ticked line with the same text does not count as waiting. The 500-item cap of the list
  applies: over it the call answers 409 `list_full` and adds nothing. The trip stays in the history. The page then returns to the
  List tab and a toast says how many were added.
- **Text, plus the product and shop of a Shelf line (2026-11-10 follow-up).** Reloading copies the typed text, and a trip item
  closed from a Shelf line also keeps that line's `product_id` and `store` (nullable columns on `trip_item`, migration
  `20261110130000_TripItemProduct`, additive; the product link is set null if the product is deleted). Reload restores both onto the new
  line, so the Shelf tile marks it "On the list" instead of adding a second one, and a product line counts as already waiting when
  an open line has the same product and shop, whatever its text. A free-text line, and every trip closed before this change, has
  neither and reloads as text exactly as before; old trips are not backfilled and free text is not matched to products. The
  history response and screen are unchanged. A later change to the catalogue still never rewrites a past trip's text.
- **Migration: additive only.** `20261110110000_Trips` creates the two tables and their indexes and touches no existing table
  or row, so it merges under the standing authority for additive migrations. It is dated after `20261110100000_LoyaltyCard` so it applies
  after it; `Down` drops both tables. Backend tests run it from empty on every class.
- **Screens.** The new route is in `frontend/e2e/support/screens.ts` (the route scan, screen check and baselines read it) and the
  list mock has a ticked item, so the List baselines change too. Linux baselines are drawn on CI only, by the "Update screen
  baselines" workflow dispatched on the branch.

## Each store page keeps its category from the shop's breadcrumb (2026-11-09, issue #41, epic #25)

Owner's ask: "in lidl all the products are almost in the same position - first there is fruit section then meat... I wanted to see
it sorted according to what goes after another. So i get predictable list by what is closest to me." A product's category is the
unit that walk order will sort by; this change only keeps it. The sort is not built here.

- **Borrowed pattern, named.** The category follows the **product photo field** (`StoreListing.ImageUrl`, issue #30) exactly:
  an optional, nullable field on the reading (`category` on `POST /api/ingest/prices`, `PriceQuote.Category` for the nightly Lidl
  job), written by `PriceRecorder`, returned on the listing, `?? listing.Category` so **a reading without one never clears a
  stored one**, and cleared only when a different page is linked (a different page is a different product).
- **It lives on the store listing, not the product.** Each shop files the same thing under its own aisles ("Chilled Food > Milk" at
  Aldi, something else at Tesco), and the walk is one shop's walk, so a product has a category per shop it is linked to.
- **A path, not one word.** The value is the breadcrumb between the shop's front page and the product, outermost first, joined with
  " > " ("Chilled Food > Milk"), at most 200 characters. The front page crumb ("Home", or a link to `/`) and the last crumb (the
  product itself, by name or by address) are dropped, as is Aldi's catalogue root "Products". Keeping the whole path lets the later
  sort choose its depth (aisle or shelf) without another read.
- **Where it is read.** PC reader, from the page it already loads: the schema.org `BreadcrumbList` JSON-LD, else a `nav` whose
  label says breadcrumb (Aldi, Tesco, Dunnes). Server, Lidl: the `BreadcrumbList` of the product page. Lidl's offer tiles carry
  only "Food" or "NonFood", which is too coarse, and a listed product is deliberately not opened for its price, so a Lidl
  listing **with no category yet** opens its own page once (the pull job passes `ListingToCheck.NeedsCategory`); once a category
  is stored the offer page alone is enough again, so the nightly request count returns to what it was. A page that cannot be read
  costs the category only, never the price; a refusal (`FetchStatus.Blocked`) on that page still ends the store's pull, as it does
  everywhere else.
- **Not verified live.** No shop was read for this change (the brief forbids it), and tesco.ie and dunnesstoresgrocery.com have
  not answered the author's PC so far. Aldi's fixture is a recorded breadcrumb; the Lidl, Tesco and Dunnes fixtures are
  hand-written from the schema.org shape and say so. A shop whose page has neither markup simply leaves the field empty.
- **One additive migration, `StoreListingCategory`:** one nullable `varchar(200)` column on `store_listing`; no rewrite, no
  backfill. It follows `StoreListingImageUrl` (the product photo) and is independent of it.

## The Shelf: pick products from a shop instead of typing them (2026-11-09, issues #32 and #11)

Owner's ask: "select the store and select the products from the list from the store and form a list to buy. Without manually typing
things to buy", with "tiles, photos, price (promotion if applicable)" as the card. A new **Shelf** tab does it. The tab bar is now List, Shelf, Prices and Cards; Promotions moved behind **More** (it is a view over
prices, and a fifth tab would leave six slots with More on a 390 px bar).

- **Borrowed patterns, named.** The product tile grid of **Bring!** (a photo tile per product, one tap puts it on the shared list,
  the tile shows it is there) and the favourites list of the **Tesco app** (browse what a shop sells, with its price, and add from
  there). No new interaction is invented. The shop selector is lifekit-common's `cmn-segmented` (All, Lidl, Aldi, Tesco, Dunnes)
  and the tile is its `cmn-selectable-card`, pressed while the product is on the list. The page is the `overview` template, which
  has no rail and takes the full width, so the grid goes from two columns at 390 px to five at 1280 px.
- **What a card shows**, all of it already on the product endpoint: photo, name, the best price today (a card holder's price when it
  is the lowest, with "with Clubcard"), the old price struck through, the promo text as a tag, how long the offer lasts, when it was
  read ("Seen 5 Nov"), and "Out of stock" when the reading says so. An offer that has not started or has ended shows "from 12 Nov"
  or "ended" and none of the deal lines. A page with no reading yet says so. `PriceResponse` gains `inStock` (it was stored, not sent).
- **A shop's filter shows that shop's reading.** Tesco keeps the products with a Tesco page and shows the Tesco price and photo.
  **All** keeps every product and shows the shop with the lowest current price, named on the card; this is a display choice, not
  the compare sheet or matching (issues #9, #10), which stay out.
- **A tap is one list line per product and shop.** The list item gets a nullable `ProductId` and a nullable `Store` (issue #11): the
  line is named after the product (the server ignores any text sent with a product), the shop is where it was picked, and **All**
  records no shop. The shop has to be one that lists the product (400 `not_sold_there`). A second tap, or a second phone, gets the
  open line back (200, not 201) and a partial unique index on `(product_id, store)` for open lines (`NULLS NOT DISTINCT`, so "no
  shop" counts as one value) holds it when two taps race. A ticked line is history: tapping the product again makes a new line,
  and un-ticking the old one while a new one is open answers 409 `already_on_list` rather than breaking the index. Deleting a
  product keeps its list lines as plain text (`ON DELETE SET NULL`). Free-text lines are untouched; the composer stays on the List
  tab as their only way in. The List row shows the shop as a tag.
- **Photos.** The column and the price-ingest `imageUrl` already exist (the nightly read's photo address, see "The product photo" above:
  an address that is absent or fails the check is ignored and never costs a price). This change returns it as `imageUrl` on the
  listing in `GET /api/products` and accepts it on the catalog link (`PUT /api/products/{id}/stores/{store}`, optional `imageUrl`,
  checked by the same `ImageUrls` rule; a bad one there is a 400 `image_invalid`, because that call is a deliberate write). A link
  without a photo keeps the stored one; relinking a different page forgets it. A tile with no photo, or whose photo does not load,
  shows the product's initial on the surface colour.
- **Content-security policy: `img-src` now allows `https:`.** The shops' image hosts are not known until the reader sends them, and
  an allowlist of guessed hosts would break the first photo that came from another one. Only the image directive widens, the
  address is server-validated as above, and the image tag sends no referrer. Narrow it to the hosts the reader really uses once
  they are known.
- **One additive migration, waiting for the owner's go:** `20261110120000_ListItemProduct` (two nullable columns on `list_item`, a
  foreign key and the partial index). It rewrites no row and needs no backfill; `Down` drops all four. It is dated after
  `20261110110000_Trips`.
- **Not in this change** (recorded as follow-ups): matching of free text to products (#9), search-to-add (#31), the match sheet
  (#10), reading photos from the shops (#30), the compare sheet and the shop plan.

## Shell before the session call

- **The shell draws before `/api/session` answers.** `AppComponent` holds the signed-in shell until the first navigation
  ends (the guards wait for the session, then the page's chunk loads): `cmn-app-layout` with `[accountPending]="true"`, and
  in the page's place a `cmn-page` of the address's kind holding the matching `cmn-skeleton` preset (`list` for every page
  today) inside a `role="status"` "Loading" region. Only the routed content waits for the session. Sign-in, invite and the
  unreachable page draw no shell; their layout is a separate fix. Pattern: Apple HIG, Loading and Launching (show something
  right away) and the Carbon Loading pattern, as written up in lifekit-common's `docs/PHONE-CONTRACT.md`, "First paint".
  `e2e/cold-start.spec.ts` holds the audit's `basket-boot` probe: session delayed 900 ms, layout shift under 0.1 on every page
  at 390 and 1280.

## Design selection in Settings (2026-11-10)

Owner's words: "we dont have yet a design selection." Basket is on `@lifekit-hq/ui` 4.7.1, which ships design presets; this wires
the library's own switcher into Settings and sets the default.

- **The library does the work; basket only offers a preset.** `provideDesignPresets({id: 'lifekit', label: 'Lifekit'})` in
  `app.config.ts` lists it, `@lifekit-hq/tokens/presets/lifekit.css` is added to the `angular.json` styles beside `theme.css`, and
  the existing `<cmn-appearance-settings>` in Settings > Appearance shows its Design control (Carbon, Lifekit) because there is now
  more than one preset. No switcher, preset or storage code lives in basket.
- **Lifekit is the default** (the owner's 2026-11-08 pick): `<html data-preset="lifekit">` in `index.html`, which is the
  library's documented way for an app to ship a default (`PresetService` keeps an offered `data-preset` already on the page when
  nothing is stored). The generated `theme-init` block is untouched.
- **Kept the way the library keeps it:** per device, in `localStorage` under `cmn-preset`, not on the account; there is no
  backend change and no migration. Because a Carbon pick did not persist (below), basket's Appearance caption reads "The look of
  the app" rather than "On this device", and the what's-new note says only that the look can be changed, not that it is remembered.
- **Carbon was a library gap, closed in 4.8.0 (see "Adopting `@lifekit-hq/ui` 4.8.0" below):** until then `PresetService` stored no key
  for Carbon, so a device that picked it came back as Lifekit after a reload. Basket never re-implemented presets to hide it.
- **Tests:** `e2e/design-preset.spec.ts` covers the default (attribute and the chosen Design cell) and switching to Carbon and
  back, with Lifekit surviving a reload. Every screen baseline changes because the default look changed; they are regenerated by
  the Update screen baselines workflow, not committed by hand.

## Adopting `@lifekit-hq/ui` 4.8.0 for the phone UI gaps (2026-11-10, issues #83, #84, #87, #89, #90)

`@lifekit-hq/ui` and `@lifekit-hq/tokens` (with `core`, `elements`, `charts-core` and `config`, which `ui` pins to the same
version) go from 4.7.1 to 4.8.0. The library halves of the phone audit are fixed there; basket takes them and drops what it
carried for them. Additive: no endpoint, schema or migration changes.

- **Icons (#83).** `BadgePercent`, `WalletCards` and `Maximize2` are in the library's own icon subset now, so `core/icons.ts`
  registers only `Store` and `RotateCcw`, which the library still does not ship. `tab-icons.spec.ts` still fails on any tab
  icon that draws nothing, so a name that falls out of the library set is caught.
- **The app mark (#87).** `provideAppMark('bk')` in `app.config.ts` makes basket's own mark the library's. `bk-auth-intro` draws
  `<cmn-app-mark [size]="56">` instead of an `<img src="/favicon.svg">`, so the sign-in, invite and unreachable pages show the
  mark in the user's colour and in dark mode. The `angular.json` icon assets come from `@lifekit-hq/tokens/brand/dist/bk` (they
  were the `lk` set, so the favicon, apple-touch icon and PWA icons were Lifekit's, not basket's). `index.html` and the web
  manifest are unchanged: they name the same file names. While the user has their own colour, the library rewrites the tab icon to
  a `data:` URL; the CSP's `img-src` already allows `data:`.
- **Alert frame (#84), Custom theme start (#89), command palette (#90)** need nothing from basket: they are library components
  (`cmn-alert`, the theme picker in Settings > Appearance, the palette). The palette's entries were supplied by basket from
  2026-11-12, by `ShellChromeService`.
- **Carbon is remembered.** The library keeps an explicit Carbon choice now, and its pre-paint script changed to match it, so
  `index.html`'s generated `theme-init` block was regenerated with `npm run sync:theme`. The inline script's `sha256` in
  `Security:ContentSecurityPolicy` (`backend/src/Basket.Api/appsettings.json`) changed with it; `npm run lint` checks the two
  stay in step. The default preset stays Lifekit (`<html data-preset="lifekit">`), and the what's-new line ("You can change the
  look in Settings under Appearance") is still true as written, so it is unchanged.
- **Not here:** secondary controls (#85) and sheet padding (#88) are separate. The screen baselines change with the new mark and
  icons and are rendered by the Update screen baselines workflow on the PR branch, never committed by hand.

## Plan API: the best shop per list line (2026-11-10, issue #12, epic #24)

Owner's plan of 2026-11-07, step Plan API: `GET /api/plan` says which shop is best for each line on the list and whether one shop
covers all of it. Unmatched or stale lines are flagged, never guessed. Backend and contract only; the Plan tab is a separate item.

- **A view over what is already collected; no migration.** `GET /api/plan` (signed-in people only; a fleet token is refused like on
  every route not named as a machine route) reads the open (unticked) lines of the list, their products, the linked shop pages and the
  latest `price_observation` of each. Per line it returns the line, its product when attached, a price per linked shop, the best shop and
  a flag when there is none. Plan-level it returns `coverage`. Ticked lines are history and are not planned.
- **One price rule, shared with Promotions.** The per-shop price is `PromotionBuilder.Compare`, extracted from the Promotions
  builder so both screens read a reading the same way: the cheapest way to buy it (shelf, card price, multi-buy), the pack size read
  from the title or the name, the unit price to the cent, the same rank. Each shop entry carries that `offer` (with its card, deal
  label and unit price), plus the raw reading: `observedOn`, `price`, `wasPrice`, `promoText`. A card price counts as in Promotions and
  names its card (`Clubcard`, `Lidl Plus`); the plan does not know who holds a card.
- **Best shop = lowest unit price among fresh, in-stock readings.** A kilo, a litre or a piece is the key (a 2 L bottle at €2.20 beats a
  1 L at €1.20). A shop that is stale or out of stock is shown with its state and never named best. If exactly one shop has a fresh,
  in-stock price it is the best, whatever its pack size. If several do, **all of them must be comparable** (a pack size known, the
  same kind of unit), otherwise no shop is named and the line is flagged `not_comparable`: naming the cheapest of the comparable ones
  would hide a shop whose price cannot be read against them. Equal unit prices go to the first shop in the fixed order (Lidl, Aldi,
  Tesco, Dunnes).
- **Staleness window: 3 days** (`PromotionBuilder.StaleAfterDays`, the Promotions window, one number for both). The nightly Lidl job
  and the PC reader should beat it, and a shelf price can move in a week; a reading from the 3rd day back still counts, the 4th is
  stale. A line whose newest usable price is older is flagged `stale` with no best shop, and its readings stay listed with their dates.
- **A line that cannot be priced says why (`flag`), and has no best shop:**
  `unmatched` (free text, or its product was deleted: never matched from the words), `no_price` (the product has no usable price at
  any shop: not linked, not read yet, no price listed, the offer has not started or has ended), `stale`, `out_of_stock` (every
  fresh price is for a product the shop shows as out of stock; added to the three reasons because naming such a shop best would send
  the shopper to an empty shelf) and `not_comparable` (above). A line the owner picked at a shop keeps that shop as `pickedStore`; it
  does not steer the answer.
- **Coverage: does one shop price every open line?** A shop prices a line when its reading is fresh, in stock and current
  (state `priced`); `not_comparable` lines still count as priced there. `coverage.singleStore` is true when at least one shop prices
  all of them (`coveredBy` lists them in the fixed order; `store` is the one that is best for the most lines, ties to the first).
  An empty list is not covered by anything. When it is false, `closestStore` is the shop that prices the most lines (null when no
  shop prices any) and `blockers` lists the lines it cannot price with a `reason`: the line's flag when no shop can price it
  (`unmatched`, `no_price`, `stale`, `out_of_stock`), otherwise that shop's own state for it (`not_read`, `no_price`, `upcoming`,
  `ended`, `stale`, `out_of_stock`, or `not_linked` when the shop has no page for the product).
- **Not in this change:** the Plan tab (next section), the cheapest two-shop split, per-shop basket totals, and Lidl last-paid prices.

## Plan tab: the plan on the phone (2026-11-10, issue #13, epic #24)

Owner's plan of 2026-11-07, step Plan tab: the `GET /api/plan` answer on a screen, with prices dated. Frontend only; no backend change
and no migration.

- **Borrowed pattern, named.** The per-store price comparison row of price-comparison grocery apps (shop, price, the day it was read),
  as the epic asks. Each open line is a `cmn-list-section` card headed by the line, with one `cmn-list-item-row` per linked shop: shop
  name, "Seen 5 Nov" and the deal beneath it, a tag, and the price on the right with the old price struck through. No interaction is
  invented; the page is read-only and sits on the `list` template with lifekit-common components only (`cmn-verdict-header`,
  `cmn-list-section`, `cmn-list-item-row`, `cmn-tag`, `cmn-empty-state`, `cmn-skeleton`). The only utility classes are the spacing and
  type tokens other pages use; the row's own layout is not overridden, so the date comes first in the line where a narrow row can
  least afford to lose it.
- **A fourth tab slot, so Prices moves behind More.** The phone tab bar holds four tabs and More (the library's `MAX_BOTTOM_TABS`). It
  is now **List, Shelf, Plan, Cards**; **Prices** joins Promotions behind More, in the order of the desktop sidebar (List, Shelf, Plan,
  Prices, Cards, Promotions, People). Plan took Prices' slot because Prices is where products are set up and shop pages linked
  (occasional), while the Shelf and the Plan are the weekly shopping loop and Cards must stay one tap away at the till. Reversible:
  it is the `PHONE_TAB_ROUTES` list in `ShellChromeService`, which the shell and the More page both read.
- **The answer on top is the coverage.** A `cmn-verdict-header` says "One shop covers it" with that shop (and how many can, when
  several), or "No one shop covers it" with the closest shop and how many lines block it, or "No shop prices it yet". Under it, when
  one shop does not cover the list, a card "Not priced at <closest shop>" lists the blocking lines each with a short reason tag. The
  wording is the server's coverage and nothing is recomputed on the phone.
- **Per line: the best shop first.** The best shop is tagged "Best", the other fresh prices follow, then shops with nothing usable;
  a shop whose reading is old, out of stock, not read yet, without a price, not started or ended says so in its tag and is dimmed. The
  price on the right is the offer's price for one pack when the deal is met (a card price counts and names its card, "2 for €4" is
  €2.00 with the deal in the line), and the unit price (€/kg, €/l, each) is in the line. The was-price is struck through only when
  it is higher than what is charged. A line the server could not price carries its flag as a plain label and one sentence: "Not
  matched" (typed by hand), "No price", "Old prices" (older than the server's window, 3 days), "Out of stock", "Can't compare". Its
  readings stay listed with their dates.
- **Dates are the reading's own** (`observedOn`, Dublin day, never shifted by a time zone), and a rule card at the bottom states the
  as-of day, the unit-price rule and the staleness limit the server sent.
- **States follow the other tabs.** Skeleton while loading, an error empty state with "Try again" plus a toast, an empty list says
  "Nothing to plan", and the page reads again when it comes back into view (prices change at most once a night). Icons: `ListChecks`
  for the tab and the empty state, from the library's Lucide subset, so nothing new is registered in `app.config.ts`.
- **Tests.** Unit: the wording helpers and the page against a recorded response (`plan-format.spec.ts`, `plan.page.spec.ts`).
  E2E: `plan-flow.spec.ts` (the tab bar and More on a phone, and what the Plan tab answers), the cold-start layout shift for `/plan`, and
  three screens in `e2e/support/screens.ts` (`plan`: no shop covers it, with one line of each flag; `plan-covered`; `plan-empty`), so
  the screen check, the route scan and the baselines cover them. The new baselines and the changed tab bar are rendered by the Update
  screen baselines workflow on the PR branch, never committed by hand.
- **Not in this change:** the cheapest two-shop split, per-shop basket totals, Lidl last-paid prices, and ticking lines off from the Plan.

## By shop: the list priced at each shop (2026-11-10, issue #47, epic #24)

Owner's words: "I am as a customer should understand what needs to be bought. I want to know where certain position is better. I want to know what promotions are in the store and which promotion is better." and "It should compare the products and show the best for price/value across multiple stores." The whole list priced at each of the four shops side by side: a total per shop, the lines each lacks, and the cheapest split.

- **Borrowed pattern, named.** The per-store basket total comparison of price-comparison grocery apps (Trolley, Basket Savings): one row per shop with the basket total, how much of the basket it prices, and the cheapest marked. Nothing is invented: it sits on the `list` page template with the same lifekit-common components as the Plan tab (`cmn-verdict-header`, `cmn-list-section`, `cmn-list-item-row`, `cmn-tag`).
- **A view over what is already collected; no migration, no new collection.** `GET /api/plan/by-shop` (signed-in people only) loads the plan exactly as `GET /api/plan` does and sums it (`ByShopBuilder`, pure, built only on `PlanResponse`). Which shops price a line, at what, and which one is best are the plan's own answers, so a price is the same number on the Plan tab, in Promotions and here. A card price (Clubcard, Lidl Plus) and a multi-buy deal count, as everywhere, and name their card or deal.
- **Reached from the Plan tab, not a tab of its own.** The phone tab bar stays at four tabs. Plan's page header carries a "By shop" action (the pattern the List tab uses for History) to `/plan/by-shop`, a page with a back arrow to Plan; the sidebar has no entry for it.
- **A shop's total adds only what it prices today.** A line counts at a shop only when the plan calls it `priced` there: a fresh reading (3 days, the plan's window), in stock, offer started and not ended. A line the shop cannot price adds nothing and is listed under "Not priced at <shop>" with the shop's own reason (`not_linked`, `stale`, `out_of_stock`, `not_read`, `no_price`, `upcoming`, `ended`, or `unmatched` for a line typed by hand). The total is therefore never a guess, and a shop is **complete** only when it lacks nothing. A shop that prices nothing shows a dash and "Nothing priced" and lists nothing (its reason would be the same on every line).
- **Cheapest = lowest total among the complete shops.** A shop with lines missing can show a lower number only because it left things out, so it is never called cheapest. When no shop is complete the top says so and names the closest (most lines priced) with its total so far. Ties go to the first shop in the fixed order (Lidl, Aldi, Tesco, Dunnes).
- **A total is one pack of each line, and says when packs differ.** Lines have no quantity yet, so each counts once, at the pack the shop lists. Where two or more shops price a line with different or unknown pack sizes, the totals are not like for like on it: each such shop's row says "packs differ on N". The rule card on the page says the same. Fixing that needs quantities on lines, which is a migration and the owner's call; it is not done here.
- **The cheapest split buys each line at the plan's best shop, by value.** The split is the plan's `bestStore` per line (lowest price per kilo, litre or piece), grouped by shop with the dated prices, each shop's subtotal and the split total. A line the plan names no best shop for (`not_comparable`, `no_price`, `stale`, `out_of_stock`, `unmatched`) is listed under "Split: no best shop" with the plan's flag and is not given to a shop by guess. `versusCheapest` sets a complete split against the cheapest complete shop (negative: the split is cheaper) and is null otherwise: a partial split is never set against a whole basket. Because the split picks by value, it can cost more than one shop whose packs are smaller; the page says so in those words instead of hiding it.
- **Dates and flags.** Every split line shows "Seen 5 Nov" first (the reading's own Dublin day), then the deal, the card it needs and the unit price; each shop's row shows the day of the oldest price in its total, the cards and offers included, and how many lines are missing. Old and out-of-stock prices are never in a total; they appear as missing lines, with their reason.
- **Not in this change:** quantities on lines, which promotion is best across shops (Promotions already ranks them), the Plan-tab cheapest-split row (issue #40), and the order to walk the shops in.
- **Tests.** Backend: `ByShopTests` (totals, cheapest, missing reasons, cards and offers, differing packs, the split and its comparison, the empty list, the endpoint and its guards). Frontend: wording helpers and the page against recorded responses, plus the Plan-to-By-shop flow, the cold-start shift for `/plan/by-shop` and three screens (`by-shop`, `by-shop-complete`, `by-shop-empty`) in the route scan and the baselines. The new baselines are rendered by the Update screen baselines workflow on the PR branch, never committed by hand.

## Basket-side halves of the phone audit's Carbon findings (2026-11-10, issues #83, #86, #88)

A phone audit of basket on the Carbon-aligned library filed missing icons and components that did not look right. Three small
changes land here; the library halves are fixed in lifekit-common, not worked around.

- **Icons the app uses outside the library's set (#83).** `BadgePercent` (the Promotions tab), `Store` (the Shelf tab),
  `WalletCards` (the Cards tab and Add card), `Maximize2` (Show at till) and `RotateCcw` (Same as last time on History) are
  registered with `provideLucideIcons` in `app.config.ts`, from `core/icons.ts`. Without that `<cmn-icon>` renders an empty box.
  `tab-icons.spec.ts` renders the shell with the real app providers and fails on any tab icon that draws nothing. Since
  `@lifekit-hq/ui` 4.8.0 the library ships `BadgePercent`, `WalletCards` and `Maximize2`, so only `Store` and `RotateCcw` stay
  registered (see "Adopting `@lifekit-hq/ui` 4.8.0" above).
- **Row overflow and cancel-link buttons are `ghost` (#86).** The list item's and product's overflow (`Ellipsis`) and the pending
  invite's cancel (`X`) are `variant="ghost"`, so they read as row furniture and not as a second primary action.
- **Sheet bodies are padded `px-cmn-6 pt-cmn-4 pb-cmn-6` (#88).** All seven `*.sheet.ts` bodies carry the padding themselves so
  the body lines up with the title and clears the header rule. This is a **stopgap until the library drawer pads its body**; when
  it does, drop the class string from the seven sheets so the padding is not doubled.

## Lidl Plus connection: paste a token once (2026-11-10, issue #15, epic #26)

Owner's words (Q5): the Lidl refresh token "lives encrypted in basket's database, seeded by one paste in Settings, not in SOPS".
This is that connection: the token is pasted, checked with Lidl, kept encrypted, and Settings says whether it still works. What is
read through it is not here: receipts import (#45) and the Lidl Plus offers (#14) are their own tasks and will call
`LidlPlusConnectionService.AccessAsync`.

- **One connection for the household, owner only.** `lidl_plus_connection` is a single-row table: the primary key is a fixed id, so
  a second row cannot exist, and a paste is `INSERT ... ON CONFLICT (id) DO UPDATE` (one statement, so two pastes at once do not
  collide). The routes are on the owner policy, like the price tokens: a member gets 403, a signed-out caller 401, and the fleet
  identity is not accepted. `GET /api/lidl-plus/connection` says where it stands; `PUT` pastes a token (201 the first time, 200
  after); `POST .../check` asks Lidl again with the saved token; `DELETE` removes it (204, or 404 when there is none). Every answer
  is `Cache-Control: no-store`.
- **The token is checked with Lidl before it is kept.** `PUT` exchanges it at Lidl's `/connect/token` (OAuth2 refresh grant, the
  Lidl Plus Android app's public client id and secret in a Basic header, the token in the form body). Lidl accepts it: stored,
  `201`/`200`. Lidl refuses it (400/401/403): `422 token_rejected`, nothing stored, and an existing connection is left as it was, so
  a mistyped paste cannot break a working one. Lidl cannot be reached, or answers something unreadable (5xx, 429, not JSON, no
  `access_token`): `502 lidl_unavailable`, nothing stored. A value that cannot be a token (empty, under 16 or over 1024 characters,
  spaces or non-ASCII inside) is `400 token_invalid` without asking Lidl. Quotes and spaces round a paste are dropped. The messages
  never repeat what was pasted.
- **Encrypted at rest with Data Protection, as Q5 and the loyalty cards chose.** `refresh_token_protected` holds
  `Protect`ed text (purpose `basket.lidl-plus.refresh-token.v1`, the app's key ring on the `/keys` volume); a test reads the column
  raw and asserts the token is not in it. Same consequences as the cards, stated so they are not a surprise: whoever can read the
  key volume and the database can read the token, and if the key ring is lost the token is unreadable. basket then reports
  `unreadable` and asks for a new paste (it does not show garbage or ask Lidl with it).
- **Never logged, never returned.** No response carries the token, whole or in part. It travels in a request body, never a path or
  query. `LidlPlusAuthClient` logs nothing and reduces every failure (including the exception text of a failed request, which could
  hold the form body) to an outcome; `LidlAuthResult` and `LidlAccess` print only their outcome. A test pastes a unique token through
  every route including the refusals, registers a log sink, and asserts neither the token, the replacement token Lidl hands out nor
  the protected text appears in any rendered log event. The HTTP client factory logs the address only, and EF's command log is at
  Warning, so parameters are not logged.
- **Lidl rotates the refresh token, so the replacement is saved first.** When Lidl's answer carries a new `refresh_token` it replaces
  the stored one in the same call, before the access token is handed to anyone: losing it would end the connection. An answer
  without one means Lidl kept the old token. The exchange is behind a process-wide lock (basket runs as one instance), so two
  exchanges of the same token cannot race and have Lidl refuse the second. Disconnect takes the same lock, so it cannot delete the row
while a check is waiting on Lidl to save its answer.
- **The states.** `not_connected`, `connected`, `needs_new_token` (Lidl refused the saved token the last time it was used: the
  row keeps a `problem` of `token_rejected`, Lidl is not asked again, and a new paste clears it) and `unreadable`. An unreachable
  Lidl changes nothing: a check answers `502`, and the connection stays `connected`, because a network error says nothing about
  the token.
- **Settings shows it, owner only.** A "Lidl Plus" section after "Price reader": the state (with the day Lidl last accepted the
  token), **Connect Lidl Plus** / **Paste a new token** (a sheet with a masked field and the server's reason when a paste is
  refused), **Check now** (only while connected) and **Disconnect** (with a confirmation). The token is never shown again after the
  paste. The what's-new note and the Settings screen mock are updated; the Linux screen baselines for Settings are rendered by the
  "Update screen baselines" workflow on CI (`update-baselines` label), not from a local run.
- **Tests use fake responses only.** `FakeLidlPlusAuth` stands in for Lidl at the `ILidlPlusAuth` seam in the integration tests;
  `LidlPlusAuthClientTests` feeds recorded answers (accepted, rotated, refused, 5xx, malformed, network error, timeout) to the real
  client through a stub handler and checks the request it sends. No test, and no build step, calls Lidl.
- **Not verified live.** The token endpoint, the client credentials and the grant shape follow the community `lidl-plus` client for
  the Lidl Plus app, not an official Lidl document, and nothing here has been run against Lidl. The first real paste is the
  check: `lidl_unavailable` or `token_rejected` on a token the app still accepts would mean one of those is different for Ireland
  (`LidlPlus:AccountsBase`, `ClientId` and `ClientSecret` are options for that reason). Where the owner gets a refresh token is the
  `lidl-plus` tool's sign-in; basket does not drive Lidl's login page (a person signs in, once).
- **One migration, additive: `20261110134121_LidlPlusConnection`.** It creates `lidl_plus_connection` and nothing else; it touches no
  existing table. It sorts after `TripItemProduct`, the latest on main when this was generated. Other ships' migrations must order
  after whichever merges first.

## Lidl Plus receipts: closed trips, shelf products and last paid prices (2026-11-10, issue #45, epic #26)

Owner's words: "I want have buy lists. And the history of lists." With the connection from issue #15 in place, the receipts Lidl Plus
holds for the account are pulled in on their own: each becomes a closed trip, its lines join the shelf, and what was paid is kept with its
day. Builds on `LidlPlusConnectionService.AccessAsync`; the Lidl Plus offers (#14) and the Plan tab's display of last paid prices
(`bsk-plan-lidl-last-paid`) are not here.

- **Borrowed pattern, named: the receipts list of the Lidl Plus app.** The app lists the account's receipts newest first, each with
  its day, shop and total, and opens one to show its lines. basket reads the same two things (the list, then each receipt) and shows
  them in the History it already has (AnyList and OurGroceries' list history, see the trips section above); no new interaction is
  invented. A receipt trip's heading reads `Thu 5 Nov 2026 · 12 items · Lidl Plus €23.40` and "Same as last time" works on it
  like on any trip.
- **A receipt is a closed trip, filed on the day it was printed.** `trip` gains three nullable columns: `source` (`lidl_plus`; null for
  a trip closed on the list), `external_id` (Lidl's receipt id) and `total` (as printed). A partial unique index on
  `(source, external_id)` means one trip per receipt, so two pulls racing cannot file it twice. `closed_at` is the
  receipt's time, so receipts sort into the history among the trips closed by hand. A trip's lines are its receipt lines as `trip_item`
  rows: text as printed, `checked_at` the receipt time, nobody as the ticker, and the product and shop (`lidl`) set, so reloading a
  receipt puts product lines on the list that the Shelf tile marks "On the list". `GET /api/trips` returns `source` and `total` on every
  trip (null for a trip closed on the list); nothing else about it changed.
- **A line is a purchase when it has a name and came to money.** Zero and negative lines (a returned deposit, a refund, a discount
  printed as its own line) and lines without a name are not trip items and set no price. The same name twice on one receipt is one
  trip line. Quantities are not kept on the trip (the trip has none); the price is.
- **Lines become shelf products by exact name, letter case aside; nothing is guessed.** A line whose name matches a product (any letter
  case) is that product; otherwise a product with that name is added, recorded against the owner (as the fleet's adds are), up to the
  existing 300-product guard (past it, the line is still on the trip as text). The receipt's name is Lidl's own ("Whole milk 2L"), not
  the household's, so a household product with another name stays separate until the owner renames one; matching words to products
  stays out, as for the plan. A product made this way has no store page, so the nightly price read ignores it.
- **Seeded Lidl products appear on the Lidl shelf.** The Shelf's Lidl filter showed only products with a linked Lidl page. It now also
  keeps the products with a known last paid price at that shop, and the card shows what was paid and when ("€2.19 / Last paid 3 Nov")
  instead of a page reading; **All** shows them too. The list endpoint's `not_sold_there` check follows: a shop "sells" a product when its
  page is linked **or** a receipt shows it was bought there, so tapping the card works; Tesco, Aldi and Dunnes are unchanged.
- **Last paid price and date.** One `paid_price` row per product, shop and day (an existing table that stayed empty until now; its
  index on those three is now unique): the price of one item, which is the line's amount over its quantity, or the price of a kilo for
  a weighed line. The row also keeps `paid_at`, the time of the receipt behind it (nullable). When two receipts of one day have the
  same product, the one printed later is the price that stays, whichever order they are found in: an existing row is overwritten only
  by a receipt with a later time. If a save loses a race on that unique index (a deploy overlap with the nightly job), the receipt is
  built again against the row that won and filed; if the other pull already filed the receipt itself, its copy stands. The day is the
  Dublin day of the receipt. It reaches `GET /api/products` (`lastPaid`: `known` with price and day; `none` once Lidl Plus is connected but no receipt has
  the product, which the Prices page leaves blank; `not_connected` before that) and **the plan API**: each plan line carries `lastPaid`,
  the latest paid price at each shop it was bought at (shop, price, day), whether or not that shop's page is linked. It is information
  only: it never decides the best shop or the coverage, and a product with only a paid price is still flagged `no_price`. The Plan tab
  draws it from the section below (issue #14).
- **It pulls on its own, nightly.** A recurring job runs at 05:00 Europe/Dublin (after the 03:00 price read and the 04:30 invite
  cleanup) and does nothing until Lidl Plus is connected. There is no endpoint or Settings button to pull on demand: Settings is
  already drawn connected in the screen baselines, and a button there would change them for a route nobody needs once the nightly job
  has run; add one if the first pull wants watching.
- **A pull is idempotent and polite.** It reads every page of the list (a page of known receipts says nothing about older ones, which
  an earlier capped pull may have left), but opens only receipts not yet filed, newest first, until 30 have been
  read (`LidlPlus:MaxNewReceiptsPerPull`), at most 50 list pages (`MaxReceiptPages`), one request at a time with 500 ms between them. A longer
  history is taken over the following nights; `waiting` says how many are left. A receipt is filed in one save (trip, lines, new
  products, paid prices), so it is there whole or not at all. A receipt in a shape basket cannot read, or that Lidl refuses on its own
  (a 4xx such as not found or forbidden), is skipped, counted in `skipped` and tried again next time; it does not use up the 30, so
  one receipt that never opens cannot hold back the older ones. Lidl being down, throttling (429), turning the access token away
  (401) or unreachable stops the pull and keeps what was filed. A process-wide lock stops two pulls overlapping.
  The access token goes in a header, is never logged or returned, and a test checks it against every rendered log line.
- **Tests use fake Lidl responses only.** `FakeLidlPlusTickets` stands in for Lidl at the `ILidlPlusTickets` seam in the integration
  tests (trip, products, paid prices, the plan and products responses, idempotency, the limit, failures mid-pull, a lost race for a day's
  price, two products differing only by letter case, the job);
  `LidlPlusTicketsClientTests` feeds recorded answers (decimal commas and numbers, bad shapes, refusals, network error) to the real
  client. No test, and no build step, calls Lidl.
- **Not verified live.** (Superseded for a receipt's address, `App-Version`, language and lines by "Lidl Plus receipts: Lidl changed
  its receipts service" below: a receipt is now read from `api/v3` and its lines from the printed HTML, not `itemsLine`, and the paid
  price nets a coupon.) The receipts service address, the headers (`App`, `Operating-System`, `App-Version`) and the field names
  (`tickets[].id`, `date`, `totalAmount`, `itemsLine[]` with `description`, `quantity`, `isWeight`, `currentUnitPrice`, `extendedAmount`
  or `originalAmount`) follow the community `lidl-plus` client, not an official Lidl document, and nothing here has been run against
  Lidl. Two readings to check against the first real receipts: whether `extendedAmount` is before or after a Lidl Plus coupon (basket
  takes it as printed and does not net discount lines, so a paid price may be the price before a coupon), and the country (`LidlPlus:Country`,
  `TicketsBase` and `Language` are options for that reason). An answer basket cannot read ends as `lidl_unavailable` or `skipped`,
  never as a wrong price.
- **One migration, additive: `20261110144147_LidlReceipts`.** It adds the three nullable columns and the partial unique index to `trip`,
  adds the nullable `paid_at` column to `paid_price`, and replaces the `paid_price` index on `(product_id, store, paid_on)` with a unique
  one. It touches no row. The unique index build would fail on duplicate `paid_price` rows; the table was empty before this change, so
  none are expected. It sorts after `20261110134121_LidlPlusConnection`, the latest on main when this was generated; `Down` drops the
  new indexes and columns and restores the non-unique `paid_price` index. A migration waits for the owner's go before merge.
- **Screens.** No route is added or removed, and the mocks' History has no receipt trip, so the baselines do not change; the Linux
  baselines are drawn on CI only, as before.

## Plan tab: Lidl's offer beside the last price paid there (2026-11-10, issue #14, epic #24)

Epic #24: "I want to know what promotions are in the store and which promotion is better." The plan API already sends `lastPaid` per line
(issue #45); the Plan tab now draws it. Frontend only; no backend change and no migration.

- **Borrowed pattern, named: the per-store comparison row, with the shopper's own last price beside it.** The row of the Plan tab section
  above (shop, price now, the day it was read) keeps its place, and a second line under it says what the household last paid at that
  shop and when: `Last paid €1.19 on 3 Nov`, the Shelf's and the Prices page's own wording, so one price reads the same on every screen.
  The two lines are the Prices page's "one fact group per line" row, with its same row classes (`whitespace-pre-line`, `break-words`) on
  `cmn-list-item-row`; nothing else is new, and the page stays read-only on the `list` template.
- **The offer is only what basket already has.** Lidl's offer is the Lidl entry's normalised `offer` from the Promotions rule (card
  price, multi-buy, percentage, shelf); nothing is fetched for Lidl Plus offers, and the line is drawn as the Plan tab already
  draws it. So the Lidl row reads `Seen 5 Nov · Lidl Plus price · €0.50/l` over `Last paid €1.19 on 3 Nov`, with the
  offer price (`€0.99`, old price struck through) on the right.
- **Last paid at any shop is shown, not only Lidl.** The server sends the latest receipt price per shop; today only Lidl receipts exist,
  and the page draws whatever shops come, so a later Tesco or Dunnes receipt needs no change here.
- **A paid price needs a shop row to sit on.** The paid line is attached to the shop rows the Plan tab already draws; a shop the
  product was bought at but has no reading for gets no row of its own, so its paid price is not drawn here (it stays on the Shelf).
- **Information only.** The paid price never picks the best shop, never moves a line's flag and is not in the coverage answer on top;
  the rule card at the bottom says so. A weighed product's paid price is a kilo's, like on the Shelf. The date is the receipt's Dublin
  day (`paidOn`), never shifted by a time zone.
- **Tests.** Unit: the wording and the row building (`plan-format.spec.ts`: offer beside last paid, a shop never
  bought at, no rows) and the page against a recorded response (`plan.page.spec.ts`). E2E: `plan-flow.spec.ts` asserts
  the offer line, both paid lines and their days on a phone; the `plan` mock gains a milk `lastPaid`, so the screen
  check (both templates, 390 and 1280) covers it. The `plan` baselines change and are rendered by the Update
  screen baselines workflow on the PR branch after the pipeline's push is final, never committed by hand; `plan-covered` changes with the
  shared milk line, `plan-empty` does not.
- **Not in this change:** a Lidl offer fetch (the Lidl Plus offers feed stays out), a price-change figure against the last paid, the
  two-shop split, per-shop basket totals, and ticking lines off from the Plan.

## Releases: release-please and the weekly release (2026-11-12, family alignment B8)

Owner's goal: basket is one of the lifekit family, so its versions and What's new move with releases the way finance-sentry's and the
dashboard's do, instead of staying at 0.1.0.

- **Same two workflows as the family.** `release-please.yml` keeps one release PR current on every merge to main (version bump plus
  `CHANGELOG.md`, from the conventional commit titles); `weekly-release.yml` merges it Mondays 00:07 UTC (`workflow_dispatch` is
  "release now"), and the merge tags and publishes the release. No releasable commit since the last release means no PR and a no-op.
- **The reusable templates are called, not copied.** `lifekit-hq/.github` is public, so a repo under `dsdevq` can call
  `lifekit-hq/.github/.github/workflows/{release-please,weekly-release}.yml@v1` (the same call lifekit-common makes); nothing is inlined.
- **What the release PR bumps:** `frontend/package.json`, the two version fields of `frontend/package-lock.json`, and `APP_VERSION`
  in `frontend/src/app/core/version.ts` (marked `x-release-please-version`), so the existing spec that ties `APP_VERSION` to
  `package.json` keeps holding on the release PR. `release-type: simple` also keeps the manifest. `pc-reader` has its own version
  and the backend has none, so neither is touched. `bootstrap-sha` is the commit 0.1.0 describes, so the first release lists only what
  lands after it.
- **Releases do not gate deploys.** Every merge to main still deploys (`deploy.yml`); the weekly release governs the version, the
  changelog and the tags. The release PR is a normal PR, so its merge deploys like any other.
- **What's new follows the changelog.** `lifekit-whats-new` now runs with `--changelog ../CHANGELOG.md`, so each release in the
  changelog is a sheet entry with its date and the fold of its user-facing changes; a `## x.y.z` heading in `src/whats-new/notes.md`
  adds the plain lines for that release (a release without them shows the change list and raises no dot). `CHANGELOG.md` starts with a
  bare `## 0.1.0` so the current release keeps its entry, and the Docker web stage copies it to `/CHANGELOG.md`.
- **`public/whats-new.json` is generated and no longer committed.** A committed copy would go stale on every release PR (it changes
  only the changelog and versions), so `npm run lint` could not stay green on it; the file is rebuilt before `start`, `build` and
  `build:prod`, and `lifekit-whats-new --check` left the lint chain with it.
- **Off until the bot is installed.** Both jobs run only when the `RELEASE_APP_ID` variable is set; they also need the
  `RELEASE_APP_PRIVATE_KEY` secret and the release bot GitHub App installed on this repo. That is an owner step; until then the jobs
  are skipped, not failed. A `GITHUB_TOKEN` fallback is deliberately absent: a release PR pushed with it never gets its checks.
- No database change.

## Plan: the cheapest two-shop split when no one shop covers the list (2026-11-10, issue #40, epic #24)

Owner's words: "It should compare the products and show the best for price/value across multiple stores." When no single shop prices every line, the plan proposes the cheapest two shops that together do, with card prices and offers counted and every price dated.

- **What By shop already had, and what was missing.** By shop's split (issue #47) places each line at the plan's `bestStore`, so it spreads the list over as many as four shops and sits only on `/api/plan/by-shop`. The plan API (`/api/plan`) returned the best single shop (`coverage.store`) but no split. This change adds only the missing piece: `coverage.split`, the cheapest pair of shops, on the plan response, and shows it in the Plan tab. By shop is untouched.
- **Borrowed pattern, named.** The "split your shop across two stores" suggestion of price-comparison grocery apps (Trolley, Basket Savings): when one shop cannot fill the basket, name the two that can, with a basket total and what is bought where. The Plan tab's new cards are the By shop split's cards (`cmn-list-section` of `cmn-list-item-row`, the `lineDetail` wording), so a line reads the same on both screens.
- **Reuses By shop's rules, does not duplicate them.** `PairSplitBuilder` (pure, on `PlanItem`s only) takes "this shop prices this line" from `ByShopBuilder.Priced` (a fresh reading, in stock, offer started and not ended; the same test By shop's shop totals use), the line shape from `ByShopBuilder.Line` and the groups from `ByShopGroup`. Those two became `internal` rather than copied.
- **Best single shop first, a split only when none covers.** `coverage.singleStore` is unchanged and wins whenever any one shop prices every line. `coverage.split` is set only when `singleStore` is false, and is null when no two shops together price every line (a typed-by-hand line, or one with no usable price anywhere): a pair that leaves a line out is not a candidate, so a partial list is never presented as a plan. Everything else is as before: the blockers still list what the closest shop lacks.
- **The pair is the lowest total.** Of every pair of the four shops that between them price every line, the one with the lowest total wins; within a pair each line is bought at the shop with the lower unit price (the offer `rank`, the same rule the plan's `bestStore` and the By shop split use; a card price or a multi-buy counts), and the pair's total is the sum of the chosen offers' pack prices. The total is like for like on every line. Ties go to the first pair in the fixed order (Lidl, Aldi, Tesco, Dunnes), and within a pair, on equal unit prices, to the first shop. **A line that cannot be compared is never guessed:** when both shops of a pair price a line but either has no `rank` (no pack size, or another unit), that pair is not a candidate; a line only one shop of the pair prices needs no comparison. No quantities are added to lines, so the total is still one pack of each line, at the pack the chosen shop lists.
- **Dates and cards.** Each bought line carries its reading's own Dublin day (`observedOn`), shown as "Seen 5 Nov" first, then the deal, the card it needs and the unit price; the split carries the day of its oldest price (`oldestSeenOn`), shown in the answer on top.
- **On the phone.** The answer on top keeps "No one shop covers it" and adds the pills "Best two: Lidl + Aldi", the total and the closest shop; under it one card per shop of the pair, "Buy at Lidl · €0.99", with its lines and prices, before the per-line cards. The rule card gains one sentence when a pair is shown. No migration, no new collection, no new route, no new component.
- **Not in this change:** quantities on lines, a three-shop split, and routing (which shop to walk first).
- **Tests.** Backend: `PlanTests` (the cheapest pair with a card price and dates, the pair chosen by lowest total and not by most lines, a tie, a line bought at the lower unit price although its pack costs more, a pair dropped when a line priced at both shops cannot be compared, no split when a line has no usable price or the list is empty, old and out-of-stock prices not counted). Frontend: the verdict wording (`plan-format.spec.ts`) and the page against a recorded response (`plan.page.spec.ts`), plus a `plan-split` screen in `e2e/support/screens.ts` for the route scan and the baselines. The new `plan-split` baseline is rendered by the Update screen baselines workflow on the PR branch after the pipeline's push is final, never committed by hand.

## The platform contract (2026-11-12, family-align step B5)

basket meets the box's platform contract (lifekit-stack `docs/platform-contract.md`, checked by `scripts/platform-contract.py`),
the way finance-sentry and the dashboard do. Before this the live checker reported `basket-app` and `basket-db` as
`UNDECLARED`.

- **Declarations.** `docker/docker-compose.prod.yml` labels `basket-app` `lifekit.contract: "v1"` with port `3310`, health
  `/api/health`, ready `/api/health/ready`, metrics `/metrics`, ingress `edge` (the loopback port is the tailnet edge's
  upstream, as for the dashboard) and service `basket`. `basket-db` is `none`: a datastore with no HTTP surface and no
  network but `default`.
- **Metrics.** OpenTelemetry's ASP.NET Core and runtime instruments, exported as Prometheus text on `/metrics` (the same
  packages as finance-sentry, metrics only; no traces exporter, the contract's traces item is not enforced yet). The app
  joins the external `lifekit-shared` network so the box Prometheus reaches `basket-app:3310/metrics`. The meters are
  process-wide, so the test hosts (dozens side by side) run with `Observability:Metrics:Enabled=false` and only the
  platform-contract tests turn it on; in production it is on.
- **`/metrics` is for Prometheus only.** It needs no sign-in, so a request that came through Tailscale (Serve or Funnel
  always proxy with `X-Forwarded-For`; Prometheus never does) is answered 404. The numbers are not served on the public
  address while the Funnel is still on, and not to tailnet devices either. The check runs before the forwarded-headers
  middleware, which consumes the header.
- **Logs.** The console sink writes one compact JSON object per line (`RenderedCompactJsonFormatter`), the trace id as
  `@tr`. The container healthcheck's `/api/health` polls stay out of the log, but a health request that carries a
  `traceparent` (the contract checker's probe) is logged, so a quiet app still shows a traced line.
- **Deploy.** `docker/deploy.sh` runs `platform-contract.py --static` on the composed config before `up`, and after the app is healthy
  runs `--project basket` with four attempts 15 s apart (Prometheus' `scraped` item reads its last scrape). A missing
  checker fails the deploy; there is no waiver.
- **Not in this repo.** The `scraped` item needs a Prometheus job for `basket-app:3310` in lifekit-stack's
  `compose/observability/prometheus/prometheus.yml` (path `/metrics`, like the `lifekit-dashboard` job). Until that job
  lands, the live checker reports `scraped` and `labels` as FAIL for `basket-app`.
- **No migration.**

## Product catalog base and deterministic candidate matching (2026-11-09, issue #9, part of the Shelf epic #21)

Owner's words: "I dont need to enter the product, I should be able to select it and see the image i believe. Like shopping
basket you know ?" The shelf needs a catalog and a way to find each product's equivalent in the four shops. Q6 stands: barcodes,
store search and size rules, no model in the app.

- **Already there, not rebuilt:** the `product` and `store_listing` tables, `GET`/`POST /api/products`, the store-page link
  (`PUT /api/products/{id}/stores/{store}`), and the fleet identity's `basket:catalog-read` / `basket:catalog-add` scopes on
  them (PRs #64 and #70, "The fleet links store pages"). This change adds only what #9 still lacked.
- **Candidates are proposals in their own table.** `match_candidate` holds `{product, store, url, title, barcode, basis, score,
  status}` and is read by nothing that reads prices. A proposal never becomes a `store_listing` by itself; confirming one is a
  person's act on the match sheet (#10), which writes the listing. `status` is `Proposed` for now; the sheet adds a
  rejected state together with the code that sets it.
- **A confirmed match is never overwritten.** `store_listing.confirmed_at` is set when a person links the page (pasting it, or
  later accepting a proposal). While it is set: proposals for that shop are refused (`409 match_confirmed`), and a fleet token
  cannot link a different page over it (`409 match_confirmed`; the same page is accepted). Only a person relinking changes it, and
  that sets `confirmed_at` again and settles the shop's open proposals. A page the fleet linked has no `confirmed_at`: it is read
  for prices like any listing but is not a choice anyone made. `ListingResponse` gains `confirmed`.
- **The migration backfills `confirmed_at = added_at` only for listings added before 2026-11-09T16:35:14Z.** That is when basket
  PR #70 (the fleet links store pages) merged; before it only a person could link a page, so each such listing is a match someone
  chose. A listing added later cannot be told from a fleet link, so it stays unconfirmed (`confirmed_at` null) and the owner
  confirms it on the match sheet. Without the backfill the first run would treat every earlier listing as open.
- **The matcher is a pure function (`Matching/CandidateMatcher`).** Rules, in order: (1) two valid barcodes decide it - equal is a
  match (score 100), different is never a match, whatever the names say; (2) otherwise the names are compared as word sets with the
  size removed (lower case, accents folded, a plural "s" dropped, filler words ignored); (3) the size rule: sizes read from the
  names (`ProductSize`: g/kg, ml/cl/l, counts, and "6 x 330ml" multipacks, reduced to a base unit) stated on both sides and
  different is never a match; equal, the shorter name's words must all appear in the other (a shop adds brand and filler words:
  "Whole milk 2L" is contained in "Milbona fresh whole milk 2 L") - `name_size`, score 50-90, falling with each extra word (Jaccard
  similarity); a size missing on either side needs a Jaccard similarity of 0.8 and ranks lower - `name`, score 20-45. A 6-pack is
  not the same as one big pack. The score is an order for the sheet, not a probability. The same inputs always give the same
  verdict; there is no model, network or clock in it.
- **Barcodes are GTINs, kept as 14 digits.** `Gtin.TryNormalize` accepts 8, 12, 13 or 14 digits (spaces and hyphens tolerated) with
  a correct check digit and pads to 14, so UPC-A and EAN-13 of one product compare equal. `product.barcode` is unique. A product
  takes one on `POST /api/products` (`barcode`) or `PUT /api/products/{id}/barcode` (empty clears); `GET /api/products` returns
  it. An invalid candidate barcode is ignored (the names then decide), never trusted. A barcode another product already holds is
  refused with `409 barcode_exists` (also when two writes race, where the unique index decides). Changing a product's barcode
  re-runs the matcher over its open proposals: those that can no longer match are deleted and the rest are rescored.
- **The store search is brought to the app, not run by it.** tesco.ie, aldi.ie and the Dunnes grocery site refuse the VPS (see
  the Tesco, Aldi and Dunnes sections), and Lidl's search has no stable machine shape that has been checked, so the server does no
  searching. `POST /api/products/{id}/candidates` takes `{store, results: [{url, title, barcode?}]}` (up to 50), the results of a
  search done by the PC reader or by search-to-add (#31), runs each through the matcher and stores the passes. Each result gets a
  stable outcome code: `proposed`, `no_match`, `url_invalid` (not that shop's product-page shape, the same check as a link),
  `duplicate`, `already_linked`, `outranked`. Posting a page again updates its proposal rather than adding one.
- **At most 5 open proposals per product and shop** (`MaxProposalsPerStore`); a stronger one pushes the weakest out, so a noisy
  search cannot flood the sheet.
- **Same door as the catalog, no new scope.** `POST .../candidates` and `PUT .../barcode` use the `basket:catalog-add` policy,
  `GET .../candidates` the `basket:catalog-read` one; people may use all three. Confirming has no machine route.
  A bearer request skips the origin check on the two new write routes only, as on the other catalog writes; the cookie is ignored
  with a bearer header.
- **Migration `ProductMatching`, additive:** `product.barcode` (nullable, unique index), `store_listing.confirmed_at` (nullable, the
  dated backfill above) and the `match_candidate` table. No column is dropped or retyped. It waits for the owner's go before merge.
- **Not here:** the match sheet (#10), product images (#30), search-to-add (#31) and the shelf screen (#32). No frontend change.

## Settings in the family order (B2)

Owner's words (2026-11-12): "Basket should be just in our ecosystem ... Same UI, same UX, same library, everything is the same.
Only difference is basket use cases and business logic." Settings now lists its sections in the family order: Account,
Appearance, Workspace (owner), Security, People (owner), About, Sign out. Name and email are read-only; a member sees no
Workspace or People section.

- **Screens.** The page's layout changed on purpose, so the four `settings` Linux baselines (390 / 1280, light and dark) differ
  from the committed images. They are re-rendered by the "Update screen baselines" workflow dispatched on this branch, not
  committed from a local run; review the images in that commit before merging.

## Typed one-offs stay text; a closed trip offers "Add to shelf" (2026-11-13, issue #34, epic #22)

Owner's words: "I dont need to enter the product, I should be able to select it and see the image i believe. Like shopping basket
you know ?" Typing stays for a one-off that is not on the shelf; after a trip the item can be promoted to the shelf.

- **Borrowed pattern, named:** Bring!'s custom items: something you typed once is kept as plain text on the list, and when it
  turns out to be a regular you save it into your own catalogue. No new interaction is invented.
- **A typed item is unchanged.** The composer, the row and `POST /api/list/items {text}` are not touched: it is a free-text line
  (`product_id` null) on the list, in the closed trip and when reloaded with "Same as last time".
- **The offer lives on the closed trip, in History.** A trip item with no product shows a ghost "Add to shelf" button on its row
  (`PackagePlus`). A Shelf pick never shows it. `GET /api/trips` items gain `productId` (null for a typed item); that is
  how the page tells them apart. Receipt-made trips follow the same rule.
- **"Starts the add-to-shelf flow" is the flow that exists.** Search-add (#31) and the match sheet (#10) are not built yet, so the
  tap goes to the Prices tab (`/prices?add=<text>`) with the typed text already in its "Add a product" box. A person edits it if
  needed and taps Add (nothing is added by the navigation itself), then links the shop pages in the same tab, the way every shelf
  product is made today. When #31 and #10 land, this link is the one place to point at their search-and-match start.
- **No migration.** No column or table changes; the response field reads the existing `trip_item.product_id`.
- **Screens.** No new route. The History mock gains `productId`, so the History baselines change (a button per typed item); Linux
  baselines are drawn on CI only, by the "Update screen baselines" workflow dispatched on the branch.

## Phone match sheet: confirm or reject a store's suggested equivalent (2026-11-14, issue #10, part of the Shelf epic #21)

Owner's words: "I shop in 4 different stores. Lidl, aldi, tesco, dunnes. I want to have one app where I could sort things out."
The matcher (#9) only proposes; this is the person's yes or no. Done when both users see the persisted results.

- **Borrowed pattern, named:** the "is this the same product?" yes/no card of price trackers and the card-stack review of dating
  apps, without the swipe: one suggestion at a time, two big buttons (Yes, same product / No, not this one), because a swipe is hard
  to find and awkward one-handed at 390 px. It is a bottom sheet, the same drawer the Prices tab already uses for pasting a page.
- **Where it lives.** On the Prices tab, a shop row with suggested pages says "N suggested pages to check" and opens the match
  sheet instead of the paste-an-address sheet; the sheet's last button, "Paste the page address instead", opens the paste sheet.
  A shop without suggestions behaves as before. No new route, so no route-scan change and no new screen baseline: the sheet is a
  drawer, and the Prices mock carries no suggestions.
- **Yes** (`POST /api/products/{id}/candidates/{candidateId}/confirm`) does exactly what pasting that page does: the shop's
  listing points at it (a new page forgets the old page's title, photo, category and readings), `confirmed_at` is set, and the
  shop's other open suggestions are settled (deleted). **No** (`.../reject`) keeps the row with status `Rejected`.
- **Rejections are persisted and final.** Rejected rows stay in `match_candidate`, so the same page is never suggested again
  (a re-post of it answers `rejected`), and both people see the same list on their next load. They do not count toward the
  5 open suggestions per product and shop, and a barcode change does not rescore or delete them. There is no undo in this change;
  a page the owner rejected by mistake can still be linked by pasting its address.
- **Both users, one result.** Either person may answer; the answer lives on the server, not on the phone. The product list
  (`GET /api/products`) gains `candidates` per shop (open suggestions), which is what both phones show. If the other person
  answered a suggestion first, the second answer finds nothing (`404`). A late No counts as done and the sheet re-reads; a late
  Yes is not reported as saved: the person is told it was already answered and the sheet re-reads the suggestions.
- **People only.** Confirm and reject take a signed-in person, no fleet scope: a token can read and propose, never decide.
- **No migration.** `match_candidate.status` is already stored as text (up to 16 characters), so the new `Rejected` value
  changes no table, column or index. Nothing waits for the owner's go on that account, and there is nothing to roll back in the
  database: reverting the code leaves a `Rejected` row that older code would fail to read, so delete those rows
  (`DELETE FROM match_candidate WHERE status = 'Rejected'`) if the change is ever backed out.
- **Not here:** who answered or when (no column was added for it), undoing a rejection, confirming a fleet-linked page that has no
  suggestion beside it, and the Shelf screen (#32).

## Store scorecard from our own baskets, opened on demand (2026-11-14, issue #49, epic #24)

Owner's words: "I am as a customer should understand what needs to be bought. I want to know where certain position is better. I want
to know what promotions are in the store and which promotion is better." The owner's 2026-10-29 call of no weekly best-store digest
still stands: this is a view he opens, never pushed, scheduled or mailed.

- **Borrowed pattern, named:** the savings summary of price-comparison and receipt apps (Trolley's savings, Basket Savings): a
  headline of what you saved, who wins, and a month by month list. No new interaction is invented; the page is the same verdict
  header and card lists as By shop.
- **Where it lives.** `GET /api/scorecard` and the page `/scorecard` ("Scorecard", under More, signed-in people only). Phone first
  at 390 px, then 1280 px, on the `cmn-page kind="list"` template.
- **Data: only what is already collected.** Closed trips (`trip`, `trip_item.product_id`) and the daily price readings
  (`price_observation`). No new collection and **no migration**: no table or column changes.
- **What is compared.** Every Shelf line (a trip item with a product) of a trip closed in the last 6 months, the current month
  included. Each is priced across the shops that list the product with the same rule as Plan and Promotions
  (`PromotionBuilder.Compare`), run as of the trip's Dublin day on the newest reading on or before that day. So a price is the same
  number here as on the other screens, a reading older than 3 days at the trip is stale and left out, as is an out-of-stock or
  unit-less one. A typed line, a deleted product, or a line with fewer than two comparable prices is **not compared**: counted and
  shown, never given a winner or a saving.
- **Wins.** The shop with the lowest unit price (per kg, l or piece) wins the line; equal prices go to the first shop in the fixed
  order (Lidl, Aldi, Tesco, Dunnes), as in the plan. The page lists every shop with its wins and share of compared lines.
- **Saved versus the dearest store.** Per compared line: the winner's pack price times (dearest unit price / winner unit price - 1),
  i.e. what the same quantity would have cost more at the dearest comparable shop. Summed, rounded to cents per line. It is a
  like-for-like quantity saving, not what was actually paid (trips keep no prices, except Lidl receipts, which are not used here).
- **Trend.** One row per month of the window (oldest first in the API, newest first on the page): trips, lines compared and saved.
  The verdict stays one short line (the shop with most wins and how many lines were not compared, under the 75-character line
  limit of the route scan); the saving is in its pill and the trend is the month list itself, newest first.
- **Screens.** New route, so new baselines (`scorecard` and `scorecard-empty`, 390 / 1280, light and dark). They are drawn on CI by
  the "Update screen baselines" workflow dispatched on the branch, not committed from a local run.

## Most bought and usually bought, worked out from closed trips (2026-11-14, issue #39, epic #23)

Owner's words: "It should work like big data with ability to show the most popular products we buy." Derived from trips, so it holds
for Tesco, Dunnes and Aldi, which give no receipts.

- **Borrowed pattern, named:** the frequent / recent items of AnyList and OurGroceries. On the Shelf it is a sort and a filter over
  the same product grid (the Bring! tile grid is unchanged); on the basket it is a short "Usually bought" list of one-tap adds. No
  new interaction is invented: the tap is the Shelf's own tap-to-add.
- **Counted from closed trips only.** `GET /api/trips/habits` reads `trip_item` rows that carry a Shelf product. A typed
  (free-text) line has no product to rank and is left out; so is a product that was deleted. Receipt-made trips (Lidl Plus) count
  the same as trips closed on the list.
- **Most bought** = the number of closed trips that bought the product (twice in one trip counts once), then the most recent
  trip, then the name; that is the order of the `habits` response. A product never bought is not in the response. The Shelf's
  "Most bought" sort uses only the trip count and leaves ties in the Shelf's own order.
- **Usually bought** = bought in at least 2 of the latest 10 closed trips (`UsualAfter`, `RecentTrips`). The window lets a habit
  that stopped fall out; the floor of 2 keeps a one-off from counting as a habit. Both are constants in `HabitEndpoints`, easy to
  move if the household finds them too strict or too loose.
- **Shelf.** A second segmented control, "Show": All products (the Shelf's own order), Most bought (sorted, never-bought products
  last in their own order) and Usually bought (only the usual ones, most bought first). It combines with the shop filter; with
  Usually bought and a shop, an empty result reads "Nothing usual at <shop> yet". A tile
  says "Bought on N trips" once a trip bought it.
- **Basket.** Under "To buy", a "Usually bought" section lists up to 5 usual products not already waiting on the list, most
  bought first; the plus button adds the product the way a Shelf pick on "All" does. It disappears while no usual product is
  missing from the list. The basket re-reads the habits with the list (poll, return to the tab, pull to refresh) and after
  Done shopping, so a trip just closed counts at once.
- **No migration.** Nothing is stored: the ranking is counted on each read from the existing `trip` and `trip_item` tables, so there
  is no table to keep in step with the trips. The household's volume (a trip a week) makes the count cheap.
- **Screens.** The Shelf and List mocks gain habits, so the `shelf` and `list` Linux baselines change (a count line on a tile, and a
  Usually bought section). They are re-rendered by the "Update screen baselines" workflow on this branch, not committed from a local
  run.

## The PC reader runs once a day by itself (2026-11-14)

Owner's question: how do Lidl, Tesco, Dunnes and Aldi products get and refresh? Lidl has the server's 03:00 pull; Aldi and Dunnes
come only from the PC reader, which had no schedule, so they refreshed only when someone ran `npm start`. Tesco stays off
(`ENABLED_STORES`, ruling of 2026-11-11). Decision: a daily run on the owner's PC, needing nobody at it.

- **Trigger: a Windows scheduled task that runs `wsl.exe`, not a systemd timer in WSL.** WSL starts lazily and a user timer only
  fires while the distro is up, so "PC on, no WSL session open" would never run. Task Scheduler is running whenever the PC is on;
  `conhost.exe --headless wsl.exe -d <distro> --exec systemctl --user start basket-pc-reader.service` boots the distro and its
  systemd if needed and holds it up until the oneshot unit finishes. The unit (not a bare script) gives a timeout, journald logs
  and one place where concurrent starts serialise. Triggers: logon +2 min, and 12:00 repeated every 3 h for 9 h with
  `StartWhenAvailable`. An ordinary (non-admin) PowerShell session can register only logon and time triggers with an interactive
  principal; boot triggers and "whether or not logged on" (S4U) are refused with access denied (verified on this PC), so the task
  as installed runs while the owner is logged on, which is also when WSL appears to come up on this PC today (its keep-alive and Docker
  start from the user's startup items). `install.sh` from an elevated session adds the boot trigger and S4U; that variant is written but was not run here.
- **Catch-up and no double runs are one rule: a stamp of the last local day read.** `src/daily.ts` skips when
  `~/.local/state/basket-pc-reader/last-read` holds today's date and writes it after a run that finished. A missed day needs no
  special case (yesterday's stamp does not match, the next trigger reads), and several triggers can overlap safely, which lets
  Task Scheduler's triggers be generous (boot/logon, midday, three retries). The stamp is a calendar day, not "24 h since the last
  run", so the schedule stays predictable after an odd-hour manual run.
- **Gate: the `npm run check` steps, with unreachable told apart from refused.** `CheckStep` gains `unreachable`, set when the
  issuer, the token endpoint or basket gave no answer at all (Tailscale down, VPS off). That is one `skipped:` log line, exit 0, no
  stamp, so the next trigger retries. Any other failed step (token refused, issuer string differs, basket 401, no fleet identity) is
  not an outage and will not heal by waiting, so it is `failed:`, exit 1, visible as a failed unit. The brief said "skip quietly
  when unreachable"; making every check failure quiet would hide a rotated secret forever.
- **A blocked store is an outcome, not an error.** The reader's exit `2` (blocked, unreadable or rejected pages) counts as a finished
  run: exit 0, stamped, summarised in the log line (`dunnes blocked (HTTP 403; ...)`). Only exit `1` (could not start, a batch
  refused) is `failed:` and leaves the day open. Dunnes is blocked by Cloudflare today; that parser/access work is separate.
- **Headless.** The scheduled run sets `BASKET_READER_HEADLESS=1` (unless the unit already sets it). Checked on 2026-11-14 against
  live Aldi: 4 of 4 pages read and accepted headless, and the `conhost --headless` wrapper leaves no console window, so nothing is
  left open on the PC. Headed would need WSLg and would put a Chrome window on the desktop.
- **Where it lives.** `pc-reader/src/schedule.ts` (the decision, unit-tested with injected checks/reader/clock), `src/daily.ts` (the
  entry, `npm run daily`), and `pc-reader/schedule/` (`basket-pc-reader.service.in`, `windows-task.ps1`, `install.sh`,
  `uninstall.sh`). The unit runs the clone `install.sh` was run from, with absolute paths, so it is re-run after the clone moves.
  No server change, no migration, nothing new on the VPS.

## The list built from the shelf by tap: a photo on every row (2026-11-14, issue #33, epic #22)

Owner's words: "My partner lists the products I need to buy - I can see the list. Partnet can compare while forming the list the
same products from different store." The done criteria are a tap on a shelf product adds it to the shared list, each row shows
photo, who added it and a store chip, and both people can add, tick and remove. Read against main, only the photo was missing: the
Shelf tap (issue #11), the "added by" name under each row and the shop tag were already there, and ticking and deleting already
work for either person (`ListTests`).

- **Borrowed patterns, named.** **Bring!** tap-to-add with a photo per item (the Shelf tile in, a thumbnail on the row) and the
  **AnyList** "added by" line on a shared row (the name under the item, unchanged). No new interaction is invented.
- **The photo is read, not copied.** `GET /api/list` and the add and tick answers return `imageUrl` on each line, taken at read
  time from the product's shop page (`store_listing.image_url`): the shop the line was picked from, or, for a pick from "All", the
  first shop alphabetically (Aldi, Dunnes, Lidl, Tesco) that has a photo. A free-text line, a product with no photo and a line whose
  product was deleted return `null`. The Shelf's "All" tile prefers the cheapest shop's photo instead; the list row has no price to
  follow, so it uses a fixed, repeatable order. Reading it each time keeps one source of truth: a photo the nightly read fixes
  later reaches the lines already on the list, and no column is added to `list_item`.
- **No migration.** Nothing is stored for this change; it is one more projected field on the existing list query.
- **The row.** A 40 px thumbnail beside the tick box (`bk-list-photo`), the Shelf tile's own fallback: the first letter on the
  surface colour when there is no photo or it fails to load. A free-text line gets the letter too, so every row keeps one leading
  edge. The row still wraps its name rather than truncating it.
- **Same powers for the partner.** Nothing to change: the shelf and the basket take no owner check, and `ListProductTests` now holds
  the partner picking from the Shelf, the row naming them as who added it, and the owner ticking and the partner deleting that row.
- **Screens.** The `list` baselines change (a thumbnail on each row). They are re-rendered by the "Update screen baselines"
  workflow on this branch, not committed from a local run.

## Dunnes: the parser reads the server-rendered state (2026-11-14, parser fix for the scout finding)

- **The finding.** A scout found the PC reader read every Dunnes product page as "no price on the page" (report
  `bsk-dunnes-no-price`, 2026-11-14). The 2026-11-08 parser (see "Dunnes: the store, its address shape and the reader's parser")
  read the price from a schema.org JSON-LD Product offer, written blind because Cloudflare had blocked the author; the site never
  publishes one (its only JSON-LD is a `WebPage` and an `OnlineStore`), so no Dunnes page could ever give a reading.
- **The fix.** `parseDunnes` reads `window.__PRELOADED_STATE__.product` from the HTML (the parser stays HTML-only). A small helper,
  `readAssignedJson` (`src/parse/state.ts`), cuts the object at its matching brace with a string- and escape-aware scan and parses it,
  so another store's parser can read a page's embedded state the same way. Price from `price` (`toMoney` drops the euro sign),
  stock from `available`, name, category (the deepest `categoryBreadcrumb` without the leading `/Grocery`) and photo
  (`image.default`, else `og:image`).
- **Guard.** `String(product.sku)` must equal the number at the end of the address, else the page is unreadable and nothing is
  posted: an offer recorded against the wrong product is worse than a missing one. No state script, or no price on a product that is
  not out of stock, stays `unreadable: no price on the page`; `available: false` is a valid out-of-stock reading.
- **Promotions** map cleanly, so they are read: the first promotion not tied to a loyalty card gives `promoText` (its description) and
  `validFrom`/`validUntil` from its local `startDate`/`endDate` (dd/mm/yyyy, as the shop shows them). The price stays the shelf price,
  so a "Buy 2 for EUR 14" deal is text, not a changed price. **Not read:** `wasPrice` (no recorded page has one yet) and loyalty-card
  promotions (Dunnes has no card price in basket).
- **Verified on recorded pages, not live.** The fixtures are trimmed Wayback Machine raw captures of the Gold Kiwi 4pk page
  (2025-09-07) and the Aussie shampoo page (2026-11-04, with a promotion), marked as recorded in their first comment; the two
  hand-written ones are gone. Cloudflare currently answers the reader's profile with a 403 challenge, so nothing was run against a
  live page. The reader still stops on a refusal; this changes nothing about that.


## Catalog 1/14: the PC catalog probe (2026-11-14, issue 127 of epic 126)

Owner's words, 2026-11-14: "I believe we should have 3 apis that is going to be combined into one. We need scrapers that are going to
give us catalog from dunnes, tesco, lidl, aldi, etc. After that, with having those apis - we can do our job? Linking similar products,
comparing prices, and so on." Before anyone builds the Tesco and Dunnes catalog sweeps (issues 135 and 136), one read-only command
finds out what those two stores will give a browser on this PC.

- **The command.** `npm run probe -- --store tesco|dunnes [--out file.json]` (`pc-reader/src/probe.ts`). It opens the reader's own
  profile through the same `openBrowser` as the reader (same directory, channel and `BASKET_READER_HEADLESS` as `npm start`; the
  profile `npm run profile` leaves is the one it uses), then asks for `/robots.txt`, `/sitemap.xml`, the home page, one category page
  and one product page, one at a time, 5 s apart (`MIN_GAP_MS`, reused from `run.ts`), no retries. The category and product addresses
  are the first links of the right shape on the previous page. The **first refusal** (the reader's own `detectBlock`: 403/429, Akamai
  "Access Denied", Cloudflare "Just a moment...") ends the probe and is the result; the later steps are listed as not asked.
- **The report** is one JSON document, to stdout or `--out`: status, content type and size per address; robots `Disallow` lines,
  `Sitemap` lines and crawl delay; sitemap kind and entry count; the JSON/XHR addresses each page fetched for itself (browser
  network events, the Aldi discovery method); one trimmed example product record with its field names; whether a barcode-looking key
  (`gtin*`, `ean*`, `upc`, `barcode`, 8 to 14 digits) appears and where; paging parameters in the page's own calls and links; and the
  count, page-size and page-number figures found in the listing's JSON. Long strings and arrays are cut, no cookies or headers are
  recorded, and the probe never reads or prints configuration.
- **Reused, not rebuilt.** The browser launch, profile and politeness (`browser.ts`, `run.ts`), the block detection
  (`parse/block.ts`), and the profile resolution (`config.ts`, which gains `loadProbeSettings`; no token needed). Reusable by the
  later sweeps: `probe/discover.ts` (capture of a page's own JSON/XHR calls) and `probe/report.ts` (the report writer).
- **What it does not touch: the ingest door, the fleet identity, the matcher.** It posts nothing, signs in to nothing and never
  reaches basket's `POST /api/ingest/prices`. The sweeps that follow reuse that existing door (same batch shape, same fleet identity
  by client credentials, same server-side matcher that links a store's product to a basket product); this probe only decides their
  endpoint, paging, barcode and page size.
- **First live run, 2026-11-14, headless, this PC: both stores refused the very first request.** `www.dunnesstoresgrocery.com/robots.txt`
  answered HTTP 403 (Cloudflare challenge page, 44 KB) and `www.tesco.ie/robots.txt` answered HTTP 403 (Akamai, 298 bytes). The probe
  stopped there for each store, retried nothing, rotated nothing and used no unblocking service. A headed run was not attempted: this
  shell has no display, and the browser and profile are the ones already refused. For Tesco the open call remains the owner's one-off
  manual `npm run profile` warm-up; no one did it on his behalf. Issues 135 and 136 therefore have no endpoint facts yet; they wait on
  a profile either store will serve, then one `npm run probe` run.

## Add a product by searching a shop (#31)
- **Borrowed pattern, named:** the add-item search with photos of Bring! and the Tesco app. Type a name, see the shop's products as
  tiles with their pictures, tap the right one; the picture is what settles which product is meant. The tiles are the Shelf's own
  tile (`cmn-selectable-card`, photo, name), so no interaction is invented. Paste-a-link stays as it was.
- **Where it lives.** A Search button beside Add in the Prices tab's add bar opens a bottom sheet. Whatever was typed in the add box
  is the first search (once it is two letters or more); the sheet searches on the Search button or Enter, not on each keystroke, so
  the shop is asked once per search. Tapping a result adds the product and closes the sheet.
- **Only Lidl can be searched, and the answer says so.** `GET /api/products/search?q=` (a signed-in person only, no fleet scope)
  reads `https://www.lidl.ie/q/search?q=...` with the same polite fetcher as the nightly pull (honest user agent, one request at a
  time per site, a refusal reported and never worked around) and returns up to 12 results `{store, url, title, imageUrl}` taken
  from the page's product tiles, plus one status per shop. Lidl's robots.txt disallows only `/q/search?id=*`; a `?q=` search is
  not covered. tesco.ie, aldi.ie and the Dunnes grocery site refuse the VPS (see their sections), so the server never requests
  them: they come back `searched: false` with a note, and the sheet shows the notes and "paste the product's link" rather than
  silence. If the PC reader later posts search results for them, the existing `POST /api/products/{id}/candidates` is where they go.
  A Lidl refusal or outage is the same answer with `searched: false` for Lidl and no results. So is a 200 page that yields no
  product tiles: a real search for a nonsense term still returns 200 with fallback/suggested tiles and no confirmable no-results
  marker, so an empty tile list can only mean the page layout changed. It is reported as a failed search with a note pointing at
  paste-a-link, never as "Nothing found". The sheet has no empty state of its own: when a search returns no results it shows the shops' notes.
- **Photos bypass the service worker.** The shops' pictures are cross-origin, and once the service worker controls the page (every
  returning visit) it fetches them itself under the `connect-src 'self'` of its own CSP, fails, and answers 504, so the sheet showed
  letters. The sheet loads each picture with `?ngsw-bypass=true`, so the browser fetches it directly under `img-src`. The CSP is
  not widened. The Shelf's own photos have the same fault and are left as they were.
- **Tapping adds the product once.** The client adds the product named as the shop names it (cut to 120 characters) with
  `POST /api/products`, then links the tapped page and photo with `PUT /api/products/{id}/stores/lidl`. That second call is made by
  a person, so it is a confirmed match (`confirmed_at`), as a pasted link already is, and Lidl's nightly read prices it from then on.
  A name already on the shelf is refused with the usual "already tracked" message and nothing is linked. If the link fails after the
  product was added, the product stays and the person is told; it can be linked from the Prices tab.
- **The other shops' candidates.** The new product is an ordinary product, so the PC reader's candidate posts and the barcode route
  match against it as for any other; this change adds no matching of its own and runs no model.
- **No migration.** Nothing new is stored; the product, the listing and the photo address use the existing tables and columns.
- **Screens.** The Prices screen gains the Search button in its add bar and a longer empty-state line, so its Linux baseline changes.
  It is re-rendered by the "Update screen baselines" workflow on this branch, not committed from a local run. The sheet is not a route.

## Seeded typical section order per chain, the fallback of walk order (2026-11-14, issue #42, epic #25)

Owner's words: "in lidl all the products are almost in the same position - what i mean is that first of all there is fruit section
then meat and so on and so on. And once my partner drafts the list - I wanted to see it sorted according to what goes after
another. So i get predictable list by what is closest to me."

- **Borrowed pattern, named:** the default aisle order of AnyList and the Tesco app: a fixed, typical order of departments that is
  used until the app knows better. No interaction is invented, and nobody enters an aisle by hand: not the owner, not the partner.
- **One order per chain.** Lidl, Aldi, Tesco and Dunnes each have a seeded list of sections in walking order, in code
  (`SeededSectionOrder`): Fruit & veg, Bakery, Meat & fish, Dairy & eggs, then Frozen, Cupboard, Drinks (Tesco and Dunnes put Frozen
  after Drinks), Household, Health & beauty, Pet, and Other last. The order is a typical guess for a shop of that chain, not a
  floor plan; the order learned from trips (#43) replaces it for a store once that store has enough trips.
- **The unit is the product's category.** A product's section comes from the shop-page breadcrumb kept on its listing (#41), read
  from the innermost part outward, so "Fresh Food > Milk" is Dairy & eggs and not Fruit & veg. A product with no category, or one
  no section claims, is in Other and sorts last. Matching is by plain keywords, fixed in code, and a keyword counts only as a whole
  word (with an optional plural), so "tea" is not found in "steak".
- **Where it can be read.** `GET /api/walk/seeded` (signed-in people only) returns the sections of each chain in order, read-only.
  The basket is not re-sorted yet: showing the basket in walk order is #44, which applies the learned order where there is one and
  this one where there is not.
- **No migration, no new screen.** Nothing is stored and no page changes, so the screen baselines are untouched.

## Tesco pages linked for Golden kiwi and Salt (2026-11-14)

The first Tesco read attempt found two catalog entries with no Tesco page: Golden kiwi and Salt. Both were linked in the production
catalog with the fleet identity (`PUT /api/products/{id}/stores/tesco`, scope `basket:catalog-add`; no code change, the route and
its address check already exist). Read back with `GET /api/products`, each shows the address below, unconfirmed (a machine linked it,
so a person relinking still wins) and not yet read (Tesco stays paused on the owner's profile warm-up).

- **Golden kiwi:** `https://www.tesco.ie/groceries/en-IE/products/322156865`, "Keelings Golden Kiwi 4pk", about €3.29. Tesco's own
  "Super Sweet Gold Kiwi 3 Pack" (`300734807`) was passed over because it showed out of stock and is a 3 pack; "Donnelly Fresh Gold
  Kiwi 4 Pack" (`317545095`) is the other 4 pack. Keelings is the plainest 4 pack whose name is "Golden Kiwi", matching Dunnes's 4pk
  on the same product. **Moderate confidence**: Tesco sells no unbranded golden kiwi that was found, so this is a judgement call.
- **Salt:** `https://www.tesco.ie/groceries/en-IE/products/251975227`, "Saxa Salt 750G", fine table salt, about €2.55. No Tesco
  own-brand plain table salt turned up (the own-brand finds were low-sodium salt), so the plainest regular item is Saxa. **Good
  confidence.**

**How they were found.** Ordinary web search only; tesco.ie was never opened with the reader or any automated browser (a plain fetch
of a Tesco page answered 403 anyway), so titles and prices come from search results and the pages were not looked at. Each address has
the `/products/<id>` shape that `Stores.TryNormalizeProductUrl` accepts for Tesco; basket's own check accepted both.

## Appearance saved per account (2026-11-14, family-align step B3)

Owner's words (2026-11-12): "Basket should be just in our ecosystem, reflecting 90% of stuff, including login, settings and so
on. Design and stuff like that. The only difference is domain basket application is about. Everything else - it should be the same
as our apps. Like its one family bro, family of products. Same UI, same UX, same library, everything is the same. Only difference
is basket use cases and business logic." and, on the plan, "I agree on everything bro for basket". Theme mode and the seed colour
now follow the person to every device they sign in on, as in finance-sentry, instead of living in one browser.

- **Borrowed pattern, named:** the account preferences resource of the lifekit app-shell standard (`docs/APP-SHELL-STANDARD.md`
  sections 2 and 3 in lifekit-common) in the shape of finance-sentry's `PreferencesController`: one resource, `GET` and `PATCH
  /api/me/preferences`, body `{appearance: {theme, seed?, intensity?}}`. Basket serves the same JSON, so the library's
  `accountPreferencesStore` and `AppearanceService` work on it unchanged.
- **Contract.** `GET` always answers the whole object (`theme` is `system` for a person who has saved nothing). `PATCH` takes the
  fields to change; a present `appearance` replaces the stored one wholesale (an omitted `seed` or `intensity` clears it), an omitted
  field is untouched, and the answer is the whole object after the change. `theme` is `system`, `light` or `dark`, `seed` is
  `#rrggbb` (stored lower case), `intensity` is 0-1; anything else answers `400` with an `error` code (`theme_invalid`,
  `seed_invalid`, `intensity_invalid`) and stores nothing. Only the choices are stored, never the derived colours: each client
  derives the palette with the tokens engine. Answers carry `Cache-Control: no-store`.
- **One migration, approved by the owner's words above (the plan names it): `UserPreferences`.** One small per-user table,
  `user_preferences` (`user_id` primary key and foreign key to `users` with cascade, `theme`, `theme_seed`, `theme_intensity`,
  `updated_at`). It is additive: nothing existing is altered, `Down` drops only this table, and a person with no row reads as the
  defaults, so the row appears with the first save and no data has to be moved. The table is a row per person, not columns on
  `users`, so the Identity schema stays untouched and the next account preference is a column here, not another change to Identity.
- **Two first saves meeting.** The save is one `UPDATE`; when it finds no row it inserts, and if the other request inserted first
  the primary key refuses and the loser updates the row the winner made. No lock, no 500.
- **The signed-in person's own copy.** The route is behind the cookie scheme like the rest of `/api`, so the fleet identity (which
  has its own scope-checked doors) cannot read or write it, and her colour never reaches the owner's screen.
- **Frontend.** `AppPreferencesStore = accountPreferencesStore('/api/me/preferences')` and `provideAppPreferences()` in
  `core/preferences`: `provideAppearanceSync(AppPreferencesStore)` plus an effect on the session. Someone signed in (a passkey,
  the lifekit sign-in, or a page load with a live cookie) makes it `load()`, and the server's copy wins over the device cache.
  The session ending, by Sign out or by a 401 that dropped it, makes it `clear()`, so the next person starts clean. The device
  cache (`cmn-theme`, `cmn-theme-seed`) is left, because the sign-in page paints from it. `provideToastPreferenceErrors()` was
  already in `app.config.ts`, so a refused save rolls back and says so.
- **The design preset stays per device**, as the library keeps it (`cmn-preset`; see "Design selection in Settings"). The line there
  about "no backend change and no migration" was true for the preset and still is; this change is about the colour and the mode,
  not the preset.
- **Screens.** The control under Settings > Appearance now captions itself "Synced to your account" instead of "On this device",
  so the two desktop `settings` Linux baselines (1280, light and dark) change (496 and 497 px differing, all of it the new
  caption); the phone ones (390) do not move. Baselines are written only by the "Update screen baselines" workflow on this
  branch, never rendered locally; an earlier locally rendered pair was a slip and the workflow's CI-rendered images replace it.
  The e2e mock for the signed-in screens answers `/api/me/preferences` with `system`, so no other screen moves.
- **Tests.** Backend: `PreferencesTests` (defaults, a save returning the whole object, wholesale replacement, an omitted field left
  alone, one copy per person, each invalid value refused with nothing stored, concurrent first saves leaving one row, deleting a
  person deleting their row) and the two verbs in the anonymous-401 list. Frontend: `preferences.provider.spec.ts` (nothing asked
  while signed out, load after sign-in, clear on sign-out, a fresh read for the next person) and `e2e/appearance-sync.spec.ts` (the
  account's theme wins after sign-in and is what Settings shows; choosing a mode in Settings is `PATCH`ed).

## Lidl Plus receipts: Lidl changed its receipts service (2026-11-14, issue #26, epic M8 - the pull works again)

- **What broke.** With the household sign-in connected and accepted, the nightly pull still filed nothing. Real requests on 2026-11-14
  showed three changes on Lidl's side (the list, 264 receipts, was fine): the `App-Version` header basket sent (`999.99.9`) now makes
  Lidl reset the HTTP/2 stream, so every call ended as `lidl_unavailable`; a receipt on `api/v2/{country}/tickets/{id}` answers 400
  with an empty body; and the working `api/v3/{country}/tickets/{id}` has no item array any more.
- **Request.** `AppVersion` is now a `LidlPlusOptions` setting with a realistic default (`16.40.2`), not a literal: Lidl accepts a real
  release and turns a made-up one away, so it is raised in configuration when Lidl starts refusing again. A receipt is read from
  `api/v3/...`; the list stays on `api/v2/...`. v3 refuses a language longer than two characters (its 400 said `LanguageCode must be 2
  characters or fewer`), so the default `Language` is `en`, sent as `Accept-Language` exactly as configured (a longer tag would be
  refused by Lidl). The language is sent in the header only: that is the one place basket sends one, but whether v3 reads the header
  or wants it elsewhere is inferred from the error, not seen.
- **Lines.** The v3 detail carries the day, `totalAmount` and the shop, but the purchased lines are only inside `htmlPrintedReceipt`, an
  HTML string (a `<pre>` of spans). `LidlPrintedReceipt` reads it with a tolerant reader (regex over the span tags, no HTML library):
  - an `article` span is a line: name from `data-art-description`, shelf price from `data-unit-price`, quantity from
    `data-art-quantity` (else 1). A second `article` span with the same `data-art-id` whose text is `N x price` is that line's detail
    (it can supply the quantity, and is never a second line); a repeated article without that text is a second scan and a second line,
    which the importer already folds into one product;
  - a `discount` span takes its amount (the number closing its text, `-0.30`, `-0,30` or `0.30-`) off the line printed just before it;
    one with no line before it, after a line that could not be read, or with no readable amount (a percentage) is left out;
  - the line's amount is `unit price x quantity` less its discounts, so the paid price is the shelf price after a coupon. It is computed
    from the attributes, not read from the printed text, because the text is a formatted receipt line whose number could be either
    a unit price or a line total. A fractional quantity is a weight in kilos (`IsWeight`), and then the unit price is a kilo's;
  - decimal commas and points are both read; a line without a name or a readable price, or with a quantity of zero, is skipped and
    the rest of the receipt is still read. The currency span is not read: basket is euro-only.
- **Unchanged.** A receipt still becomes a closed trip on its print date, lines are matched to products by name, the paid price is kept
  with its day, a filed receipt is never opened again, and the pauses and limits are the same. `LidlReceipt` is the same model; the total is
  `totalAmount`.
- **A receipt with no readable line is `skipped`, not filed empty.** Because a filed receipt is never opened again, an empty trip would
  hide a future change of the print for good. Left unread it is logged and tried again on the next pull, and it does not use up the
  pull's limit. The price of that safety is one extra request a night for a receipt that is truly empty (none seen).
- **Tests.** Synthetic only: the repository is public, so the fixtures are built from the masked structure of the print with invented
  products, and no real receipt, article, token or store address is committed. `LidlPrintedReceiptTests` covers the quantity pair,
  weights, discounts, decimal commas and unreadable lines; `LidlPlusTicketsClientTests` covers the v3 address, the headers and the
  receipt as a whole. Nothing was run against Lidl; the first real pull after the deploy is the live check.
- **No migration, no new setting to set.** `LidlPlus:AppVersion` and `LidlPlus:Language` are optional overrides of their defaults.

## Catalog 2/14: the catalog tables and the catalog ingest (2026-11-14, issue 128 of epic 126)

Owner's direction, 2026-11-14: per-store catalog readers feed one combined catalog API, and linking similar products and price
comparison are built on it. The design is the catalog scout's report (sections 6 and 7). This issue is its data model and its write
door; no sweep, search, linking or screen is here (issues 129 to 140).

- **Supersedes "prices: his products only".** The plan's 2026-10-29 call (basket reads prices only for products the household put on
  its shelf, outside this repository) is replaced by the owner's 2026-11-14 direction: a shop's whole catalog is read, shelf or not.
  The shelf `Product` stays the canonical node; the catalog is a separate table that the shelf's 300-product guard does not bound. Prices
  of shelf products still come from `store_listing` and `price_observation`, untouched.
- **Reused, not rebuilt.** The door is the existing ingest: `POST /api/ingest/catalog` and `/api/ingest/catalog/complete` sit beside
  `POST /api/ingest/prices` and run the same admission (`IngestEndpoints.AdmitAsync`, extracted from the price route so there is one
  check): an owner-minted `bsk_` token, or the fleet identity with the existing scope `basket:price-ingest` (no new Logto scope, so no
  account step), the same `ingest` rate limiter (30 requests a minute per client, shared by every ingest route), the same 256 KB body cap
  and 200 items a batch, the same reason-code style (`unknown_store`, `store_not_ingested`, `url_invalid`, `price_invalid`,
  `dates_invalid`, `duplicate`, plus `external_id_invalid` and `no_name`), 200 when anything was kept and 422 when nothing was. Size,
  barcode and photo go through the matcher's `ProductSize` and `Gtin` and the price feature's `ImageUrls`; the shop's product address
  through `Stores.TryNormalizeProductUrl`. Nothing in the matcher or the fleet identity changed.
- **One write path, two callers.** `CatalogRecorder` upserts by `(store, external_id)` and is what the server's own sweeps (Aldi, Lidl)
  will call in process, as `LidlPriceSource` calls `PriceRecorder`; the endpoint is the PC reader's way in. `CatalogReading.TryCreate`
  is the single validation and normalisation, so both callers get the same rules.
- **The tables (migration `StoreCatalog`, purely additive).** `store_product` (one row per product per shop: name, brand, size text and
  the size reduced to `size_amount`/`size_unit` (`g`, `ml`, `each`) per pack plus `size_packs`, the shop's unit price and its basis,
  GTIN-14 barcode, category path, https photo address, `first_seen_at`, `last_seen_at`, `discontinued_at`), `store_product_price`
  (history) and a nullable `store_listing.store_product_id` foreign key (`ON DELETE SET NULL`), not backfilled. Indexes: unique
  `(store, external_id)`, `barcode` where not null, `(store, last_seen_at)`, and a GIN index over a database-generated `tsvector`
  of name and brand (`english` configuration; no extension). Nothing existing is dropped or rewritten; rollback is dropping the two
  tables and the column.
- **`size_packs` is not in the scout's sketch.** A "6 x 330 ml" and a single 330 ml bottle must never read as the same size (the matcher's
  rule), so the pack count is kept beside the per-pack amount.
- **Price history is write-on-change.** A `store_product_price` row is written only when the price tuple (price, was price, loyalty
  price and scheme, promo text, dates, in stock) differs from the latest row; `observed_on` is the Dublin day the value first appeared.
  A different reading on the same Dublin day replaces that day's row. A product with no price at all (Lidl's in-store-only lines) is
  stored without one, not rejected; a price that disappears is recorded as an empty row. About 40,000 products times a daily row would
  be 14 million rows a year for almost no information.
- **A thinner reading never erases.** A later reading without a barcode, photo, brand or category keeps the stored one; a barcode that
  is not a valid GTIN or a photo that is not a plain https address is dropped, and the product is kept. A barcode is evidence that two
  products are the same, so a malformed one is never stored. A size that cannot be read, or whose amount is too large for the column, is likewise dropped
  rather than rejecting the product or the batch.
- **Server-dated.** `last_seen_at` is the server's clock, never the poster's.
- **Closing a sweep.** `/complete` takes `{store, seen, startedAt}`. Products last seen before `startedAt` are marked
  `discontinued_at` only if the sweep covered at least 90 percent of the live products known before it. Both numbers must clear the
  bar: the reader's `seen` and the server's own count of the known products the sweep actually listed (new products do not make up for
  missed ones, and a reader that claims more than it posted cannot get the rest delisted). `startedAt` must be within the last 24 hours
  and no more than 5 minutes ahead, so a skewed PC clock cannot make everything look unseen. A product listed again comes back. Each
  closed sweep writes one `price_pull_run` row (`Ok`; `Partial` or `Failed` with the reason in the message when the guard held), which the
  Prices page already reads; an individual batch writes none. A sweep that listed no products is `Failed` even on a shop with nothing
  known yet. On a first sweep (nothing known) a reader that counts more products than the server saw is never `Ok`, since the server's
  own count is the only evidence; once products are known, `seen` stays only the lower bound of the 90 percent guard, so one item the
  server refused (or a product repeated across batches) does not stop a healthy sweep from ever delisting anything.
- **Which shops post.** The shops `Stores.ReadBy` calls `pc` (Aldi, Tesco, Dunnes today); Lidl is read by the server and gets
  `store_not_ingested`. `ReadBy(Aldi)` stays `pc` here: the server-side Aldi sweep (issue 129) flips it together with the
  sweep itself, so the page never says "nightly" for a shop nothing reads yet.
- **Tests.** Contract tests for both endpoints (`CatalogIngestTests`: admission, scopes, reason codes, limits, rate limit, write-on-change,
  the 90 percent guard and the run row), recorder tests against Postgres (`CatalogRecorderTests`, including the generated `tsvector`), and
  pure tests for the validation (`CatalogReadingTests`). Fixtures are invented products shaped like the scout's Aldi and Lidl records;
  no real catalog data and no tokens are in this public repository.

## Catalog 4/14: the Lidl catalog sweep (2026-11-14, issue 130 of epic 126)

Owner's direction, 2026-11-14: per-store catalog readers feed one combined catalog API. This is Lidl's reader, the server-side one.
lidl.ie is a showcase, not a shop: it lists its whole range (about 1,440 products, about 690 of them food) but shows an everyday price on
only part of it (the catalog scout measured 56 percent of listed tiles with a regular price, the rest with only a Lidl Plus price or none).
So this sweep reads the range; the paid price keeps coming from the Lidl Plus receipts, and the week's offers from the three offer pages.

- **Reused, not rebuilt.** No new door, table, identity or matcher. The sweep runs on the VPS and calls `CatalogRecorder` in process (the
  same write path `POST /api/ingest/catalog` ends in for the PC reader), validates each tile with `CatalogReading.TryCreate` (so barcode,
  size and photo go through the matcher's `Gtin` and `ProductSize` and `ImageUrls` exactly as a posted product does), and closes through
  `CatalogIngest.CompleteAsync` (the 90 percent discontinued guard and the `price_pull_run` row every shop has). No fleet token is used: the
  token is for readers outside the server, and `Stores.ReadBy(Lidl)` stays `nightly`, so the ingest door still answers Lidl with
  `store_not_ingested`. No migration: the tables are the ones from issue 128.
- **What it reads.** `LidlParser.ParseCatalogTiles` reads the product tiles of a category listing page with the tile reader the offer pages
  already use (`erpNumber`, title, brand, price, Lidl Plus price, dates, photo), plus the pack text (`price.packaging.text`, the size) and
  `keyfacts.wonCategoryPrimary` as the category path, without its shared root "Worlds of need". A tile with no price is a product stored
  without one; it is not an error and writes no price row. A tile with no product number or no address is skipped.
- **Which pages.** Listing pages are walked by `?offset=` in steps of 12 (offset 0 is the plain address) until a page brings no product not
  already seen, with a hard cap of 120 pages a category (a fixed ceiling, not a setting). The categories are the food pages of the page
  sitemap only: `/static/sitemap.xml` (the index robots.txt names) lists the sitemaps, the one whose file name starts with `pages` lists
  the `/c/<slug>/s<id>` category pages, and a page is food when its hyphenated slug contains one of `Prices:LidlFoodWords` as a whole word
  ("fresh-fruit-and-vegetables", not "garden-furniture"). A product is stored once however many categories list it. A `.xml.gz` sitemap is
  a file, not a transfer encoding, so `PoliteFetcher` unpacks an address that ends in `.gz` (bounded at 20 MB unpacked).
- **A sweep that missed something delists nothing.** A listing page or sitemap that is down, a first page of a category that shows no
  product (a changed layout reads the same as a failed page), or a category still listing new products at the 120-page ceiling makes the
  sweep incomplete. It is not closed through `CatalogIngest.CompleteAsync`: its `price_pull_run` row is `Partial` (`Failed` when it read
  nothing) with the reason, and no product is marked discontinued, however small the gap (the 90 percent guard only catches large ones).
  The products it did read are still stored. Only a sweep that walked every category to its end is closed and may delist.
- **Barcodes.** Tiles carry no GTIN; the product page's JSON-LD does (`gtin13`, a list with one entry per variant). After the sweep,
  the product pages of products with no stored barcode are opened, at most `Prices:LidlBarcodePagesPerNight` (200) a night, and the first
  valid GTIN is stored (as GTIN-14, through `CatalogRecorder`, which never lets a thinner reading erase one). A first sweep therefore takes about three nights to fill the food range; afterwards only products new to the catalog are opened.
  The candidates are in product-number order but start at a place that moves on by the cap each UTC day (wrapping round), so products
  whose page is gone, down or without a valid GTIN are skipped and tried again on a later night without ever holding the whole cap (no
  column records the attempt, because there is no migration). A malformed barcode is never stored.
- **Never the search API.** `/q/api/search` (what the site's script calls) answers 401 without a token the page injects; it is not used and
  not worked around, and neither is anything that needs a login.
- **A refusal ends it.** A 403, 429 or access-denied page (`FetchStatus.Blocked`) stops the sweep at once; the products read so far stay,
  nothing is marked discontinued, and one `price_pull_run` row with outcome `Blocked` records it. A listing page that is merely down ends
  that category only and makes the sweep incomplete (above). Nothing is retried.
- **When.** `LidlCatalogJob`, a Hangfire job of its own at 03:30 Europe/Dublin, half an hour after the price pull, so the two never share the
  shop's politeness gap. A failure of the sweep is recorded as a `Failed` run and leaves the nightly price pull untouched. Another shop's
  sweep (issue 129) is its own task and registers itself.
- **Kept as they are.** The three offer pages and `LidlPriceSource`'s per-listing read: the shelf's listings still get their daily
  `price_observation` row from them. Mirroring catalog readings into linked listings is the link-to-shelf issue's (132), not this one.
- **Tests.** Parser tests on synthetic pages (`LidlCatalogParserTests`: tiles priced and unpriced, the sitemap, the food filter, the
  GTIN), sweep tests against Postgres with a fake fetcher (`LidlCatalogSweepTests`: the offsets walked, the barcode cap and the
  skip of products that already have one, the daily rotation of the cap, write-on-change on a repeat sweep, a refusal, an unreadable
  sitemap, and the incomplete sweeps that delist nothing) and gzip tests for the fetcher. The fixtures are invented products and invented ids shaped like lidl.ie's pages; no real catalog data and no tokens
  are in this public repository, and no test reaches a shop.

## Catalog 5/14: catalog search and detail (2026-11-14, issue 131 of epic 126)

The read side of the combined catalog that issue 128 put in the database: `GET /api/catalog/search` and `GET /api/catalog/{id}`. No
screen yet (a later issue builds it on these two answers) and no migration: the generated `tsvector` and its GIN index came with
issue 128 and are used as they are.

- **Reused, not rebuilt.** The door in is still issue 128's: `POST /api/ingest/catalog` behind the owner-minted token or the fleet
  identity with `basket:price-ingest`, writing through `CatalogRecorder`. The door out takes the policy the shelf's product list
  already has, `catalog-read`: a signed-in person (owner or member), or the fleet identity holding the existing scope
  `basket:catalog-read` (no new Logto scope, so no account step). A request with a bearer header is judged on the fleet token alone,
  as everywhere else; a token with another scope is 403, no credential 401. Nothing in the matcher (`CandidateMatcher`, `ProductSize`,
  `Gtin`) or the fleet identity changed: size and barcode were already normalised when the row was written, so reading is plain.
- **Search.** `q` is split into words (letters and digits only, lower-cased, at most 8 of the first 80 characters) and each word is a
  prefix of the `english` full-text configuration the index was built with: `choc milk` is `choc:* & milk:*`, so a half-typed word
  still finds things and every word must match, in the name or the brand. The words are the only thing that reaches
  `to_tsquery`, so query syntax typed by a person is never query syntax. A query with fewer than two letters is 400
  `query_too_short`. Results are ordered by `ts_rank`, then name, then id, so equal ranks (the usual case) come back in the same
  order every time. At most 50 come back; `limit` may lower that (1 to 50), never raise it. There is no paging: a person who
  needs the 51st result types more of the name.
- **Store filter.** `store` takes a slug in any case (`lidl`, `aldi`, `tesco`, `dunnes`); another value is 400 `unknown_store`
  rather than an empty list, so a typo is not read as "nothing there". Absent means every shop.
- **Discontinued products.** Search leaves out a product whose `discontinued_at` is set (a complete sweep no longer listed it): a
  tile for something nobody sells misleads. The detail still answers it, with `discontinuedAt`, because a link kept from earlier
  must not turn into a 404 that looks like a bug.
- **What a result carries.** Id, shop (slug and display name), name, brand, size as the shop writes it, the shop's own unit price and
  its basis, the https photo address, the product page, the latest price and `lastSeenAt`. The price is the shelf's own `PriceResponse`
  (state `current`, `upcoming`, `ended` or `unavailable` through the same `PriceStates` rule, price, was price, loyalty price and
  scheme, promo text, dates, `best`, in stock), built from the product's newest `store_product_price` row, so the screen formats a
  catalog price exactly like a shelf price. Its `observedOn` is the Dublin day that value first appeared (history is write-on-change),
  not the day it was last confirmed; `lastSeenAt` says how fresh the product is. A product with no price yet has `price: null`.
  The detail adds the parsed size (`sizeAmount`, `sizeUnit`, `sizePacks`), barcode, category, `firstSeenAt` and `discontinuedAt`.
- **Read only.** No route here writes, and nothing is created by searching. No history endpoint, shelf link or equivalents list:
  those are their own issues (linking is 132, equivalents 138).
- **Tests.** `CatalogEndpointsTests` runs against Postgres so the generated vector, the ranking and the prefix match are what the
  tests exercise: shape of a result and of the detail, brand and stem matching, every-word prefix matching, ranking and the name
  tie-break, the store filter and its refusal, the 50 cap and `limit`, discontinued handling, too-short and syntax-shaped queries,
  price states and the latest of several readings, and who may read (owner, member, fleet scopes, no credential).
  `AuthGuardTests` lists both routes as 401 when signed out. The frontend gets the typed client (`CatalogApi`, `catalog.model.ts`) and
  its spec; there is no page or route, so no screen baseline changes. Every product in the fixtures is invented.

## Catalog 6/14: link to shelf (2026-11-14, issue 132 of epic 126)

A shelf page (`store_listing`) and the catalog row of the same product (`store_product`) were two unrelated records. This issue joins
them and lets the catalog feed the shelf's price history, so a sweep keeps Plan, By shop, Promotions, Scorecard and the Shelf cards current
without a per-page read.

- **The join key is the shop's own product id in the address.** `Stores.ProductId` reads it off a normalized address (Aldi and Dunnes: the
  trailing number; Lidl: the number of `p<number>`; Tesco: `/products/<id>`), which is the `external_id` the catalog keys the product under. A
  listing is linked to the `store_product` of the same shop and id and to nothing else: no name or barcode guessing, so a link is never a
  match decision. A page whose catalog row does not exist yet stays unlinked and is linked the moment a sweep lists the product. The matcher
  (`CandidateMatcher`) is not involved: it proposes which page is the shelf product's equivalent, this only says which catalog row a page is.
- **Existing listings: migration `StoreListingCatalogLink`.** One idempotent `UPDATE` (data only, no schema change) fills
  `store_product_id` for the unlinked listings whose address names a catalog row, with the same rules as `Stores.ProductId`
  (`StoreListingCatalogLink.LinkSql`, which a test runs). It touches only `store_product_id`, so `confirmed_at`, the address and the
  readings are untouched; with no catalog rows it updates nothing, and run again it changes nothing. The migration runs on deploy.
- **Going forward: set on link.** `ProductEndpoints.PointListingAsync`, the one place a listing is created or pointed at a page, now sets the
  link: a person pasting a page, a person confirming a proposal, and the fleet linking a page (`basket:catalog-add`) all go through it. A
  changed address recomputes it, so an old product's link never survives a relink, and a page pointed at again picks up a catalog row that
  has arrived since. `confirmed_at` semantics are unchanged: a fleet-linked page is linked to the catalog but stays unconfirmed, and a
  sweep never confirms, relinks or changes the address of a page.
- **The mirror lives in `CatalogRecorder`, so every caller gets it.** After the upsert, the recorder links the shop's unlinked listings whose
  address names a product of the batch, then records each batch reading of every linked listing through `PriceRecorder.RecordAsync` as that
  listing's observation for the Dublin day (`price_observation`, one row per listing per day). That is the same path the nightly Lidl job
  and the price ingest use, so a day read both ways keeps the later reading and nothing downstream (Plan, By shop, Promotions, Scorecard,
  Shelf) changed: they already read `price_observation`. The HTTP door (`POST /api/ingest/catalog`, token or fleet identity, existing
  limiter and size caps) and the server's own sweeps (issues 129 and 130) call the one recorder; no new endpoint, scope or identity. Every
  listing that is the product is fed, including two shelf products that point at one page.
- **What is mirrored.** The price tuple, the title, the photo and the category, as a page read would: `PriceRecorder` keeps the last good
  photo and category when a reading has none. A reading with no price at all that is in stock says nothing a shelf page can use and is not
  mirrored (the price ingest refuses it the same way); a product that is out of stock is mirrored as unavailable. The catalog's own
  history (`store_product_price`, write on change) is unaffected.
- **Copy that depended on `ReadBy`.** The product row said "Waiting for the next PC read" for every PC-read shop with no reading, including a
  shop where the product has no page at all, which nothing waits to read. It now says "No page linked yet" whenever there is no listing, and
  keeps "Waiting for the next PC read" for a linked page of a PC-read shop. `ReadBy` itself is unchanged (Aldi stays `pc` until its
  server-side sweep, issue 129, flips it).
- **Not here.** No sweep, no reading of the catalog by the screens, no search or detail endpoint (issues 129, 130, 131, 133 and later).
  Seeding a newly linked page's first observation from the catalog's latest price is not done: the next reading of the product does it.
- **Tests.** `CatalogLinkTests` (address ids per shop, the migration statement on a populated, an empty and a repeated run, link on paste,
  relink and fleet link, the mirror: replace within a day, a new row the next day, unlinked and other-shop pages untouched, no-price and
  unavailable readings, `confirmed_at` untouched) and the price formatting specs. The existing plan, by-shop, promotion and scorecard tests
  pass unchanged. Fixtures are invented products; no real data or token is in this public repository.
