#!/usr/bin/env bash
# Vet a candidate cover domain BEFORE adding it to cover-profiles.json.
# Run on the VM (checks reachability + TLS 1.3 + h2 + valid cert + X25519).
# A pass here is necessary but NOT sufficient - www.microsoft.com passed TLS 1.3
# yet FAILED the live REALITY handshake, so always confirm live with set-cover.
#
# Usage: ./vet-cover.sh www.example.com
set -euo pipefail
D="${1:?usage: vet-cover.sh <domain>}"

echo "Vetting $D:443 as a REALITY cover ..."
OUT="$(echo | timeout 10 openssl s_client -connect "$D:443" -servername "$D" -tls1_3 -alpn h2 2>/dev/null || true)"
[ -z "$OUT" ] && { echo "  [FAIL] no TLS 1.3 connection (unreachable or no TLS 1.3)"; exit 1; }

echo "$OUT" | grep -q "New, TLSv1.3" && echo "  [ok]   TLS 1.3"            || echo "  [FAIL] no TLS 1.3"
echo "$OUT" | grep -q "ALPN protocol: h2" && echo "  [ok]   HTTP/2 (h2)"   || echo "  [warn] no h2 ALPN"
echo "$OUT" | grep -q "Verify return code: 0 (ok)" && echo "  [ok]   valid cert chain" || echo "  [warn] cert not verified"
KEY="$(echo "$OUT" | grep -i "Server Temp Key" || true)"
if echo "$KEY" | grep -qi "X25519"; then echo "  [ok]   $KEY"; else echo "  [warn] X25519 not confirmed ($KEY)"; fi

echo
echo "If TLS 1.3 + h2 + valid cert are [ok], add to cover-profiles.json:"
echo "    \"<name>\": { \"dest\": \"$D:443\", \"serverNames\": [\"$D\"], \"sni\": \"$D\" }"
echo "then: sudo ./set-cover.sh <name>   and verify a client still exits the VPS."
