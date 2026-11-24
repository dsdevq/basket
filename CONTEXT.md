# Basket
A grocery app for one household over Lidl, Aldi, Tesco, Dunnes (and Centra offers) in Dublin: one shared shopping list, nightly price readings, loyalty cards. The glossary covers the shopping, product and price domain, not sign-in or deploy.

## Language

**List**:
The one shared shopping list; there are no per-user lists. Each line is a **List item**.

**List item**:
One line of the list (`ListItem`): free text, or a product picked from the **Shelf**, remembering the shop it was picked from.

**Shelf**:
The household's set of known **Products**, shown as tiles where products are picked from a shop and added to the list without typing.

**Product**:
One thing the household buys, named in their own words. Shops' own pages hang off it as **Store listings**.

**Store**:
One of the shops basket reads (`Store` enum: Lidl, Aldi, Tesco, Dunnes, Centra). The UI says "shop".
_Avoid_: shop, chain (see flagged ambiguities)

**Store listing**:
The page of one shop that sells a **Product** (`StoreListing`): where the nightly job reads its price. A person-confirmed listing is never replaced by matching.
_Avoid_: listing page, shop page

**Store product**:
One product in one shop's combined **Catalog** (`StoreProduct`), whether or not anyone buys it, keyed by the shop's own id.

**Catalog**:
The combined store catalog: every product of every shop, filled by **Sweeps** and the PC reader. Search and equivalents read it.

**Sweep**:
A nightly job that lists a shop's whole catalog (the Lidl catalog sweep, the Aldi sweep inside the price pull).

**Price observation**:
One dated price reading for a listing, one per listing per Dublin day (`PriceObservation`), with optional was-price, loyalty price and promo text.

**Price pull**:
The nightly Hangfire job (`PricePullJob`) that reads prices for the products the household buys.

**Promotion**:
A current offer shown in the Promotions view, ranked by unit price, with a "Lowest in 90 days" tag at its 90-day low.

**Match candidate**:
A shop page the matcher proposes as a product's equivalent (`MatchCandidate`), either proposed or rejected, until a person confirms it on the match sheet.

**Equivalent**:
The product in another shop that stands for the same thing; confirmed by a person, never guessed by a model.

**Alias**:
Another name for a product in the household's own words (`ProductAlias`); a typed list line that matches an alias links to the product.

**Trip**:
A shopping trip. The one with no close time is the open trip, not yet in History; ticking an item records it there with its shop and time, and "Done shopping" closes it into **History**.

**History**:
The closed trips, including those filed from Lidl Plus receipts. A past trip can be reloaded onto the list.

**Plan**:
The best shop for each list line, and whether one shop covers the whole list; if none does, the cheapest two-shop split. Unmatched or stale lines are flagged, never guessed.

**Walk order**:
The order a shop's sections are visited in, learned from the order items are checked off across closed trips, falling back to a seeded typical order.

**Scorecard**:
The report of which shop wins most of what the household buys and how much was saved, opened on demand.

**Most bought / Usually bought**:
Habits counted from closed trips: most bought is the number of trips that bought a product; usually bought is at least 2 of the latest 10 closed trips.

**Loyalty card**:
A shop's loyalty card kept once for the household (`LoyaltyCard`), its number encrypted at rest, shown as a barcode at the till.

**Lidl Plus**:
Lidl's loyalty service, connected by pasting a token once; its receipts become closed trips and its coupons can price the Lidl line.

**PC reader**:
The Node program on the owner's PC (`pc-reader/`) that reads shop pages the server cannot reach and posts them to the ingest endpoint.

**Ingest token**:
A write-only credential for the PC reader (`IngestToken`); it can post price readings and read nothing.

**Fleet**:
The org's machine identity (`fleet-agent`), whose scopes (e.g. `basket:catalog-read`) let automation call basket's machine endpoints.

## Relationships
- A **Product** has one **Store listing** per **Store** at most, and many **Aliases** and **Match candidates**.
- A **Store listing** has many **Price observations**, one per Dublin day.
- A **List item** is either free text or linked to a **Product**; ticking it records it in the open **Trip**.
- "Done shopping" closes the open **Trip** into **History**.
- The **Plan** is computed from the **List**, the latest **Price observations** and the confirmed **Equivalents**.
- A **Loyalty card** belongs to one **Store**, one per shop.
- The **PC reader** posts prices with an **Ingest token**; the **Fleet** uses scoped machine endpoints.
- **Most bought** and **Usually bought** are counted from **History**; nothing is stored for them.

## Flagged ambiguities
- **Store** (code: `Store` enum, `Prices/Store.cs:6`, `Data/ListItem.cs:35`) and **shop** (UI and docs, e.g. `Data/ListItem.cs:7`, `docs/decisions.md:308`) are the same concept.
- **Store listing** (`StoreListing`, per-shop page of a product) and **Store product** (`StoreProduct`, catalog row of a shop) are different things and easily confused; `Data/Product.cs:61` links a listing to a catalog row.
- **Shelf** (UI) is the set of `Product` rows; it appears in doc comments, endpoints and the UI, not as a domain class (only private test helpers).
- **Trip** / **History**: the open trip is a Trip but is excluded from History (`Data/Trip.cs:35`).

