# pc-reader

Reads Dunnes product pages (and Tesco's, while it is not paused, see below) in an ordinary browser on **your own PC and home connection** and posts the
prices to basket's write-only ingest endpoint (`POST /api/ingest/prices`). tesco.ie and dunnesstoresgrocery.com
refuse the VPS, so this is how those shops get prices. Aldi is no longer read here: the server's nightly job reads Aldi's product API
("Catalog 3/14: the Aldi sweep" in the decisions), and the ingest endpoints answer `store_not_ingested` for it (see [docs/decisions.md](../docs/decisions.md), "M2 decisions",
"Price ingest decisions" and "Dunnes: the server cannot read it, the PC reader does").
A product whose shop has not been read yet shows "Waiting for the next PC read" in basket.

**Tesco is paused for now.** The reader skips Tesco entries (from the catalog or the URL file) with one log line per run, not
an error, and reads Dunnes as usual. To read Tesco again, add `'tesco'` back to `ENABLED_STORES` in `src/types.ts`.
The Tesco parser, fixtures and tests are kept.

It is a small Node program: Chrome (installed on the PC, with a profile of its own) loads each page, a parser reads what
the page says, and the readings go to basket in batches. Run it by hand with `npm start`, or let the daily schedule do it ("Running it every day", below).

## Run it

Needs Node 24 and Google Chrome (or set `BASKET_READER_CHANNEL=msedge`).

```sh
cd pc-reader
npm ci
cp .env.example .env.local        # fill in the origin and the credentials
npm start
```

There are two ways to prove itself to basket; the reader uses the first one that is set.

- **The fleet identity (preferred).** The reader signs in to the fleet's identity service by OAuth client credentials
  (`BASKET_FLEET_CLIENT_ID`, `BASKET_FLEET_CLIENT_SECRET`, `BASKET_FLEET_ISSUER`) and gets a short-lived token for the scopes
  `basket:price-ingest` (push prices) and `basket:catalog-read` (read the tracked pages). It renews the token a minute before it
  expires. No token is minted in basket and no page list is kept by hand: the list comes from basket's catalog (below). Basket
  already validates these tokens (docs/decisions.md, "Fleet identity on the machine endpoints"); the reader asks for nothing else.
- **The `bsk_` token.** Make it in basket under **Settings > Workspace > Price reader** (owner only); it is shown once. It pushes prices
  but cannot read the catalog, so with it alone you keep the URL file (below), `cp urls.example.json urls.local.json`.

Files you fill in (`.env.local`, `urls.local.json`) are ignored by git. The secret and the token are never logged.

### The identity service over https, no tunnel

Logto runs on the VPS and publishes its issuer as the tailnet https address, `https://<vps>.<tailnet>.ts.net:3001/oidc`
(its discovery document at `<issuer>/.well-known/openid-configuration` says so, and every token's `iss` is that value).
Basket validates the same string (`FleetAgent:Authority`, see [docs/decisions.md](../docs/decisions.md), "Fleet identity on the
machine endpoints"), so the reader's `BASKET_FLEET_ISSUER` and `BASKET_API_ORIGIN` are both tailnet https addresses and the PC
needs nothing but Tailscale being up: no reverse ssh tunnel, no `127.0.0.1` address. (The `http://127.0.0.1:3001/oidc` form
this README used to give only worked while a tunnel from the VPS was open by hand.) The tailnet certificate is a real one, so
Node verifies it without any setting.

`npm run check` proves it from the PC, without reading a page or posting a price and without printing any token or secret:

```text
ok   issuer address: https://<vps>.<tailnet>.ts.net:3001/oidc (https, not a loopback address)
ok   discovery: answers, and reports the issuer https://<vps>.<tailnet>.ts.net:3001/oidc
ok   token: granted; iss https://<vps>.<tailnet>.ts.net:3001/oidc, audience https://fleet.lifekit.internal/api, scope basket:price-ingest basket:catalog-read
ok   basket: https://<vps>.<tailnet>.ts.net:10000 accepts the token (GET /api/products, HTTP 200)
```

It stops at the first step that fails and says why: a loopback issuer (tunnel-dependent), an issuer that differs from what
Logto reports, a token with the wrong `iss`, audience or scope, or basket answering 401 (its `FLEET_AGENT_AUTHORITY` is not that
issuer). Exit code `0` when all four pass, `1` otherwise.

| Setting | Where | Meaning |
|---|---|---|
| `BASKET_API_ORIGIN` | env or `.env.local` | Where basket runs, e.g. `https://basket.example.com`. `http` only for `localhost`. |
| `BASKET_FLEET_CLIENT_ID` | env or `.env.local` | The fleet identity's client id (the `fleet-agent` application). With the next two, replaces the `bsk_` token. |
| `BASKET_FLEET_CLIENT_SECRET` | env or `.env.local` | Its secret. Never logged, never in the repo. |
| `BASKET_FLEET_ISSUER` | env or `.env.local` | The identity service's issuer address, e.g. `https://<vps>.<tailnet>.ts.net:3001/oidc` (below); the reader posts to `<issuer>/token`. Keep it equal to the issuer Logto reports and stamps into its tokens (`npm run check` compares them). `http` is accepted only for `localhost`/`127.0.0.1`, which on the PC means a hand-held ssh tunnel and is not for regular use. Setting only some of the three is an error naming the rest. |
| `BASKET_INGEST_TOKEN` | env or `.env.local` | The `bsk_...` token, used when no fleet setting is present. Never logged. The environment wins over the file. |
| `BASKET_READER_URLS` | env or `.env.local` | The URL file (fallback list). Default `pc-reader/urls.local.json`. |
| `BASKET_READER_PROFILE` | env or `.env.local` | Chrome profile directory. Default `~/.local/state/basket-pc-reader/profile`: a profile of the reader's own, never your everyday one. `npm run profile` opens the same directory by hand (below). |
| `BASKET_READER_CHANNEL` | env or `.env.local` | Installed browser to drive. Default `chrome`. |
| `BASKET_READER_HEADLESS` | env or `.env.local` | `1` for a window-less run (no display). Default is a normal visible window. |
| `BASKET_READER_ENV_FILE` | env | Read settings from this file instead of `.env.local`. |

## Which pages: the catalog, then the URL file

With the fleet identity the reader reads the list from basket itself (`GET /api/products`, scope `basket:catalog-read`): every
product's linked page at a shop the PC reads (Dunnes, Tesco). Each address goes through the same check as the URL file
below; one that fails it is skipped and counted in the log. Link a product to a shop page in basket and the next run reads it.
An empty catalog means nothing to read (the file is not consulted).

The URL file is the **fallback**, used only when the reader has no fleet identity (the `bsk_` token cannot read the catalog) or
the catalog call fails (identity service or basket unreachable, a refusal); the run logs which. If the file is missing then too,
the run stops naming both reasons.

The ingest endpoint deliberately never tells a `bsk_` token holder which pages are tracked, so for that path (or as a fallback)
you keep the list yourself: a JSON array of `{ "store", "url" }`, `store` being `dunnes` or `tesco`, `url` the product page you linked to the product in basket
(the same address; a page that is not linked comes back `unknown_listing`).

```json
[
  { "store": "tesco", "url": "https://www.tesco.ie/groceries/en-IE/products/254317390" },
  { "store": "dunnes", "url": "https://www.dunnesstoresgrocery.com/product/dunnes-stores-swede-100317581" }
]
```

Only product pages on the shop's own host are accepted (the shape basket accepts; for Dunnes that is
`https://www.dunnesstoresgrocery.com/product/<name>-<number>`, not the clothing and home site `dunnesstores.com`);
anything else stops the run before a browser opens, naming the entry. Query strings and fragments are dropped.

## What it does, and does not do

- One page at a time per shop, **at least 5 seconds** between loads, shops one after the other. No retries.
- It is an ordinary browser on your connection and does nothing to look like anything else: no stealth plugin, no
  fingerprint or user-agent changes, no proxy, no CAPTCHA solving. It is driven by Playwright, and a shop may notice and
  refuse; that is accepted (below).
- Each page's price, name and stock are posted as read, with the was price and promo text where the shop's markup gives
  them (Aldi only; see the Tesco and Dunnes sections below); basket dates the reading itself. Every page also posts the product
  photo's address (the JSON-LD `image`, else `og:image`; https only, otherwise left out), never the picture; see
  [docs/decisions.md](../docs/decisions.md) ("The product photo"). Every shop's page also posts its category, the breadcrumb
  as a path ("Chilled Food > Milk"), when the page has one; see "Each store page keeps its category" there. A page it cannot read (shop changed its
  markup, 404) is reported and nothing is posted for it.

## A blocked run

If a shop answers with a 403/429, Akamai's "Access Denied" page, Cloudflare's "Just a moment..." challenge, a CAPTCHA or an interstitial, the reader **stops that shop
for the run** (the pages after it are not asked, the refused one is not retried), still posts what it read before the
refusal, and carries on with the other shop. The summary then says so:

```json
{ "store": "tesco", "planned": 12, "read": 0, "unreadable": [],
  "blocked": { "url": "https://www.tesco.ie/groceries/en-IE/products/254317390", "why": "challenge page \"Access Denied\"", "notAsked": 11 },
  "posted": { "batches": 0, "accepted": 0, "rejected": [] } }
```

Progress goes to stderr, the JSON summary to stdout. Exit code: `0` every page read and accepted, `2` finished but a shop
was blocked or a page was unreadable or refused (`unknown_listing` etc.), `1` could not start, basket could not be reached, or basket refused a batch
(bad token, rate limit).

## Tesco: not yet checked against a live page (paused, see above)

On 2026-11-07 tesco.ie answered every fresh, reader-made Chrome profile with Akamai's "Access Denied" (as it does the VPS),
while a long-used Chrome profile was served a real page. So the Tesco parser follows the page's schema.org markup and is
tested on hand-written fixtures only. It reads the price, name and stock from the JSON-LD offer, the category from the page's
breadcrumb, the photo address (see above), and nothing else from the visible text, so Tesco's was price, Clubcard price and offer dates are not posted
until a live page shows where they can be read safely. The Aldi parser is tested on trimmed copies of live pages.

### The one manual step: open the reader's profile by hand once

Like the Lidl Plus login, this is a one-off step you do yourself, accepted in [docs/decisions.md](../docs/decisions.md)
("Tesco profile decision"). From your own WSL terminal (it needs a display; WSLg gives it one):

```sh
cd pc-reader && npm run profile
```

It opens your installed Chrome as a plain browser (no Playwright, no automation flags) on the reader's own profile
directory, the same `BASKET_READER_PROFILE` the reader uses, at https://www.tesco.ie. Browse tesco.ie as you normally
would, accept the cookie banner, sign in to Clubcard only if you want Clubcard prices, then **close Chrome** (the reader
cannot open the profile while Chrome has it open). Then, with Tesco back in `ENABLED_STORES` (it is paused otherwise and
`--store tesco` would read nothing), retry Tesco once:

```sh
npm start -- --store tesco
```

`--store tesco` (or `aldi`, `dunnes`) runs only that shop's entries; everything else is unchanged. If Tesco still refuses, the reader
stops that shop and records it as in "A blocked run"; it does not try again. Nothing here warms up, imitates or hides the
automation, and the reader never handles your credentials: you type them into Chrome yourself. If Akamai still refuses,
using your personal Chrome with an Allow click is your call, not something the reader does. If a Chrome crash leaves the
profile locked, run `npm run profile` again and close Chrome normally.

## Dunnes: read from the page's server-rendered state

Dunnes' grocery shop, dunnesstoresgrocery.com (a Mi9 Retail storefront), publishes no schema.org Product JSON-LD, so the reader
takes the product from the Redux state the server renders into the page (`window.__PRELOADED_STATE__.product`), and only when its
`sku` is the number at the end of the address. It posts the price, name, stock (`available`), photo address and category, and a
promotion that is not for a loyalty card as promo text with its dates. The was price is not posted: no recorded page shows one.
Dunnes has no card price in basket. The parser is checked on pages recorded from the Wayback Machine (2025-09-07 and 2026-11-04),
not on a live one: Cloudflare challenges Dunnes (HTTP 403) for the reader's profile, and the first run that gets through will say
whether the live page still has this shape. A refusal stops the shop and is reported as in "A blocked run". Nothing is built to get
past it; `npm run profile` and its manual step below are written for Tesco and are not a Dunnes workaround. `npm start -- --store dunnes`
runs only Dunnes.

## The catalog probe

`npm run probe -- --store tesco|dunnes [--out file.json]` reads `/robots.txt`, `/sitemap.xml`, the home page, one category page and one
product page of that store (5 s apart, one at a time, no retries, stopping at the first refusal) in the reader's own profile, and
writes one JSON report: statuses, robots rules, sitemap sizes, the JSON/XHR calls the pages made, a trimmed example product, whether a
barcode appears, paging parameters and totals. It posts nothing and needs no token. Set `BASKET_READER_HEADLESS=1` to keep it windowless.
See `docs/decisions.md` ("Catalog 1/14").

## Develop

```sh
npm run check       # the fleet identity end to end over the configured addresses (needs the real credentials and the tailnet)
npm test            # parsers on saved pages, pacing, blocked handling, work list, ingest client, fleet token and catalog (no network)
npm run typecheck
```

`test/fixtures/` holds the saved pages; each says in its first comment whether it was recorded or hand-written. They carry
no personal data. Parsers (`src/parse/`) take a page's HTML and know nothing about the browser; `src/browser.ts` is the
only file that touches Chrome.

To run it by hand against a local basket: start the dev stack as in the root README, make a `bsk_` token in the dev app's
Settings (or use the fleet identity), and point `BASKET_API_ORIGIN` at `http://localhost:3310`.

## Running it every day

On this PC the reader runs once a day by itself, window-less, whenever the PC is on and Tailscale is up. Nobody has to be at the PC
and no terminal has to be open. Dunnes only comes from the PC (Lidl and Aldi have the server's nightly job), so this is what keeps it
fresh. Tesco stays off (`ENABLED_STORES`).

```text
Windows Task Scheduler  ->  conhost --headless wsl.exe  ->  systemctl --user start basket-pc-reader.service  ->  node src/daily.ts
   (the trigger)             (starts WSL if it is down)       (oneshot unit in WSL, logs to the journal)         (gate, stamp, src/main.ts)
```

Install and remove (inside WSL, in the PC's long-lived clone, after `npm ci` and a filled-in `.env.local`):

```sh
./schedule/install.sh      # writes ~/.config/systemd/user/basket-pc-reader.service and registers the Windows task
./schedule/uninstall.sh    # removes both; keeps the log, the stamp, the Chrome profile and .env.local
```

Re-run `install.sh` after moving the clone or upgrading Node (the unit holds absolute paths). To run it now instead of waiting:
`systemctl --user start basket-pc-reader.service` (or `schtasks.exe /Run /TN "Basket PC reader (daily)"`).

**What triggers it.** A systemd user timer inside WSL only fires while the distro is up, and WSL starts lazily, so the trigger is
the Windows task `Basket PC reader (daily)`: at logon (2 minutes in, so Tailscale, which runs inside WSL, is up) and at 12:00,
repeated every 3 hours until 21:00 (a PC that was off at 12:00 runs it as soon as it is back, `StartWhenAvailable`). `wsl.exe` boots
the distro and its systemd if needed and holds it up until the run ends. Installed from an ordinary PowerShell session the task runs
while the user is logged on; Windows refuses boot triggers and "run whether logged on or not" to a non-admin, so if the PC must
read after a reboot with nobody having logged in, run `install.sh` once from a WSL session started from an **elevated** terminal
and it registers the boot (+5 min) trigger with a password-less S4U logon too (not exercised on this PC: it needs an admin prompt). `windows-task.ps1` is the whole definition.

**At most once a local day, whatever fires.** `src/daily.ts` writes `~/.local/state/basket-pc-reader/last-read` (the local date)
after a run that finished, and a later trigger the same day logs `skipped: already read today` and exits 0. That is both the
catch-up rule (a missed day is read at the next trigger, since yesterday's stamp does not match) and the no-double-run rule (a
second trigger, or the elevated boot trigger next to the logon one, finds today's stamp). It does not store a time, so a run at
23:59 does not stop the 00:01 one: that is a new local day.

**Skipping quietly.** Before opening a browser it runs the same four checks as `npm run check`. If the identity service or basket gives
no answer at all (Tailscale down, the VPS off) it writes one line, `skipped: basket or the identity service is unreachable ...`, exits
0 and does **not** stamp the day, so the next trigger tries again. A check that fails for any other reason (a refused token, a wrong
issuer, basket answering 401, no fleet identity configured; the scheduled run needs the fleet identity, not the `bsk_` token)
is not an outage and would never fix itself: it logs `failed: ...` and exits 1, so the unit shows as failed in
`systemctl --user status basket-pc-reader`.

**A blocked store is a normal outcome.** The reader's own exit `2` (a store blocked, a page unreadable or rejected) counts as a
finished run: exit 0, day stamped, the block named in the line. Dunnes is behind a Cloudflare challenge today and shows up like this:

```text
2026-11-14T10:18:17.195Z read: aldi 4/4 read, 4 accepted; dunnes blocked (HTTP 403; 0 not asked), 0/1 read, 0 accepted (reader exit 2)
```

Only the reader failing outright (exit `1`: it could not start, or basket refused a batch) is `failed:` and leaves the day open.

**Headless.** The scheduled run sets `BASKET_READER_HEADLESS=1`, so no Chrome window ever opens on the PC; Aldi reads the same
headless (checked 2026-11-14: 4 of 4 pages read and accepted). To watch a run, put `Environment=BASKET_READER_HEADLESS=0` (and the
`DISPLAY`/`WAYLAND_DISPLAY`/`XDG_RUNTIME_DIR` of WSLg) in the unit by `systemctl --user edit`. The unit uses the reader's own
Chrome profile (`~/.local/state/basket-pc-reader/profile`), never the everyday one.

**Where to look.** `~/.local/state/basket-pc-reader/daily.log` has one line per attempt; the reader's page-by-page progress is in
`journalctl --user -u basket-pc-reader.service`. `npm run daily` runs the same entry point by hand.
