# pc-reader

Reads Tesco, Aldi and Dunnes product pages in an ordinary browser on **your own PC and home connection** and posts the
prices to basket's write-only ingest endpoint (`POST /api/ingest/prices`). tesco.ie, aldi.ie and dunnesstoresgrocery.com
refuse the VPS, so this is how those three shops get prices (see [docs/decisions.md](../docs/decisions.md), "M2 decisions",
"Price ingest decisions" and "Dunnes: the server cannot read it, the PC reader does").
A product whose shop has not been read yet shows "Waiting for the next PC read" in basket.

It is a small Node program: Chrome (installed on the PC, with a profile of its own) loads each page, a parser reads what
the page says, and the readings go to basket in batches. Nothing is scheduled; you run it when the PC is on.

## Run it

Needs Node 24 and Google Chrome (or set `BASKET_READER_CHANNEL=msedge`).

```sh
cd pc-reader
npm ci
cp .env.example .env.local        # fill in the origin and the token
cp urls.example.json urls.local.json   # list the pages to read
npm start
```

Make the token in basket under **Settings > Price reader** (owner only). It is shown once. Both files you filled in are
ignored by git.

| Setting | Where | Meaning |
|---|---|---|
| `BASKET_API_ORIGIN` | env or `.env.local` | Where basket runs, e.g. `https://basket.example.com`. `http` only for `localhost`. |
| `BASKET_INGEST_TOKEN` | env or `.env.local` | The `bsk_...` token. Never logged. The environment wins over the file. |
| `BASKET_READER_URLS` | env or `.env.local` | The URL file. Default `pc-reader/urls.local.json`. |
| `BASKET_READER_PROFILE` | env or `.env.local` | Chrome profile directory. Default `~/.local/state/basket-pc-reader/profile`: a profile of the reader's own, never your everyday one. `npm run profile` opens the same directory by hand (below). |
| `BASKET_READER_CHANNEL` | env or `.env.local` | Installed browser to drive. Default `chrome`. |
| `BASKET_READER_HEADLESS` | env or `.env.local` | `1` for a window-less run (no display). Default is a normal visible window. |
| `BASKET_READER_ENV_FILE` | env | Read settings from this file instead of `.env.local`. |

## The URL file

The ingest endpoint deliberately never tells a token holder which pages are tracked, so you keep the list yourself: a JSON
array of `{ "store", "url" }`, `store` being `aldi`, `dunnes` or `tesco`, `url` the product page you linked to the product in basket
(the same address; a page that is not linked comes back `unknown_listing`).

```json
[
  { "store": "aldi", "url": "https://www.aldi.ie/product/clonbawn-protein-milk-000000000000422541" },
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
  them (Aldi only; see the Tesco and Dunnes sections below); basket dates the reading itself. A page it cannot read (shop changed its
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

## Tesco: not yet checked against a live page

On 2026-11-07 tesco.ie answered every fresh, reader-made Chrome profile with Akamai's "Access Denied" (as it does the VPS),
while a long-used Chrome profile was served a real page. So the Tesco parser follows the page's schema.org markup and is
tested on hand-written fixtures only. It reads the price, name and stock from the JSON-LD offer and nothing from the visible
text, so Tesco's was price, Clubcard price and offer dates are not posted until a live page shows where they can be read
safely. The Aldi parser is tested on trimmed copies of live pages.

### The one manual step: open the reader's profile by hand once

Like the Lidl Plus login, this is a one-off step you do yourself, accepted in [docs/decisions.md](../docs/decisions.md)
("Tesco profile decision"). From your own WSL terminal (it needs a display; WSLg gives it one):

```sh
cd pc-reader && npm run profile
```

It opens your installed Chrome as a plain browser (no Playwright, no automation flags) on the reader's own profile
directory, the same `BASKET_READER_PROFILE` the reader uses, at https://www.tesco.ie. Browse tesco.ie as you normally
would, accept the cookie banner, sign in to Clubcard only if you want Clubcard prices, then **close Chrome** (the reader
cannot open the profile while Chrome has it open). Then retry Tesco once:

```sh
npm start -- --store tesco
```

`--store tesco` (or `aldi`, `dunnes`) runs only that shop's entries; everything else is unchanged. If Tesco still refuses, the reader
stops that shop and records it as in "A blocked run"; it does not try again. Nothing here warms up, imitates or hides the
automation, and the reader never handles your credentials: you type them into Chrome yourself. If Akamai still refuses,
using your personal Chrome with an Allow click is your call, not something the reader does. If a Chrome crash leaves the
profile locked, run `npm run profile` again and close Chrome normally.

## Dunnes: not yet checked against a live page

Dunnes' grocery shop, dunnesstoresgrocery.com, answers the VPS and a plain request from the author's PC alike with
Cloudflare's "Just a moment..." challenge (2026-11-08, HTTP 403, `cf-mitigated: challenge`), so no real Dunnes product page
has been read. The parser follows the page's schema.org JSON-LD offer, as Tesco's does, and is tested on hand-written
fixtures only (plus the recorded challenge page, which must stop the shop). It posts the price, name and stock; the was
price and promo text are not posted until a live page shows where they can be read safely. Dunnes has no card price in
basket. Whether Cloudflare serves the reader's Chrome profile is not known: the first run stops on a refusal and reports
it as in "A blocked run". Nothing is built to get past it; `npm run profile` and its manual step below are written for
Tesco and are not a Dunnes workaround. `npm start -- --store dunnes` runs only Dunnes.

## Develop

```sh
npm test            # parsers on saved pages, pacing, blocked handling, work list, ingest client (no network)
npm run typecheck
```

`test/fixtures/` holds the saved pages; each says in its first comment whether it was recorded or hand-written. They carry
no personal data. Parsers (`src/parse/`) take a page's HTML and know nothing about the browser; `src/browser.ts` is the
only file that touches Chrome.

To run it by hand against a local basket: start the dev stack as in the root README, make a token in the dev app's
Settings, and point `BASKET_API_ORIGIN` at `http://localhost:3310`.

## Running it on a schedule (not set up)

Not installed on purpose. On this PC a systemd user timer (`~/.config/systemd/user/basket-pc-reader.timer` plus a oneshot
`.service` running `node src/main.ts` in this folder with `BASKET_READER_HEADLESS=1`, or a session with a display, and
`Persistent=true` so a missed run happens when the PC wakes) would do it.
