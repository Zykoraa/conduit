#!/usr/bin/env bash
# Cancel a pending auto-revert armed by `set-cover.sh <name> --revert <sec>`.
# Run after the new cover is confirmed working, so it sticks.
set -euo pipefail
MARKER="/tmp/cover-revert-pending"
PROFILES="/usr/local/etc/xray/cover-profiles.json"
[ "$(id -u)" -ne 0 ] && exec sudo "$0" "$@"
if [ -f "$MARKER" ]; then
  rm -f "$MARKER"
  echo "confirmed: auto-revert cancelled; active cover stays '$(jq -r '.active' "$PROFILES")'."
else
  echo "nothing pending to confirm."
fi
