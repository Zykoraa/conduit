#!/usr/bin/env bash
# Add a VLESS/REALITY client (new UUID) to the running xray config, safely.
#
#   sudo add-user.sh [label]              add a client, restart xray, print UUID
#   sudo add-user.sh --dry-run [label]    validate only: no write, no restart
#
# On success prints a line "UUID=<uuid>" that the packaging wrapper parses.
# Keeps config perms + SELinux label (writes IN PLACE, never `mv` from /tmp),
# gates on `xray -test`, verifies the client count actually grew, and restores
# the backup if xray doesn't come back.
set -uo pipefail

CFG=/usr/local/etc/xray/config.json
XRAY="$(command -v xray || echo /usr/local/bin/xray)"
FLOW="xtls-rprx-vision"

DRY=0
if [ "${1:-}" = "--dry-run" ]; then DRY=1; shift; fi
LABEL="${1:-user-$(date -u +%Y%m%d-%H%M%S)}"

[ -f "$CFG" ] || { echo "ERROR: $CFG not found" >&2; exit 1; }
command -v jq >/dev/null || { echo "ERROR: jq not installed" >&2; exit 1; }

# reject a label that already exists
DUP="$(jq -r --arg e "$LABEL" '[.inbounds[]?.settings.clients[]?|select(.email==$e)]|length' "$CFG" 2>/dev/null || echo 0)"
if [ "${DUP:-0}" != "0" ]; then echo "ERROR: a client labelled '$LABEL' already exists" >&2; exit 1; fi

# fresh UUID
UUID="$("$XRAY" uuid 2>/dev/null || cat /proc/sys/kernel/random/uuid)"
[ -n "$UUID" ] || { echo "ERROR: could not generate a UUID" >&2; exit 1; }

BEFORE="$(jq '[.inbounds[]?.settings.clients[]?]|length' "$CFG")"

# add the client to the VLESS + REALITY inbound only
TMP="$(mktemp --suffix=.json)"
jq --arg id "$UUID" --arg flow "$FLOW" --arg email "$LABEL" '
  (.inbounds[] | select(.protocol=="vless" and (.streamSettings.security=="reality")).settings.clients)
    += [ { id: $id, flow: $flow, email: $email } ]
' "$CFG" > "$TMP" || { echo "ERROR: jq edit failed" >&2; rm -f "$TMP"; exit 1; }

AFTER="$(jq '[.inbounds[]?.settings.clients[]?]|length' "$TMP")"
if [ "$AFTER" != "$((BEFORE+1))" ]; then
  echo "ERROR: client count did not grow ($BEFORE -> $AFTER) - REALITY inbound not matched; not applying" >&2
  rm -f "$TMP"; exit 1
fi

if ! "$XRAY" -test -c "$TMP" >/dev/null 2>&1; then
  echo "ERROR: new config failed 'xray -test'; not applying" >&2
  rm -f "$TMP"; exit 1
fi

if [ "$DRY" = "1" ]; then
  rm -f "$TMP"
  echo "DRY-RUN OK: would add '$LABEL' (clients $BEFORE -> $AFTER), xray -test passed. Nothing changed."
  echo "UUID=$UUID"
  exit 0
fi

# apply: backup, write IN PLACE (keep perms/label), fix perms, restorecon
BK="$CFG.bak.$(date -u +%Y%m%d%H%M%S)"
cat "$CFG" > "$BK"
cat "$TMP" > "$CFG"; rm -f "$TMP"
chown root:root "$CFG" 2>/dev/null || true
chmod 644 "$CFG" 2>/dev/null || true
restorecon "$CFG" 2>/dev/null || true

systemctl reset-failed xray 2>/dev/null || true
systemctl restart xray
sleep 2

if systemctl is-active --quiet xray && ss -tln 2>/dev/null | grep -q ':443 '; then
  echo "OK: added client '$LABEL' (clients now $AFTER). Backup: $(basename "$BK")"
  echo "UUID=$UUID"
  exit 0
fi

echo "ERROR: xray did not come back; restoring backup" >&2
cat "$BK" > "$CFG"; restorecon "$CFG" 2>/dev/null || true
systemctl restart xray
exit 1
