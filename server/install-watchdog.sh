#!/usr/bin/env bash
# Installs a self-healing health watchdog for xray + hardens the service.
# Run on the VM as: sudo bash install-watchdog.sh
set -euo pipefail

# ---------------------------------------------------------------- watchdog
cat > /usr/local/bin/tunnel-watchdog.sh <<'WD'
#!/usr/bin/env bash
# Every minute: verify xray is up + listening on :443; if not, self-heal.
set -uo pipefail
CFG=/usr/local/etc/xray/config.json
WEBHOOK_FILE=/usr/local/etc/xray/alert-webhook
STATE=/run/tunnel-watchdog.state
XRAY="$(command -v xray || echo /usr/local/bin/xray)"

listening() { ss -tln 2>/dev/null | grep -q ':443 '; }

alert() {
  [ -f "$WEBHOOK_FILE" ] || return 0
  local url; url="$(head -n1 "$WEBHOOK_FILE" | tr -d '[:space:]')"
  [ -n "$url" ] || return 0
  curl -fsS -m 10 -H 'Content-Type: application/json' \
    -d "$(jq -nc --arg c "$1" '{content:$c}')" "$url" >/dev/null 2>&1 || true
}

if systemctl is-active --quiet xray && listening; then
  [ "$(cat "$STATE" 2>/dev/null)" = "down" ] && \
    alert "RECOVERED: WorkTunnel xray is back and listening on :443 ($(hostname))."
  echo up > "$STATE"; exit 0
fi

# unhealthy -> self-heal
echo down > "$STATE"
NOTE="down at $(date -u +%FT%TZ)."

# 1) fix the known failure mode: config perms / SELinux label
chown root:root "$CFG" 2>/dev/null || true
chmod 644 "$CFG" 2>/dev/null || true
restorecon "$CFG" 2>/dev/null || true

# 2) if the config won't even parse, restore the newest good backup
if ! "$XRAY" -test -c "$CFG" >/dev/null 2>&1; then
  BK="$(ls -1t "$CFG".bak.* 2>/dev/null | head -1)"
  [ -n "$BK" ] && { cat "$BK" > "$CFG"; restorecon "$CFG" 2>/dev/null || true; NOTE="$NOTE restored $(basename "$BK")."; }
fi

# 3) restart
systemctl reset-failed xray 2>/dev/null || true
systemctl restart xray 2>/dev/null || true
sleep 3

if systemctl is-active --quiet xray && listening; then
  alert "AUTO-HEALED: WorkTunnel xray was down, restarted, now on :443 ($(hostname)). $NOTE"
else
  alert "ALERT: WorkTunnel xray is DOWN and auto-heal FAILED ($(hostname)). $NOTE Manual attention needed."
fi
WD
chmod 755 /usr/local/bin/tunnel-watchdog.sh

# ---------------------------------------------------------------- systemd units
cat > /etc/systemd/system/tunnel-watchdog.service <<'SVC'
[Unit]
Description=WorkTunnel xray health watchdog
After=network-online.target
[Service]
Type=oneshot
ExecStart=/usr/local/bin/tunnel-watchdog.sh
SVC

cat > /etc/systemd/system/tunnel-watchdog.timer <<'TMR'
[Unit]
Description=Run WorkTunnel watchdog every minute
[Timer]
OnBootSec=60
OnUnitActiveSec=60
AccuracySec=10s
[Install]
WantedBy=timers.target
TMR

# ---------------------------------------------------------------- harden xray
mkdir -p /etc/systemd/system/xray.service.d
cat > /etc/systemd/system/xray.service.d/override.conf <<'OVR'
[Unit]
StartLimitIntervalSec=0
[Service]
Restart=on-failure
RestartSec=3
OVR

systemctl daemon-reload
systemctl enable --now tunnel-watchdog.timer
systemctl restart xray

echo "=== installed ==="
echo "timer: $(systemctl is-active tunnel-watchdog.timer)"
systemctl list-timers tunnel-watchdog.timer --no-pager 2>/dev/null | sed -n '2p'
echo "alerts: $([ -f /usr/local/etc/xray/alert-webhook ] && echo 'ON (webhook set)' || echo 'OFF (no webhook yet)')"
echo "--- run watchdog once now ---"; /usr/local/bin/tunnel-watchdog.sh; echo "watchdog exit=$?"
echo "xray=$(systemctl is-active xray); listening:"; ss -tln 2>/dev/null | grep ':443 ' || echo NONE
