#!/usr/bin/env bash
# Deploy basket on the VPS: pull the image CI published for one commit and (re)start the stack. Nothing is built here.
#
# Runs on the box, in a checkout, normally from the deploy workflow's self-hosted runner after it logged in to ghcr.
# Needs docker + docker compose, curl, and:
#   - IMAGE_TAG: the full SHA of the commit whose image to deploy (ghcr.io/dsdevq/basket:<sha>). Re-running with an
#     older SHA is a rollback.
#   - the secrets: docker/.env.sops, decrypted into the git-ignored docker/.env with the age key at SOPS_AGE_KEY_FILE
#     (default ~/.config/sops/age/firstmate.agekey, a recipient beside the captain in .sops.yaml). Needs sops.
#     Until docker/.env.sops is committed, the old env file ($BASKET_ENV_FILE, default ~/.config/basket/env) is used
#     instead and the script says so; delete that branch with the first deploy from the encrypted file (docs/vps-setup.md).
# Idempotent: re-running redeploys the same image.
#
# Break-glass (ghcr or CI unavailable): the compose file keeps its `build:` block, so the image can be built from the
# checkout on the host instead of pulled:
#
#   export NODE_AUTH_TOKEN=$(gh auth token)   # read:packages, for the build's @lifekit-hq/* install
#   IMAGE_TAG=local docker compose -p basket -f docker/docker-compose.prod.yml --env-file docker/.env up -d --build
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
  echo "[deploy] warn: docker/.env.sops is not committed yet - using $ENV_FILE" >&2
fi

compose=(docker compose -p basket -f docker/docker-compose.prod.yml --env-file "$ENV_FILE")

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
    exit 0
  fi
  sleep 3
done
echo "error: basket-app did not become healthy" >&2
"${compose[@]}" logs --tail 60 app >&2 || true
exit 1
