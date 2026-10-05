#!/usr/bin/env bash
# Show the active cover profile and what the live xray config actually serves.
set -euo pipefail
CFG="/usr/local/etc/xray/config.json"
PROFILES="/usr/local/etc/xray/cover-profiles.json"
R='(.inbounds[]|select(.streamSettings.security=="reality").streamSettings.realitySettings)'
echo "active profile:   $(jq -r '.active' "$PROFILES" 2>/dev/null || echo '?')"
echo "live dest:        $(jq -r "$R.dest" "$CFG")"
echo "live serverNames: $(jq -c "$R.serverNames" "$CFG")"
