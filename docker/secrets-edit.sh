#!/usr/bin/env bash
# Edit docker/.env.sops IN PLACE: sops decrypts into $EDITOR and re-encrypts on save, so no plaintext file is written.
# docker/deploy.sh decrypts the same file on the box at deploy time. Needs sops and the captain's age key
# (default ~/.config/sops/age/keys.txt, a recipient in .sops.yaml); SOPS_AGE_KEY_FILE overrides it.
set -euo pipefail
cd "$(dirname "$0")/.."

command -v sops >/dev/null 2>&1 || { echo "error: sops not on PATH. Install from https://github.com/getsops/sops/releases" >&2; exit 1; }
KEYFILE="${SOPS_AGE_KEY_FILE:-$HOME/.config/sops/age/keys.txt}"
[[ -f $KEYFILE ]] || { echo "error: no age key at $KEYFILE - cannot decrypt for editing" >&2; exit 1; }
[[ -f docker/.env.sops ]] || { echo "error: docker/.env.sops does not exist (docs/vps-setup.md says how to create it)" >&2; exit 1; }

# The name has no recognised extension, so pin the type.
SOPS_AGE_KEY_FILE="$KEYFILE" exec sops --input-type dotenv --output-type dotenv docker/.env.sops
