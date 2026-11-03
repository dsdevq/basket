#!/usr/bin/env bash
# Deploy basket on the VPS: build the image from this checkout and (re)start the stack.
#
# Runs on the box, in a checkout, normally from the deploy workflow's self-hosted runner. Needs:
#   - docker + docker compose
#   - the env file with the secrets, outside any checkout: $BASKET_ENV_FILE, default ~/.config/basket/env
#     (docker/.env is the fallback; see docker/.env.example and docs/vps-setup.md)
#   - NODE_AUTH_TOKEN in the environment: a token with read:packages, for the build's @lifekit-hq/* install
# Idempotent: re-running redeploys the checked-out commit.
set -euo pipefail
cd "$(dirname "$0")/.."

ENV_FILE="${BASKET_ENV_FILE:-$HOME/.config/basket/env}"
[[ -f $ENV_FILE ]] || ENV_FILE=docker/.env
if [[ ! -f $ENV_FILE ]]; then
  echo "error: no env file - create ~/.config/basket/env from docker/.env.example (docs/vps-setup.md)" >&2
  exit 1
fi
if [[ -z "${NODE_AUTH_TOKEN:-}" ]]; then
  echo "error: NODE_AUTH_TOKEN (read:packages) is needed to build the frontend image" >&2
  exit 1
fi

IMAGE_TAG="${IMAGE_TAG:-$(git rev-parse --short=12 HEAD)}"
export IMAGE_TAG

compose=(docker compose -p basket -f docker/docker-compose.prod.yml --env-file "$ENV_FILE")

echo "[deploy] build + start basket @ $IMAGE_TAG"
"${compose[@]}" up -d --build --remove-orphans

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
