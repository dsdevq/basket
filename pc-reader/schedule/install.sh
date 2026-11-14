#!/usr/bin/env bash
# Installs the daily PC-reader schedule on this PC (WSL + Windows). Idempotent; run it again after moving the repo.
# Needs: the pc-reader folder inside WSL with `npm ci` done and .env.local filled in, Node 24 on PATH, Windows interop.
# Undo with ./uninstall.sh.
#
# Two overrides, for installing from one checkout while the schedule should run another (e.g. a worktree vs the PC's
# long-lived clone): READER_DIR (the pc-reader folder the unit runs; default: the one this script is in) and
# BASKET_READER_ENV_FILE (baked into the unit when set; default: READER_DIR/.env.local).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
reader_dir="$(cd "${READER_DIR:-$(dirname "$here")}" && pwd -P)"
env_file="${BASKET_READER_ENV_FILE:-$reader_dir/.env.local}"
node_bin="$(command -v node || true)"
unit_dir="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
powershell=/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe

[ -n "$node_bin" ] || { echo "node is not on PATH (Node 24 is needed)" >&2; exit 1; }
[ -f "$env_file" ] || { echo "$env_file is missing: fill it in first (README, \"Run it\")" >&2; exit 1; }
[ -f "$reader_dir/src/daily.ts" ] || echo "warning: $reader_dir/src/daily.ts does not exist yet (is that checkout on a main that has the schedule?); the unit will fail until it does" >&2
[ -d "$reader_dir/node_modules" ] || { echo "run 'npm ci' in $reader_dir first" >&2; exit 1; }
[ -x "$powershell" ] || { echo "Windows interop is not available ($powershell)" >&2; exit 1; }
# WSL_DISTRO_NAME is not set in every kind of session (ssh, services); the distro's own root path names it either way.
distro="${WSL_DISTRO_NAME:-$(wslpath -w / 2>/dev/null | sed -n 's|^\\\\wsl[^\\]*\\\([^\\]*\).*|\1|p')}"
[ -n "$distro" ] || { echo "run this inside WSL (could not tell which distro this is)" >&2; exit 1; }

mkdir -p "$unit_dir"
sed -e "s|@READER_DIR@|$reader_dir|" -e "s|@NODE@|$node_bin|" "$here/basket-pc-reader.service.in" > "$unit_dir/basket-pc-reader.service"
if [ -n "${BASKET_READER_ENV_FILE:-}" ]; then
  sed -i "/^\[Service\]/a Environment=BASKET_READER_ENV_FILE=$BASKET_READER_ENV_FILE" "$unit_dir/basket-pc-reader.service"
fi
systemctl --user daemon-reload

"$powershell" -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$here/windows-task.ps1")" \
  -Mode install -Distro "$distro" -LinuxUser "$(id -un)" | tr -d '\r'

echo "installed. unit: $unit_dir/basket-pc-reader.service (runs $reader_dir)"
echo "run it now:   systemctl --user start basket-pc-reader.service    (or: schtasks.exe /Run /TN 'Basket PC reader (daily)')"
echo "log:          ${XDG_STATE_HOME:-$HOME/.local/state}/basket-pc-reader/daily.log"
