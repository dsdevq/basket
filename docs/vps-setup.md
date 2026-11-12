# VPS setup (runbook)

How basket runs on the lifekit VPS and what is left to do for the first start. Nothing here is done by the code
change itself: the Funnel needs root, the secrets file is created and encrypted on the box side, the first deploy runs there.

## Shape

| Piece | Where |
|---|---|
| Compose project | `basket` (`docker compose -p basket ...`), file `docker/docker-compose.prod.yml` |
| App | container `basket-app`, **256 MB**, listens on `127.0.0.1:3310` only |
| Database | container `basket-db` (Postgres 17), **192 MB**, no published port, volume `basket_pgdata` |
| Data Protection keys | volume `basket_keys`, mounted at `/keys` (keeps the sign-in cookie valid across deploys) |
| Image | `ghcr.io/dsdevq/basket:<full commit SHA>`, built once per `main` commit by CI (`ci.yml`, job `image builds`, arm64) and pulled by the deploy; nothing is built on the box |
| Secrets | `docker/.env.sops` in the repo, encrypted with SOPS + age; `docker/deploy.sh` decrypts it into the git-ignored `docker/.env` |
| Public address | `https://lifekit-vps.tail1cb676.ts.net:10000` (Tailscale Funnel, port **10000**) |

Port 3310 was free on the box when this was written (checked with `ss -ltn`); re-check before the first start.
Funnel supports only 443, 8443 and 10000. 443 belongs to xui and must not change, and a `/basket` path on that mapping
is not acceptable, so basket uses 10000.

## First start

### 1. Create the encrypted secrets file

`docker/.env.sops` holds the values `docker/.env.example` documents. It is encrypted to the age recipients in `.sops.yaml`
(the captain's key and firstmate's, the same two as finance-sentry), so either decrypts it and it is safe in git. It
needs `sops` and `age` installed; never use the captain's private key on the box, the deploy uses firstmate's
(`~/.config/sops/age/firstmate.agekey`, or `SOPS_AGE_KEY_FILE`).

Create it once from the existing plaintext env file, then commit it:

```sh
sops encrypt --input-type dotenv --output-type dotenv --filename-override docker/.env.sops \
  ~/.config/basket/env > docker/.env.sops
```

For a fresh install write the plaintext first (`umask 077`): `PUBLIC_ORIGIN`, `POSTGRES_PASSWORD=$(openssl rand -hex 24)`
and `BOOTSTRAP_TOKEN=$(openssl rand -base64 33 | tr '+/' '-_' | tr -d '=')`, then encrypt it as above and delete the
plaintext. `BOOTSTRAP_TOKEN` is the owner's one-time sign-up secret (at least 32 characters). Keep it out of chat logs
and tickets: whoever has it while no user exists can register as owner.

Change a value with `docker/secrets-edit.sh` (decrypts into `$EDITOR`, re-encrypts on save, leaves no plaintext).
Until `docker/.env.sops` is committed, `docker/deploy.sh` falls back to `~/.config/basket/env` and says so; once the
encrypted file has deployed once, delete that fallback branch from `deploy.sh` and the old env file.

To accept the fleet's machine identity, also add `FLEET_AGENT_AUTHORITY` and `FLEET_AGENT_BACKCHANNEL_AUTHORITY` (not
secrets; `docker/.env.example` and `docs/decisions.md` "Fleet identity" say what each is). The app joins the external
Docker network `identity-oidc`, which lifekit-stack creates; check that `docker network inspect identity-oidc` works
before the deploy. Both empty keeps the path off.

### 2. Start the stack

```sh
echo "$GHCR_TOKEN" | docker login ghcr.io -u <user> --password-stdin   # read:packages; the workflow does this itself
IMAGE_TAG=<full commit SHA> docker/deploy.sh   # pulls the image, starts basket-app + basket-db, waits for "healthy"
```

The normal route is the **Deploy to VPS** workflow (`.github/workflows/deploy.yml`) on a self-hosted runner: it starts
when CI succeeds on `main`, after the `image builds` job published that commit's image, and deploys that SHA. To roll
back, run it by hand (`workflow_dispatch`) with an older full SHA in `sha`, or run the script with that `IMAGE_TAG`.
If ghcr or CI is down, `docker/deploy.sh` documents the break-glass build from the checkout.

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
`curl`, `sops` and firstmate's age key. The workflow logs in to ghcr with `GITHUB_TOKEN`, so the `ghcr.io/dsdevq/basket`
package must let this repo's Actions read it (package settings > "Manage Actions access"; CI creates the package on its
first push). The image build in CI also reads the `@lifekit-hq` packages with `GITHUB_TOKEN`.

## Operating it

| Task | Command |
|---|---|
| Logs | `docker compose -p basket -f docker/docker-compose.prod.yml logs -f app` |
| Redeploy a commit | `IMAGE_TAG=<full SHA> docker/deploy.sh` |
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
- **No passkey recovery in M1.** A person with a second passkey removes the lost one under Settings > Passkeys. If a
  phone is lost and it held the only passkey (not synced), the owner makes a new invite for that person. If the owner loses theirs with no synced passkey, the last resort is a reset that also clears the list:
  `docker exec -it basket-db psql -U basket basket -c 'TRUNCATE users, list_item, invite CASCADE;'`, then restart the app
  (a fresh owner invite opens from `BOOTSTRAP_TOKEN`) and register again. Take a `pg_dump` first.
- **A join link that does nothing.** Every request log line carries `ua=`, `ch-ua=` and `lang=` (the browser's
  User-Agent, client hint and language). Static files in the log but no `/api/session` from that `ua=` means the app
  never started in that browser; the page then shows "Something went wrong" with an error name and a Reload button
  (after 15 s if the script never ran), or "Cannot reach basket" with Try again when the API could not be reached.
  The invite token is in the URL fragment and is never logged.
- **A passkey that will not create on the join page.** When the browser refuses, the page posts a fixed-label report
  to `POST /api/auth/passkey-failure` and the app logs one `Join passkey failed:` warning (the `ua=` request line sits
  beside it). Its `kind=` says what happened: `no-api`, `insecure-context`, `not-supported`, `security` and `blocked`
  (the browser refused at once, typical of an in-app browser such as Telegram's) all show "Open this link in your
  browser" with a Copy link button; `cancelled` is a dismissal or a slower refusal and shows the try-again notice with
  the same open-in-Safari-or-Chrome hint and Copy link button. `elapsedBucket=` (`instant` or `later`) and
  `credentialNull=` tell a real dismissal from a slow in-app refusal. The usual fix is to send the link again and ask
  her to open it in Safari or Chrome.
- **Memory.** The app container is capped at 256 MB and Postgres at 192 MB; the box has little headroom, so do not
  raise them without checking `free -m` first.
