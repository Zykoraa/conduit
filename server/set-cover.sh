#!/usr/bin/env bash
# Switch the REALITY cover site FLEET-WIDE. Rewrites only dest + serverNames on
# the reality inbound; private key, shortIds and every user UUID are untouched.
#
# Writes the new config IN PLACE (cat >), never `mv` from /tmp -- a mv would give
# config.json the temp file's owner/mode/SELinux label and xray (User=nobody)
# could no longer read it. Validates with `xray -test` (needs a .json temp so xray
# detects the format), self-heals by restoring the backup if xray won't start, and
# can arm a detached auto-revert for switching over the tunnel itself.
#
# Usage:  sudo ./set-cover.sh <name> [--revert <sec>]
#         sudo ./set-cover.sh            # list profiles + show active/pending
set -euo pipefail

CFG="/usr/local/etc/xray/config.json"
PROFILES="/usr/local/etc/xray/cover-profiles.json"
MARKER="/tmp/cover-revert-pending"
XRAY="$(command -v xray || echo /usr/local/bin/xray)"

[ "$(id -u)" -ne 0 ] && exec sudo "$0" "$@"
command -v jq >/dev/null || { echo "jq missing: sudo dnf install -y jq"; exit 1; }
[ -f "$PROFILES" ] || { echo "missing $PROFILES"; exit 1; }

NAME="${1:-}"
REVERT_SEC=0
[ "${2:-}" = "--revert" ] && REVERT_SEC="${3:-90}"

if [ -z "$NAME" ]; then
  echo "profiles:"; jq -r '.profiles | keys[]' "$PROFILES" | sed 's/^/  /'
  echo "active: $(jq -r '.active' "$PROFILES")"
  [ -f "$MARKER" ] && echo "PENDING auto-revert to: $(cat "$MARKER")"
  exit 0
fi

DEST="$(jq -er --arg n "$NAME" '.profiles[$n].dest' "$PROFILES")" \
  || { echo "no such profile: $NAME"; exit 1; }
SNIS="$(jq -c --arg n "$NAME" '.profiles[$n].serverNames' "$PROFILES")"
PREV="$(jq -r '.active' "$PROFILES")"

# in-place write helper: keeps the destination file's owner/mode/SELinux context
write_inplace() { cat "$1" > "$2"; restorecon "$2" 2>/dev/null || true; }

TS="$(date +%Y%m%d-%H%M%S)"
cp -a "$CFG" "$CFG.bak.$TS"

TMP="$(mktemp --suffix=.json)"   # xray infers config format from the extension
jq --arg dest "$DEST" --argjson snis "$SNIS" \
  '(.inbounds[] | select(.streamSettings.security=="reality") | .streamSettings.realitySettings)
     |= (.dest=$dest | .serverNames=$snis)' "$CFG" > "$TMP"

if ! "$XRAY" -test -c "$TMP" >/dev/null 2>&1; then
  echo "generated config failed 'xray -test' - NOT applied. backup: $CFG.bak.$TS"
  rm -f "$TMP"; exit 1
fi
write_inplace "$TMP" "$CFG"; rm -f "$TMP"

PTMP="$(mktemp)"
jq --arg n "$NAME" '.active=$n' "$PROFILES" > "$PTMP"; write_inplace "$PTMP" "$PROFILES"; rm -f "$PTMP"

# Arm auto-revert BEFORE the restart (the restart drops the tunnel/SSH; the
# detached setsid job survives SIGHUP and reverts unless cancelled).
if [ "$REVERT_SEC" -gt 0 ] && [ "$PREV" != "$NAME" ]; then
  echo "$PREV" > "$MARKER"
  setsid bash -c "sleep $REVERT_SEC; [ -f $MARKER ] && { T=\$(cat $MARKER); rm -f $MARKER; $0 \$T; }" \
    >/tmp/cover-revert.log 2>&1 < /dev/null &
  echo "auto-revert to '$PREV' armed in ${REVERT_SEC}s (confirm-cover.sh cancels)"
fi

systemctl restart xray
sleep 1
if ! systemctl is-active --quiet xray; then
  echo "xray did NOT start on '$NAME' - self-healing: restoring $CFG.bak.$TS"
  rm -f "$MARKER"
  write_inplace "$CFG.bak.$TS" "$CFG"
  PTMP2="$(mktemp)"; jq --arg n "$PREV" '.active=$n' "$PROFILES" > "$PTMP2"; write_inplace "$PTMP2" "$PROFILES"; rm -f "$PTMP2"
  systemctl restart xray
  echo "restored to '$PREV'. xray=$(systemctl is-active xray)"
  exit 1
fi
echo "cover -> '$NAME'  (dest=$DEST  serverNames=$SNIS). xray active on :443."
