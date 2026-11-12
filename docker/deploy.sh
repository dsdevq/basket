#!/usr/bin/env bash
# Deploy basket on the VPS: pull the image CI published for one commit and (re)start the stack. Nothing is built here.
#
# Runs on the box, in a checkout, normally from the deploy workflow's self-hosted runner after it logged in to ghcr.
# Needs docker + docker compose, curl, and:
#   - IMAGE_TAG: the full SHA of the commit whose image to deploy (ghcr.io/dsdevq/basket:<sha>). Re-running with an
#     older SHA is a rollback.
#   - the secrets: docker/.env.sops, decrypted into the git-ignored docker/.env with the age key at SOPS_AGE_KEY_FILE
#     (default ~/.config/sops/age/firstmate.agekey, a recipient beside the captain in .sops.yaml). Needs sops.
#     TEMPORARY: until docker/.env.sops is committed, the old env file ($BASKET_ENV_FILE, default ~/.config/basket/env)
#     is used instead and the script says so. The follow-up item bsk-fam-b6b has the operator produce the encrypted
#     docker/.env.sops, makes it required and deletes this fallback (docs/vps-setup.md).
# Gated by the platform contract (lifekit-stack scripts/platform-contract.py, on the box at /srv/lifekit-stack):
# the declarations are checked before anything changes, the running containers after.
# Idempotent: re-running redeploys the same image.
set -euo pipefail
cd "$(dirname "$0")/.."

IMAGE_TAG="${IMAGE_TAG:-}"
if ! [[ $IMAGE_TAG =~ ^[0-9a-f]{40}$ ]]; then
  echo "error: IMAGE_TAG must be the full commit SHA whose published image to deploy (got '$IMAGE_TAG')" >&2
  exit 1
fi
export IMAGE_TAG

if [[ -f docker/.env.sops ]]; then
  KEYFILE="${SOPS_AGE_KEY_FILE:-$HOME/.config/sops/age/firstmate.agekey}"
  if [[ ! -f $KEYFILE ]]; then
    echo "error: no age key at $KEYFILE - cannot decrypt docker/.env.sops (set SOPS_AGE_KEY_FILE to another recipient's key)" >&2
    exit 1
  fi
  if ! command -v sops >/dev/null 2>&1; then
    echo "error: sops missing on PATH. Install from https://github.com/getsops/sops/releases" >&2
    exit 1
  fi
  echo "[deploy] decrypt docker/.env.sops"
  ( umask 077; SOPS_AGE_KEY_FILE="$KEYFILE" sops --decrypt --input-type dotenv --output-type dotenv docker/.env.sops > docker/.env )
  ENV_FILE=docker/.env
else
  ENV_FILE="${BASKET_ENV_FILE:-$HOME/.config/basket/env}"
  if [[ ! -f $ENV_FILE ]]; then
    echo "error: no docker/.env.sops and no env file at $ENV_FILE (docs/vps-setup.md)" >&2
    exit 1
  fi
  echo "[deploy] warn: docker/.env.sops is not committed yet - using $ENV_FILE. This fallback is temporary: bsk-fam-b6b makes the encrypted file required and deletes it" >&2
fi

compose=(docker compose -p basket -f docker/docker-compose.prod.yml --env-file "$ENV_FILE")

# Platform contract (guardrail 1). No existence check and no `|| true` on purpose: a missing checker fails the deploy
# rather than silently skipping the gate. No waivers.
CONTRACT="${PLATFORM_CONTRACT:-/srv/lifekit-stack/scripts/platform-contract.py}"

# Static gate: every service's lifekit.contract.* declaration, before anything changes.
echo "[deploy] platform contract: declarations"
"${compose[@]}" config --format json | python3 "$CONTRACT" --static -

# Fail before anything changes when the commit has no published image (CI failed or never ran for it, or it was pruned).
echo "[deploy] pull basket @ $IMAGE_TAG"
if ! "${compose[@]}" pull app; then
  echo "error: image ghcr.io/dsdevq/basket:$IMAGE_TAG could not be pulled - was it published for this commit?" >&2
  exit 1
fi

echo "[deploy] start basket (no build)"
"${compose[@]}" up -d --no-build --remove-orphans

echo "[deploy] prune dangling images"
docker image prune -f >/dev/null

echo "[deploy] wait for the app to report healthy"
for _ in $(seq 1 40); do
  state="$(docker inspect --format '{{.State.Health.Status}}' basket-app 2>/dev/null || true)"
  if [[ $state == healthy ]]; then
    echo "[deploy] basket-app healthy"
    curl -fsS http://127.0.0.1:3310/api/health/ready >/dev/null && echo "[deploy] ready (database reachable)"
    break
  fi
  sleep 3
done
if [[ $state != healthy ]]; then
  echo "error: basket-app did not become healthy" >&2
  "${compose[@]}" logs --tail 60 app >&2 || true
  exit 1
fi

# Runtime gate: probe the running containers (health, ready, metrics, scraped, logs). Bounded retry: the `scraped`
# item reads Prometheus' LAST scrape of the target, and a container recreated seconds ago is still `down` there until
# its next 15s scrape. Four attempts a scrape interval apart. The containers stay up either way; the job goes red.
echo "[deploy] platform contract: running containers (project basket)"
attempts=4
for ((attempt = 1; attempt <= attempts; attempt++)); do
  if python3 "$CONTRACT" --project basket; then
    echo "[deploy] platform contract met"
    exit 0
  fi
  if ((attempt < attempts)); then
    echo "[deploy] platform contract: attempt $attempt failed - retrying in 15s"
    sleep 15
  fi
done
echo "error: platform contract failed after $attempts attempts (table above)" >&2
exit 1
