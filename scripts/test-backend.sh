#!/bin/sh
# Backend build + test against a throwaway Postgres.
#   - starts postgres:17-alpine on a free loopback port as container `basket-test-pg-<pid>`, removes it on exit;
#   - runs `dotnet` directly when it is on PATH, otherwise inside the SDK image (2 GB cap, 2-slot flock).
# Nothing else on the host is touched.
set -eu
cd "$(dirname "$0")/.."

name="basket-test-pg-$$"
cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; }
trap cleanup EXIT INT TERM

port=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')
docker run -d --rm --name "$name" --memory 256m -p "127.0.0.1:$port:5432" \
  -e POSTGRES_USER=basket -e POSTGRES_PASSWORD=basket-test -e POSTGRES_DB=postgres \
  postgres:17-alpine >/dev/null
i=0
until docker exec "$name" pg_isready -U basket >/dev/null 2>&1; do
  i=$((i + 1)); [ "$i" -gt 40 ] && { echo "postgres did not start" >&2; exit 1; }
  sleep 1
done

BASKET_TEST_POSTGRES="Host=127.0.0.1;Port=$port;Username=basket;Password=basket-test;Database=postgres"
export BASKET_TEST_POSTGRES

dn='set -e
dotnet restore backend/Basket.sln
dotnet build backend/Basket.sln --configuration Release --no-restore
dotnet build-server shutdown
dotnet test backend/Basket.sln --configuration Release --no-build --verbosity normal'

if command -v dotnet >/dev/null 2>&1; then
  sh -c "$dn"
else
  img=mcr.microsoft.com/dotnet/sdk:10.0
  docker run --rm -u 0 -v nm-nuget-cache:/nuget "$img" chown "$(id -u):$(id -g)" /nuget
  sh scripts/nm-heavy-slot.sh docker run --rm --memory 2g --network host -u "$(id -u):$(id -g)" \
    -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e DOTNET_NOLOGO=1 -e DOTNET_gcServer=0 \
    -e NUGET_PACKAGES=/nuget -e BASKET_TEST_POSTGRES \
    -v nm-nuget-cache:/nuget -v "$PWD":/src -w /src "$img" sh -c "$dn"
fi
