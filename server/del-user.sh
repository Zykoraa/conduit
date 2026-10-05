#!/usr/bin/env bash
# Revoke a VLESS/REALITY client by label OR UUID (a coworker leaves, key lost...).
#
#   sudo del-user.sh <label-or-uuid>            remove, restart xray
#   sudo del-user.sh --dry-run <label-or-uuid>  validate only: no write, no restart
#
# Same safety as add-user.sh: in-place write, xray -test gate, count check,
# backup/restore self-heal.
set -uo pipefail

CFG=/usr/local/etc/xray/config.json
XRAY="$(command -v xray || echo /usr/local/bin/xray)"

DRY=0
if [ "${1:-}" = "--dry-run" ]; then DRY=1; shift; fi
KEY="${1:-}"
[ -n "$KEY" ] || { echo "usage: del-user.sh [--dry-run] <label-or-uuid>" >&2; exit 1; }

[ -f "$CFG" ] || { echo "ERROR: $CFG not found" >&2; exit 1; }
command -v jq >/dev/null || { echo "ERROR: jq not installed" >&2; exit 1; }

BEFORE="$(jq '[.inbounds[]?.settings.clients[]?]|length' "$CFG")"

TMP="$(mktemp --suffix=.json)"
jq --arg k "$KEY" '
  (.inbounds[] | select(.protocol=="vless" and (.streamSettings.security=="reality")).settings.clients)
    |= map(select(.email != $k and .id != $k))
' "$CFG" > "$TMP" || { echo "ERROR: jq edit failed" >&2; rm -f "$TMP"; exit 1; }

AFTER="$(jq '[.inbounds[]?.settings.clients[]?]|length' "$TMP")"
if [ "$AFTER" = "$BEFORE" ]; then
  echo "ERROR: no client matched '$KEY' (nothing removed)" >&2
  rm -f "$TMP"; exit 1
fi

if ! "$XRAY" -test -c "$TMP" >/dev/null 2>&1; then
  echo "ERROR: new config failed 'xray -test'; not applying" >&2
  rm -f "$TMP"; exit 1
fi

if [ "$DRY" = "1" ]; then
  rm -f "$TMP"
  echo "DRY-RUN OK: would remove $((BEFORE-AFTER)) client(s) matching '$KEY' (clients $BEFORE -> $AFTER). Nothing changed."
  exit 0
fi

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
  echo "OK: removed $((BEFORE-AFTER)) client(s) matching '$KEY' (clients now $AFTER). Backup: $(basename "$BK")"
  exit 0
fi

echo "ERROR: xray did not come back; restoring backup" >&2
cat "$BK" > "$CFG"; restorecon "$CFG" 2>/dev/null || true
systemctl restart xray
exit 1
