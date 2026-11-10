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
  pages keep `bk-auth-intro`; they are not part of this change.
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

- **No app-local copy.** `provideWhatsNew` gets basket's version (`src/app/core/version.ts`, checked against `package.json` and
  the notes file by a spec) and a `basket.whatsNew.lastSeen` marker. The sheet, the list, the dot and the seen marker are the library's.
- **Notes.** `src/whats-new/notes.md`, plain second person; `lifekit-whats-new` runs before `start`, `build` and `build:prod`,
  and `--check` runs in `npm run lint`, so CI fails when the committed `public/whats-new.json` is stale.
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
  `trip_item` (`id`, `trip_id`, `text`, `checked_at`, `checked_by_user_id`). No shops, prices or planned store: marking a planned
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
- **Free text, as the list is.** Reloading copies the typed text; there is no link to a product, so a later change to the
  catalogue never rewrites a past trip.
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

## Shell before the session call

- **The shell draws before `/api/session` answers.** `AppComponent` holds the signed-in shell until the first navigation
  ends (the guards wait for the session, then the page's chunk loads): `cmn-app-layout` with `[accountPending]="true"`, and
  in the page's place a `cmn-page` of the address's kind holding the matching `cmn-skeleton` preset (`list` for every page
  today) inside a `role="status"` "Loading" region. Only the routed content waits for the session. Sign-in, invite and the
  unreachable page draw no shell; their layout is a separate fix. Pattern: Apple HIG, Loading and Launching (show something
  right away) and the Carbon Loading pattern, as written up in lifekit-common's `docs/PHONE-CONTRACT.md`, "First paint".
  `e2e/cold-start.spec.ts` holds the audit's `basket-boot` probe: session delayed 900 ms, layout shift under 0.1 on every page
  at 390 and 1280.
