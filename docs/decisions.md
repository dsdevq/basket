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
  `inStock`, `title`). Readings go through `PriceRecorder`, so they become the same one-per-listing-per-Dublin-day
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
  (and only that). The endpoint never reads the session cookie and answers 401 without a valid token, so a forged
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
- **The work list is a local file the owner keeps.** The endpoint tells a token holder nothing about what is tracked, so
  the reader reads `{store, url}` entries from `pc-reader/urls.local.json` (ignored by git), checked against the same
  product-page shape the server accepts. A page not linked in basket comes back `unknown_listing`. A server-provided work
  list (a token-scoped read endpoint) would widen what the token can learn and is the owner's separate decision.
- **Ordinary browser, nothing hidden.** Installed Chrome with a clean profile of its own (never the owner's), visible by
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
  redirects to it. basket accepts only the `/groceries/en-IE/products/<id>` shape when a product is linked
  (`Stores.TryNormalizeProductUrl`), so a `/shop/` address is refused as `url_invalid` by both the reader and the server.
  Widening the server's shape is a backend change outside the reader's scope and is left for the owner.

