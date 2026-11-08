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
- **Polling, not SignalR or push.** The list must show the other person's changes, so it polls every 15 seconds while the
  page is visible (two people do not need SignalR); push belongs with a later milestone. M1 had no service worker; the
  shell-only one is "Offline shell and update prompt" below.
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
- **The work list is a local file the owner keeps.** The endpoint tells a token holder nothing about what is tracked, so
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
  Reword again when the library's pull-to-refresh lands.
- **Sign-in leads with the explanation and one primary action (Carbon Empty states; HIG Onboarding).** A `cmn-empty-state`
  (icon, "The shopping list for the two of you", "Sign in with the passkey on this phone. There is no password.", the
  passkey button in its `cta` slot) in the middle of the screen, the invite hint under it. The join and unreachable
  pages keep `bk-auth-intro`; they are not part of this change.
- **A Prices row is a value on the right and two short lines on the left (HIG Lists and tables; Carbon content density).**
  `detailLines` replaces the run-on `detail` string: line one is what the price is (card price, old price, promo, end
  date), line two is the reading (seen date, last paid, a failed read). A shop with no price yet ("Waiting for the next PC
  read") has one line saying why, plus the last-paid line when there is one. The row keeps the library's `cmn-list-item-row`; the line break is a `\n` shown with `pre-line`.
- **Out of scope here:** the offline shell / service worker (own task) and every library-side item (44px hit areas,
  skeleton before auth, swipe rows, pull-to-refresh, tab-bar text, startup image) stay with lifekit-common. The Linux
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
  The e2e config keeps `serviceWorkers: 'block'` for every other spec (the worker fetches around `page.route`); only
  `offline-shell.spec.ts` allows it, against its own server that answers `/api`.

## Fleet identity on the machine endpoints (2026-11-08, #62)

Owner decision 2026-11-08 ("Agree, thats good"): the fleet gets a service identity on his own apps. In the owner's Logto it is
the machine-to-machine application `fleet-agent` with one API resource (`https://fleet.lifekit.internal/api`) and one scope per
action class. People stay passkey-only on basket; this only adds a machine path.

- **Three endpoints, three scopes, nothing else.** `POST /api/ingest/prices` needs `basket:price-ingest`, `GET /api/products`
  needs `basket:catalog-read`, `POST /api/products` needs `basket:catalog-add`. Sign-in, invites, members, passkeys, ingest
  tokens, lists, product deletion and store linking take a signed-in person only, as before. A fleet token on any of them is
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
  values are host settings in `~/.config/basket/env`, not in git: `FLEET_AGENT_AUTHORITY=<IDENTITY_ENDPOINT>/oidc` (the issuer
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
  `Origin` through (and only on `/api/products`, as `/api/ingest` already did). A cross-site page cannot set that header, so a
  forged request still has nothing to ride on, and a signed-in browser with a bad bearer is refused, not served by its cookie.
- **No migration.** `product.AddedByUserId` is a required foreign key to a user, and the fleet identity is not one. A product the
  fleet adds is therefore recorded against the owner (the oldest owner account) rather than adding a nullable column or a service
  user; the `Fleet agent {ClientId} authorised for {Scope}` log line is where the fleet's own actions are told apart. If the
  owner wants "added by the fleet" shown on the product, that is a nullable column and a migration, which waits for his go.
- **Tests** use a signing key generated per run and tokens minted in the test; no real credential or Logto call is involved.
  They cover a valid token per endpoint, a wrong scope, an expired token, a token for another audience or issuer, a forged
  signature, an unsigned token, a scope-less token (the disabled application), every non-machine endpoint, and the path being off.
