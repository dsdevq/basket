# VPS setup (runbook)

How basket runs on the lifekit VPS and what is left to do for the first start. Nothing here is done by the code
change itself: the Funnel needs root, the env file holds secrets, the first deploy runs on the box.

## Shape

| Piece | Where |
|---|---|
| Compose project | `basket` (`docker compose -p basket ...`), file `docker/docker-compose.prod.yml` |
| App | container `basket-app`, **256 MB**, listens on `127.0.0.1:3310` only |
| Database | container `basket-db` (Postgres 17), **192 MB**, no published port, volume `basket_pgdata` |
| Data Protection keys | volume `basket_keys`, mounted at `/keys` (keeps the sign-in cookie valid across deploys) |
| Secrets | `~/.config/basket/env` on the box, mode 600, outside any checkout and outside git |
| Public address | `https://lifekit-vps.tail1cb676.ts.net:10000` (Tailscale Funnel, port **10000**) |

Port 3310 was free on the box when this was written (checked with `ss -ltn`); re-check before the first start.
Funnel supports only 443, 8443 and 10000. 443 belongs to xui and must not change, and a `/basket` path on that mapping
is not acceptable, so basket uses 10000.

## First start

### 1. Create the env file (as the deploy user)

```sh
install -d -m 700 ~/.config/basket
umask 077
cat > ~/.config/basket/env <<EOF
PUBLIC_ORIGIN=https://lifekit-vps.tail1cb676.ts.net:10000
POSTGRES_PASSWORD=$(openssl rand -hex 24)
BOOTSTRAP_TOKEN=$(openssl rand -base64 33 | tr '+/' '-_' | tr -d '=')
EOF
```

`docker/.env.example` documents each value. `BOOTSTRAP_TOKEN` is the owner's one-time sign-up secret (at least 32
characters). Keep it out of chat logs and tickets: whoever has it while no user exists can register as owner.
Never put this file in git; do not use the captain's private age key for it.

### 2. Start the stack

```sh
export NODE_AUTH_TOKEN=...      # a token with read:packages (the image build installs @lifekit-hq/*)
docker/deploy.sh                # builds the image, starts basket-app + basket-db, waits for "healthy"
```

The normal route is the **Deploy to VPS** workflow (`.github/workflows/deploy.yml`) on a self-hosted runner; it runs the
same script after CI passes on `main`. Running the script by hand is the same thing.

Check: `curl -s http://127.0.0.1:3310/api/health/ready` returns `Healthy`, and
`docker stats --no-stream basket-app basket-db` stays under 256 MB / 192 MB.

### 3. Turn the Funnel on (needs root - one command)

```sh
sudo tailscale funnel --bg --https=10000 http://127.0.0.1:3310
```

SHA-256 of that exact line (no trailing newline), so the command that runs can be matched to the one reviewed:

```
bbe81e1c396927c6a8bc6b7a7abb34ae54136bce0482dd0efd87bd247e3657f8
```

It adds a mapping on port 10000 only; the 443 mapping is untouched. Verify with `tailscale funnel status` (the 443
entry is unchanged and a new `:10000` entry proxies to `http://127.0.0.1:3310`). To undo:
`sudo tailscale funnel --https=10000 off`. Do not run `tailscale funnel reset` - it clears every mapping, 443 included.

### 4. Register the owner

Open `https://lifekit-vps.tail1cb676.ts.net:10000/join#<BOOTSTRAP_TOKEN>` on the owner's phone, enter a name, and
create the passkey. The link works once and for 24 hours after the app started; if it lapsed, restart the app
(`docker compose -p basket restart app`) to open a fresh 24 hours - it only does so while no user exists.

Then, in the app: **More > People > Invite someone** makes her a one-time link (24 hours). Send it over a private
channel. After the owner exists, `BOOTSTRAP_TOKEN` does nothing; remove it from the env file or rotate it.

## Self-hosted runner

The deploy workflow runs on `runs-on: [self-hosted, lifekit-vps, basket]`. **No runner is registered for this repo
yet** - until one is, the deploy job queues and nothing deploys (CI itself runs on GitHub-hosted runners and is
unaffected). Registering one is a box-side action for the captain/firstmate (same pattern as the finance-sentry runner:
labels `lifekit-vps` and `basket`, the deploy user, Docker access). The runner needs `docker`, `docker compose` and
`curl`, and `~/.config/basket/env` for its user. The workflow passes `GITHUB_TOKEN` as `NODE_AUTH_TOKEN`; the repo must
be allowed to read the `@lifekit-hq` packages (package settings > "Manage Actions access").

## Operating it

| Task | Command |
|---|---|
| Logs | `docker compose -p basket -f docker/docker-compose.prod.yml logs -f app` |
| Redeploy this checkout | `docker/deploy.sh` |
| Restart | `docker compose -p basket restart app` |
| Back up the database | `docker exec basket-db pg_dump -U basket basket > basket-$(date +%F).sql` |
| Stop (keeps data) | `docker compose -p basket -f docker/docker-compose.prod.yml down` |

Migrations run on app start (`OwnerBootstrap`), so a deploy migrates the database. Never `down -v`: it deletes the
database volume.

## Things to know

- **Public surface.** Anyone on the internet can reach the sign-in page. The defences: passkey-only accounts, invite
  links that work once for 24 hours, rate limits on sign-in and invite routes, identical answers for every kind of bad
  invite, an `Origin` allow-list on writes, strict cookie and security headers (see `docs/decisions.md`).
- **Cookie scope.** Browsers share cookies between ports of one host. Other services on
  `lifekit-vps.tail1cb676.ts.net` receive basket's cookie; the `__Host-` prefix and the unique name keep them from
  setting or overwriting it. A custom domain later removes the caveat.
- **No passkey recovery in M1.** If a phone is lost and the passkey is not synced, the owner makes a new invite
  for that person. If the owner loses theirs with no synced passkey, the last resort is a reset that also clears the list:
  `docker exec -it basket-db psql -U basket basket -c 'TRUNCATE users, list_item, invite CASCADE;'`, then restart the app
  (a fresh owner invite opens from `BOOTSTRAP_TOKEN`) and register again. Take a `pg_dump` first.
- **A join link that does nothing.** Every request log line carries `ua=`, `ch-ua=` and `lang=` (the browser's
  User-Agent, client hint and language). Static files in the log but no `/api/session` from that `ua=` means the app
  never started in that browser; the page then shows "Something went wrong" with an error name and a Reload button
  (after 15 s if the script never ran), or "Cannot reach basket" with Try again when the API could not be reached.
  The invite token is in the URL fragment and is never logged.
- **Memory.** The app container is capped at 256 MB and Postgres at 192 MB; the box has little headroom, so do not
  raise them without checking `free -m` first.
