# basket

Where to buy what I buy: grocery app over Lidl, Aldi, Tesco and Dunnes in Dublin.

For the two of us first: **one shared shopping list** on the phone, and (M2) **prices** read every night for the products
we buy. Matching, a shop plan and Lidl Plus receipts come in later milestones (see [docs/decisions.md](docs/decisions.md)).

## What is here (M1, M2)

- `backend/` - .NET 10 minimal API: ASP.NET Core Identity with passkeys and one-time invite links, the shared list,
  EF Core + Postgres, Hangfire, Serilog. Tests in `backend/tests/Basket.Tests` (real WebAuthn ceremonies against a
  software authenticator).
- `backend/src/Basket.Api/Prices` - M2: the nightly Hangfire price pull (Lidl), its parsers and polite fetcher, the
  product and price endpoints, and `Prices/Ingest`: the token-signed write-only endpoint the owner's PC posts Tesco, Aldi and Dunnes prices to. Tests use recorded shop pages in `backend/tests/Basket.Tests/Fixtures`, never the network.
- `pc-reader/` - the small Node program that reads Tesco, Aldi and Dunnes product pages in a browser on the owner's PC and posts
  them to the ingest endpoint ([README](pc-reader/README.md)).
- `frontend/` - Angular 22 on [lifekit-common](https://github.com/lifekit-hq/lifekit-common) (`@lifekit-hq/ui`,
  `core`, `tokens`), Violet `#7c3aed` seed, phone-first with bottom sheets.
- `docker/` - one image (the API serves the Angular build), the production compose project `basket`, a dev database.
- `docs/` - [decisions](docs/decisions.md) and the [VPS runbook](docs/vps-setup.md).

## Develop

Needs the .NET 10 SDK, Node 22 and Docker. `@lifekit-hq/*` install from GitHub Packages, so export a token with
`read:packages` as `NODE_AUTH_TOKEN` first.

```sh
docker compose -f docker/docker-compose.dev.yml up -d          # Postgres on 127.0.0.1:55432
dotnet run --project backend/src/Basket.Api                    # API on http://localhost:3310
( cd frontend && npm ci && npm start )                         # Angular on http://localhost:4200, proxies /api
```

On first start the API turns the development `Auth:BootstrapToken` (in `appsettings.Development.json`) into an owner
invite; open `http://localhost:4200/join#<that token>` to register. Passkeys work on `localhost`.

## Test

```sh
BASKET_TEST_POSTGRES="Host=localhost;Port=55432;Username=basket;Password=basket-dev;Database=postgres" \
  dotnet test backend/Basket.sln                                # each test class gets a throwaway database
( cd frontend && npm run lint && npm run format:check && npm run test:ci )
( cd frontend && npm run build:prod && npx playwright install chromium && npm run e2e )   # phone-layout, screen-composition (C0-C10), route-scan (design judge + axe) and screen-baseline specs on the prod build
```

`npm run lint` also runs `npm run drift` (`lifekit-chrome-check drift`: source that bypasses the design tokens). The route scan and the
baselines cover every routed page at 390 and 1280 wide, light and dark; the list of pages is `frontend/e2e/support/screens.ts`.
A finding in a library component, token or page template is fixed in lifekit-common, never excepted here. The baselines are Linux
images (`frontend/e2e/screens.spec.ts-snapshots`) that only the **Update screen baselines** workflow rewrites, on the branch that changes a screen. A screen with no image fails, so an empty or missing baseline directory fails CI. The images are drawn on the CI runner (Linux x64): a comparison on other hardware can differ by a few anti-aliased pixels, so a local mismatch is settled by the CI result.

`scripts/test-backend.sh` does the backend run against a throwaway Postgres container and cleans it up.

## Run it for real

[docs/vps-setup.md](docs/vps-setup.md): secrets, first start, the Tailscale Funnel command and the owner sign-up.
