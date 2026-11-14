# basket — AGENTS.md

Grocery app over Lidl, Aldi, Tesco and Dunnes in Dublin: one shared shopping list, nightly prices, loyalty cards. A member of the lifekit-hq family: same UI, UX and library as the other apps (`@lifekit-hq/ui`, `core`, `tokens` from [lifekit-common](https://github.com/lifekit-hq/lifekit-common)); only the domain differs. Sole developer: Denys. **The repo is public: never commit a secret, token or plaintext env file** (deploy secrets are SOPS + age in `docker/.env.sops`; `.env*` is git-ignored).

## Commands

`@lifekit-hq/*` install from GitHub Packages: export `NODE_AUTH_TOKEN` (needs `read:packages`) before `npm ci`.

```bash
docker compose -f docker/docker-compose.dev.yml up -d   # dev Postgres on 127.0.0.1:55432
dotnet run --project backend/src/Basket.Api             # API on http://localhost:3310
( cd frontend && npm ci && npm start )                  # Angular on http://localhost:4200, proxies /api

scripts/test-backend.sh                                 # backend build + test against a throwaway Postgres container
BASKET_TEST_POSTGRES="Host=localhost;Port=55432;Username=basket;Password=basket-dev;Database=postgres" \
  dotnet test backend/Basket.sln                        # or directly, against the dev database

( cd frontend && npm run lint && npm run format:check && npm run build:prod && npm run test:ci )
( cd frontend && npx playwright install chromium && npm run e2e )   # runs on the prod build
( cd pc-reader && npm ci && npm run typecheck && npm test )
```

## CI gates (`.github/workflows/ci.yml`)

All must be green before merge:

- **backend**: restore, Release build, `dotnet test backend/Basket.sln`. Warnings are errors (`TreatWarningsAsErrors` in `backend/Directory.Build.props`).
- **frontend**: `npm run lint` (includes `drift`: source bypassing the design tokens), `format:check`, `build:prod`, `test:ci`, `e2e` (phone-layout, screen-composition, route-scan with design judge + axe, screen baselines).
- **pc-reader**: `npm run typecheck`, `npm test`.
- **image**: builds `docker/Dockerfile`; a push to main publishes `ghcr.io/dsdevq/basket:<sha>`, which the Deploy workflow ships to the VPS.

## Layout

```
backend/src/Basket.Api/   .NET 10 minimal API: Auth, Lists, Cards, Prices (+Ingest, Promotions), Matching, Plan, Scorecard, LidlPlus, Data (EF Core, Postgres)
backend/tests/Basket.Tests  xUnit; shop pages are recorded fixtures, never the network
frontend/                 Angular 22 on lifekit-common, phone-first with bottom sheets
pc-reader/                Node program on the owner's PC: reads Aldi and Dunnes pages, posts them to the ingest endpoint
docker/                   one image (API serves the Angular build), prod compose project `basket`, dev database, SOPS deploy scripts
docs/                     decisions.md (the why of each rule), vps-setup.md (runbook)
```

## Rules

- **Design is the library's.** A finding in a library component, token or page template is fixed in lifekit-common, never excepted here. Run `npm run lint` (drift check) on any UI change.
- **Screen baselines.** Every routed page is listed in `frontend/e2e/support/screens.ts` and compared at 390 and 1280 wide, light and dark. The Linux images in `frontend/e2e/screens.spec.ts-snapshots` are rewritten only by the **Update screen baselines** workflow (manual dispatch) on the branch that changes a screen; review the images in its commit. A screen with no image fails CI. Never generate them locally: a local mismatch of a few anti-aliased pixels is settled by the CI result.
- **Migrations.** EF Core migrations in `backend/src/Basket.Api/Data/Migrations` are additive and run on app start, so a deploy migrates. Name them `<yyyyMMddHHmmss>_<Name>` and order after the latest one on main. Never `docker compose down -v` (it deletes the database).
- **Decisions.** `docs/decisions.md` records the rule behind each behaviour (matching, plan, promotions, auth, ingest); read it before changing one.
- **Tests never hit a shop.** Parsers are tested on recorded pages in `backend/tests/Basket.Tests/Fixtures`.

## Delivery

- Conventional commits (`feat(list): …`, `fix(pc-reader): …`); release-please builds `CHANGELOG.md` and the weekly release, so never edit `CHANGELOG.md` by hand.
- All changes land via a PR to `main`, CI green first; squash merge.
- Root markdown is `README.md`, `AGENTS.md`, `CLAUDE.md` and the release-please `CHANGELOG.md`; durable docs go in `docs/`.

## Further reading

- `README.md` — what is built and how to run it
- `docs/decisions.md` — decisions and rules, by milestone
- `docs/vps-setup.md` — secrets, first start, Tailscale Funnel, owner sign-up
- `pc-reader/README.md` — the PC reader
