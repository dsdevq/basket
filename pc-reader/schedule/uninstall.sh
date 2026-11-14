#!/usr/bin/env bash
# Removes the daily PC-reader schedule: the Windows task and the systemd unit. Leaves the log, the once-a-day stamp, the
# reader's Chrome profile and .env.local alone.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
unit="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/basket-pc-reader.service"
powershell=/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe

"$powershell" -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$here/windows-task.ps1")" -Mode uninstall | tr -d '\r'
rm -f "$unit"
systemctl --user daemon-reload
echo "uninstalled."
