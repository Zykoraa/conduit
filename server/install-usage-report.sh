#!/usr/bin/env bash
# Installs a daily Discord data-usage report (systemd timer). Run: sudo bash install-usage-report.sh
set -euo pipefail

cat > /usr/local/bin/usage-report.sh <<'US'
#!/usr/bin/env bash
# Post this month's VM egress vs the 10 TB cap to Discord.
set -uo pipefail
LIMIT_GB=10240

# webhook: prefer a dedicated usage channel, else reuse the alert webhook
WEBHOOK=""
for f in /usr/local/etc/xray/usage-webhook /usr/local/etc/xray/alert-webhook; do
  [ -f "$f" ] && { WEBHOOK="$(head -n1 "$f" | tr -d '[:space:]')"; break; }
done
[ -n "$WEBHOOK" ] || { echo "no webhook configured"; exit 0; }

J="$(vnstat --json m 2>/dev/null)"
TX="$(printf '%s' "$J" | jq -r '.interfaces[0].traffic.month[-1].tx // empty' 2>/dev/null)"
RX="$(printf '%s' "$J" | jq -r '.interfaces[0].traffic.month[-1].rx // empty' 2>/dev/null)"

if [ -z "${TX:-}" ] || [ "$TX" = "null" ]; then
  curl -s -m 10 -H 'Content-Type: application/json' \
    -d "$(jq -nc '{content:"WorkTunnel usage: vnstat is still collecting data - check again later."}')" \
    "$WEBHOOK" >/dev/null
  exit 0
fi

TXG="$(awk "BEGIN{printf \"%.2f\", $TX/1073741824}")"
RXG="$(awk "BEGIN{printf \"%.2f\", $RX/1073741824}")"
PCT="$(awk "BEGIN{printf \"%.2f\", ($TX/1073741824)/$LIMIT_GB*100}")"
COLOR="$(awk "BEGIN{p=($TX/1073741824)/$LIMIT_GB*100; if(p>=90)print 15158332; else if(p>=70)print 16776960; else print 3066993}")"
BAR="$(awk "BEGIN{n=int(($TX/1073741824)/$LIMIT_GB*20); if(n>20)n=20; s=\"\"; for(i=0;i<n;i++)s=s\"#\"; for(i=n;i<20;i++)s=s\"-\"; print s}")"
OUT="$(printf '%s\n[%s]' "$TXG GB / $LIMIT_GB GB  ($PCT%)" "$BAR")"

PAYLOAD="$(jq -nc \
  --argjson color "$COLOR" \
  --arg out "$OUT" \
  --arg inc "$RXG GB (does not count)" \
  --arg foot "shared VM egress · resets monthly · $(hostname)" \
  '{embeds:[{title:"WorkTunnel — data usage this month",color:$color,
     fields:[{name:"Outbound (counts toward 10 TB)",value:$out},
             {name:"Incoming (free)",value:$inc,inline:true}],
     footer:{text:$foot}}]}')"

curl -s -m 10 -H 'Content-Type: application/json' -d "$PAYLOAD" "$WEBHOOK" >/dev/null && echo "usage posted"
US
chmod 755 /usr/local/bin/usage-report.sh

cat > /etc/systemd/system/usage-report.service <<'SVC'
[Unit]
Description=WorkTunnel daily data-usage report to Discord
After=network-online.target
[Service]
Type=oneshot
ExecStart=/usr/local/bin/usage-report.sh
SVC

cat > /etc/systemd/system/usage-report.timer <<'TMR'
[Unit]
Description=Post WorkTunnel data-usage report daily
[Timer]
OnCalendar=*-*-* 13:00:00
Persistent=true
[Install]
WantedBy=timers.target
TMR

systemctl daemon-reload
systemctl enable --now usage-report.timer

echo "=== installed ==="
echo "timer: $(systemctl is-active usage-report.timer)"
systemctl list-timers usage-report.timer --no-pager 2>/dev/null | sed -n '2p'
echo "--- posting one now as a test ---"
/usr/local/bin/usage-report.sh
