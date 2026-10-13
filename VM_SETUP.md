# Conduit 2.4.4 — Oracle VM and Xray setup guide

This guide covers the Conduit Oracle Linux VM, the Windows Owner app, and the
VLESS/REALITY tunnel between them. Start with the existing-VM section if Xray is
already installed. The fresh-VM instructions include a guard against replacing
an existing Xray configuration.

## 1. Identify the correct VM

The deployment inspected on **October 13, 2026** was:

| Item | Conduit deployment |
| --- | --- |
| Oracle instance | Conduit |
| Region | US Midwest (Chicago), `us-chicago-1` |
| Architecture | ARM64 (`aarch64`); check shape/resources in Oracle Compute |
| Operating system | Oracle Linux 9 |
| Public IPv4 | `147.224.209.249` |
| Private IPv4 | `10.0.0.30` |
| SSH user and port | `opc`, TCP `22` |
| Tunnel entry point | TCP `443` on the Conduit public IPv4 |
| REALITY cover/SNI | `www.apple.com`, destination `www.apple.com:443` |
| Native IPv6 | No global IPv6 address/default route at inspection |
| Root filesystem | 30 GB; about 19 GB free at inspection |

These are deployment details, not installer defaults. Recheck the Oracle
instance's Networking tab if an address is reassigned or the VM is replaced.
The separate **Culvert** instance has public IPv4 `147.224.134.253` and private
IPv4 `10.0.0.176`. Do Conduit administration only against `147.224.209.249`.
Never import Culvert's connection profile, UUID, REALITY keys or short IDs into
Conduit to perform this update. Updating the app preserves its saved profile.

The inspected VCN is `vcn-20260917-0022` and subnet is `subnet-20260917-0022`
(`10.0.0.0/24`). These resources are shared with Culvert. Inspect associations
before changing shared routes or security lists; use a Conduit-specific network
security group if a new rule should apply only to Conduit.

## 2. Understand the connection

```text
Windows applications, including Discord TCP and UDP
    -> Conduit sing-box TUN adapter
    -> local Xray SOCKS listener at 127.0.0.1:10808
    -> VLESS + REALITY + Vision over TCP 443
    -> Xray on the Conduit Oracle VM
    -> the destination website, message gateway or voice server
```

SSH is a separate administration connection. Its private key signs your SSH
login; it is not the VLESS UUID or a REALITY key. Installing a client does not
create its server identity. Each device imports its own complete connection
link or previous profile. Run one full-device tunnel at a time.

## 3. Configure Oracle networking

In Oracle Cloud, open **Compute → Instances → Conduit → Networking** and verify
the instance name and public IPv4 before following the VNIC/subnet links.

1. The primary VNIC must have the intended private IPv4 and a public IPv4.
2. Its subnet must support public access. The effective route table needs an
   IPv4 `0.0.0.0/0` route to an enabled internet gateway in the VCN.
3. A subnet security list or attached network security group must allow the
   traffic below. Retain existing rules needed by the Oracle image.
4. Check the guest's firewalld rules as well. A cloud rule alone does not open
   a guest firewall port, and a guest rule alone does not open the cloud network.

| Direction | Source/destination | Protocol/port | Purpose |
| --- | --- | --- | --- |
| Stateful ingress | Your authorized administrator public IPv4/CIDR | TCP destination 22; source port any | SSH administration |
| Stateful ingress | Intended client public CIDRs, or `0.0.0.0/0` for roaming clients | TCP destination 443; source port any | VLESS/REALITY clients |
| Stateful egress | `0.0.0.0/0` | All protocols | Websites, DNS, destination voice UDP and package downloads |

Stateful rules permit response traffic for an allowed connection. SSH source
restrictions must match the public address from which your SSH connection
actually exits; a tunnel may change that address.

The client-to-VM transport is TCP 443. Discord voice UDP is carried inside that
tunnel and sent outward by the VM. This deployment does not require public
inbound UDP 443 or inbound Discord voice ports on the VM. A destination such
as `discord.media:2087` is an outbound connection from the server.

No public Xray management API or SOCKS listener is required. Keep unrelated
services separate from tunnel ingress rules.

## 4. Prepare SSH on Windows

Use the private key supplied for the Conduit VM. Its `.pub` file is the public
key and cannot authenticate your login by itself. The examples use a convenient
local filename; substitute the real path to your own key.

```powershell
$serverIpv4 = '147.224.209.249'
$keyPath = "$env:USERPROFILE\.ssh\oracle.key"
ssh -i "$keyPath" -o IdentitiesOnly=yes "opc@$serverIpv4"
```

On a first connection, verify the host-key fingerprint against a trusted Oracle
console session before accepting it. The server can display its SSH public-key
fingerprint with `sudo ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`.

If OpenSSH reports that the private-key permissions are too open, restrict that
specific file to your Windows account. These commands do not change a folder:

```powershell
$account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
icacls "$keyPath" /inheritance:r
icacls "$keyPath" /grant:r "${account}:(R)"
icacls "$keyPath" /remove:g '*S-1-1-0' '*S-1-5-11' '*S-1-5-32-545'
```

Retry SSH. If TCP 22 is blocked by the current local network, use an authorized
network that permits SSH or an already working Conduit connection. Restarting
Xray interrupts an SSH session that is itself travelling through Conduit.

## 5. Download the server tools

The public application and distribution repository is `Zykoraa/conduit`. It
does not require a personal access token. The older `Zykoraa/work-tunnel-app`
repository remains private; do not publish its historical profiles.
On the VM:

```bash
sudo dnf install -y curl unzip jq python3 openssl
mkdir -p ~/conduit-release-2.4.4
cd ~/conduit-release-2.4.4
curl -fL https://github.com/Zykoraa/conduit/releases/download/v2.4.4/Conduit-Server-2.4.4.zip -o Conduit-Server-2.4.4.zip
curl -fL https://github.com/Zykoraa/conduit/releases/download/v2.4.4/SHA256SUMS.txt -o SHA256SUMS.txt
sha256sum --check --ignore-missing SHA256SUMS.txt
unzip -q Conduit-Server-2.4.4.zip -d tools
cd tools
```

Require `Conduit-Server-2.4.4.zip: OK` before extraction. The server ZIP has a
published SHA-256 checksum; Windows update bundles and feeds additionally use
Conduit's existing release signature. The archive includes only public server
helpers and documentation, with Unix line endings for executable scripts.

## 6A. Existing Conduit VM: retain the working configuration

The existing Conduit VM already has the required hostname routing. This release
needs **no server migration, new identity, config replacement or Xray restart**.
Inspect it without printing device credentials:

```bash
sudo /usr/local/bin/xray run -test -c /usr/local/etc/xray/config.json
systemctl is-active xray
sudo jq '.inbounds[] | {port, protocol, sniffing}' /usr/local/etc/xray/config.json
```

For its VLESS port 443 inbound, expect `enabled: true` and
`destOverride: ["http", "tls", "quic"]`, with `routeOnly` omitted/default false.
Keep the existing enrolled users, server REALITY key, short IDs, cover and
single `freedom` outbound. This guide does not apply Culvert's bootstrap,
private-destination blocking rules, service user or helper names to Conduit.

Before any later intentional server edit, create a root-only backup:

```bash
sudo install -d -m 700 /var/backups/conduit
sudo sh -c 'umask 077; cp /usr/local/etc/xray/config.json /var/backups/conduit/config-$(date -u +%Y%m%dT%H%M%SZ).json; chmod 600 /var/backups/conduit/config-*.json'
```

Back up your current client profile separately using Conduit's private export
controls. An app update preserves the saved profile; it does not recreate it.

## 6B. Fresh VM only: install a replacement deliberately

Skip this section on `147.224.209.249`. A replacement VM gets a new identity and
its own profile; these commands do not migrate existing users automatically.
First configure Oracle networking and SSH from sections 3–4.

For a new ARM64 Oracle Linux 9 VM, download the pinned upstream core:

```bash
sudo dnf install -y curl unzip jq python3 openssl
mkdir -p ~/conduit-xray-core
cd ~/conduit-xray-core
curl -fL https://github.com/XTLS/Xray-core/releases/download/v26.7.28/Xray-linux-arm64-v8a.zip -o xray.zip
echo 'f5698bb218ada3b4022db26fafc39601c5f53b46b19eb76c9616325985807501  xray.zip' | sha256sum --check
unzip -q xray.zip -d core
sudo install -m 755 core/xray /usr/local/bin/xray
```

These download/hash commands are for `aarch64`, not `x86_64`. Never install an
ARM64 executable on an x86 VM. Confirm `uname -m` first.

1. Generate a new device UUID with `/usr/local/bin/xray uuid` and a new REALITY
   pair with `/usr/local/bin/xray x25519`. Generate a short ID with
   `openssl rand -hex 8`. Store results privately; do not paste them into issues.
2. Use section 7's illustrative JSON, replacing every `__PLACEHOLDER__` locally.
   Put the UUID in `clients[].id`, private key in `privateKey`, and short ID in
   `shortIds`. Keep the public key for the client profile. Never use Culvert's
   values. Save the completed JSON in a protected local file `config.json`.
3. On the new VM, install it with the permissions needed by the service below:

```bash
sudo install -d -m 755 /usr/local/etc/xray
sudo test ! -e /usr/local/etc/xray/config.json && sudo install -o root -g root -m 644 config.json /usr/local/etc/xray/config.json
sudo /usr/local/bin/xray run -test -c /usr/local/etc/xray/config.json
```

The `test` guard refuses to replace an existing config. Stop if it fails.
The existing deployment runs as `nobody`, which needs read access to this file.
For a replacement, a dedicated service group with mode 640 can provide tighter
credential access, but configure that group and service together.

4. On the fresh VM only, create `/etc/systemd/system/xray.service`:

```ini
[Unit]
Description=Conduit Xray server
After=network.target nss-lookup.target
StartLimitIntervalSec=0
[Service]
User=nobody
ExecStart=/usr/local/bin/xray run -config /usr/local/etc/xray/config.json
CapabilityBoundingSet=CAP_NET_ADMIN CAP_NET_BIND_SERVICE
AmbientCapabilities=CAP_NET_ADMIN CAP_NET_BIND_SERVICE
NoNewPrivileges=true
Restart=on-failure
RestartSec=3
RestartPreventExitStatus=23
LimitNOFILE=1000000
[Install]
WantedBy=multi-user.target
```

Then validate, enable the service, and open the guest firewall:

```bash
sudo restorecon -RF /usr/local/bin/xray /usr/local/etc/xray
sudo /usr/local/bin/xray run -test -c /usr/local/etc/xray/config.json
sudo systemctl daemon-reload
sudo systemctl enable --now xray
sudo firewall-cmd --permanent --add-port=443/tcp
sudo firewall-cmd --reload
```

5. From the extracted server package, install management helpers if needed:

```bash
for helper in add-user.sh del-user.sh get-cover.sh vet-cover.sh set-cover.sh confirm-cover.sh; do
  sudo install -m 755 "server/$helper" "/usr/local/bin/$helper"
done
sudo install -m 755 server/worktunnel-devices.py /usr/local/bin/worktunnel-devices
sudo install -m 644 server/cover-profiles.json /usr/local/etc/xray/cover-profiles.json
sudo bash server/install-watchdog.sh
```

The current VM's `/home/opc/usage.sh` is an existing deployment component; the
server package includes the optional daily-report installer, not that usage
script. Do not promise an Owner usage readout on a replacement before setting up
and validating its usage collector. Watchdog/report webhook files are optional
credentials, never release assets.

6. Build a private client profile using the new public IPv4, new device UUID,
   REALITY public key, short ID and cover from section 7. Import it as described
   in section 8 and verify real message/voice traffic. Do not replace the working
   Conduit profile on your main PC until the replacement has been tested.

## 7. Xray settings reference

The release pins **Xray 26.7.28** and **sing-box 1.13.19**. Windows engine hashes
are in `engines.lock.json`; section 6B pins the ARM64 server archive hash. The current VM uses the ARM64 Xray build. Engine upgrades require the
repository's evaluation and release checks, rather than replacing one binary
independently while debugging a connection.

### Server configuration

| Field | Conduit value/meaning |
| --- | --- |
| Config | `/usr/local/etc/xray/config.json` |
| Executable | `/usr/local/bin/xray` |
| Service | `xray.service`, user `nobody` on the existing VM |
| Listener | `0.0.0.0`, port `443` |
| Protocol | `vless` |
| Client authentication | Separate UUID in `settings.clients` for each enrolled device |
| Client flow | `xtls-rprx-vision` |
| VLESS decryption field | `none`; transport security is provided by REALITY |
| Transport | `tcp` in this pinned configuration |
| Security | `reality` |
| REALITY destination | `www.apple.com:443` |
| REALITY server names | `www.apple.com` |
| REALITY private key | Generated for this VM; remains server-side |
| REALITY short IDs | Generated server values; client must use an accepted value |
| Hostname routing | `sniffing.enabled: true`, `destOverride: [http, tls, quic]` |
| `routeOnly` | Omitted, so the default `false` permits destination override |
| Internet outbound | `freedom`, tag `direct` |
| Routing/outbounds | No explicit routing object; one `freedom` outbound |
| Logging | Warning level; inspect service journal for failures |

The following is an illustration, **not a ready-to-run credential file**:

```json
{
  "log": { "loglevel": "warning" },
  "inbounds": [{
    "listen": "0.0.0.0",
    "port": 443,
    "protocol": "vless",
    "sniffing": { "enabled": true, "destOverride": ["http", "tls", "quic"] },
    "settings": {
      "clients": [{ "id": "__DEVICE_UUID__", "flow": "xtls-rprx-vision", "email": "worktunnel-owner" }],
      "decryption": "none"
    },
    "streamSettings": {
      "network": "tcp",
      "security": "reality",
      "realitySettings": {
        "show": false,
        "dest": "www.apple.com:443",
        "xver": 0,
        "serverNames": ["www.apple.com"],
        "privateKey": "__SERVER_REALITY_PRIVATE_KEY__",
        "shortIds": ["__SHORT_ID__"]
      }
    }
  }],
  "outbounds": [{ "protocol": "freedom", "tag": "direct" }]
}
```

The configuration file contains device identities and the server REALITY private
key. The inspected Conduit deployment uses `root:root`, mode `644`, and service
user `nobody`. That is its current state, not a claim of root-only access. Restrict
local VM access and protect backups. Do not change it to Culvert's `root:xray`
mode 640 without also configuring an appropriate service user/group.
No live UUID, private key or short ID is embedded in this guide or server ZIP.

### Keys, identities and recovery

The SSH private key, VLESS UUID and REALITY key pair have different jobs:

- **SSH private key:** authenticates VM administration as `opc`. It stays on the
  administration PC. Client devices do not need it.
- **VLESS UUID:** identifies an enrolled device in Xray's client list. Generating
  a UUID with `xray uuid` alone does not authorize it on the server.
- **REALITY private/public keys:** the private key remains in the VM's protected
  Xray config; the paired public key is included in client profiles. The
  `xray x25519` command generates a new pair for a fresh deployment.
- **Short ID:** the client's value must be accepted by this server's
  `shortIds` list. Generate it for a new deployment with `openssl rand -hex 8`.
- **Complete device link/profile:** combines server, port, UUID, SNI, REALITY
  public key and short ID. Treat the complete link as a device credential.

Use the existing owner profile or a protected backup to recover an existing
installation. A new deployment generates new credentials rather than discovering
the old ones. Changing the server's REALITY key requires updating its clients;
changing one device UUID requires updating that device's profile.

This setup uses a public REALITY cover destination and does not require an ACME
certificate or a domain you own. Keep the VM and PC clocks synchronized. On the
VM, `timedatectl status` and `chronyc tracking` help inspect time synchronization.

### Client configuration

| Field | Conduit value/meaning |
| --- | --- |
| Server | Imported Conduit public IPv4 and TCP port 443 |
| UUID | Must match this device's server-side client entry |
| Flow/encryption | `xtls-rprx-vision`, VLESS `encryption: none` |
| REALITY serverName | Matches the selected server cover, initially `www.apple.com` |
| REALITY publicKey | Paired with this VM's REALITY private key |
| REALITY shortId | An accepted server short ID |
| Fingerprint / spiderX | `chrome` / `/` |
| Local SOCKS | `127.0.0.1:10808`, UDP enabled, local only |
| DNS | Cloudflare DoH over the proxy; `prefer_ipv4` |
| TUN | `worktunnel`; `172.19.0.1/30`, `fdfe:dcba:9876::1/126` |
| TUN MTU/stack | `1400`, `gvisor`; automatic and strict routes enabled |
| Server loop guard | Imported server IPv4/TCP port routed directly to the physical uplink |
| QUIC | UDP destination port 443 rejected by the client policy |
| XUDP | `mux.enabled: true`, `concurrency: -1`, `xudpConcurrency: 16`, `xudpProxyUDP443: reject` |

`concurrency: -1` disables ordinary TCP multiplexing while keeping the configured
XUDP behavior. Preserve this tested pair of settings when updating Conduit.
Changing the client to a system web proxy does not replace the full-device TUN
needed to carry Discord voice traffic.

### Why Conduit's hostname routing matters for Discord

An application can request AAAA records and choose IPv6 even when DNS prefers
IPv4. Conduit's VM has no native IPv6 route, but its existing HTTP/TLS/QUIC
hostname override can recover a visible hostname and use the server's usable
address family. This behavior was the missing setting on the separate Culvert VM.
Conduit already had it; this release does not add a server fix or force IPv4-only DNS.

This does not decrypt HTTPS application content, provision native IPv6, or
guarantee every protocol exposes a usable hostname. Literal IPv6 destinations
and IPv6-only services still need native IPv6 egress. Preserve `prefer_ipv4` and
test both Discord messages and voice after an intentional connection change.

## 8. Import the owner profile and set VM access

Existing Conduit installations keep their current encrypted profile during an
update. Do not import a different profile just to install this release.

For a new PC, export that device's complete Conduit link from the Owner tools,
or import its previous `configs/profile.json` through **Import previous profile**.
The existing VM has no `~/.config/conduit/profile.json` bootstrap output; do not
follow Culvert's SCP profile instructions against this server.

For a deliberately new deployment, a native profile has this shape (substitute
all placeholders privately):

```json
{
  "user": "Conduit owner",
  "server": "__CONDUIT_SERVER_PUBLIC_IPV4__",
  "port": 443,
  "uuid": "__DEVICE_UUID__",
  "sni": "www.apple.com",
  "publicKey": "__SERVER_REALITY_PUBLIC_KEY__",
  "shortId": "__SHORT_ID__",
  "expectedExitIp": "__CONDUIT_SERVER_PUBLIC_IPV4__"
}
```

1. Install/open the **Owner** edition from this release.
2. Disconnect the other full-device tunnel.
3. Keep the existing Conduit profile, or import the intended device's Conduit
   link/previous JSON. Its server must be `147.224.209.249` for this deployment.
4. Owner SSH administration uses user `opc` and the private key at
   `%USERPROFILE%\.ssh\oracle.key`. This key is never included in an installer.
5. Connect and approve the normal elevated tunnel host prompt.
6. Verify exit IPv4 `147.224.209.249`, then test messages and voice in Discord.

The Windows app encrypts the saved profile under
`%LOCALAPPDATA%\WorkTunnel\secrets\profile.dpapi` for the current Windows user.
A downloaded plain JSON profile remains a credential file; remove temporary
copies after successful import and keep any needed recovery copy privately.

**Import from v2rayN** imports its selected/generated server. Confirm the selected
server is Conduit before importing. Imported v2rayN profiles do not enable
Conduit's owner server-administration tools.

## 9. Enroll additional devices

Use the Owner app's device-management controls to add a uniquely labelled device
and export its connection link. Give that private link only to the intended
device. Install Client there, import the link, connect, and test the actual app.
That device needs neither your SSH private key nor a separate VM.

Owner device operations use `/usr/local/bin/worktunnel-devices` through SSH with
`sudo -n`. The default Oracle `opc` account supports passwordless sudo; a custom
administration account must have the required approved sudo access. Device jobs
can continue if the tunnel/SSH connection drops during a server restart; refresh
their status before resubmitting an uncertain operation.

Legacy manual helpers also exist:

```bash
sudo /usr/local/bin/add-user.sh --dry-run laptop-2
sudo /usr/local/bin/add-user.sh laptop-2
sudo /usr/local/bin/del-user.sh --dry-run laptop-2
sudo /usr/local/bin/del-user.sh laptop-2
```

Run only the intended add or revoke action. Adding prints the new UUID; it does
not itself enroll a Windows app or construct every field of a complete link.
Revoking that device removes its server access and interrupts active service.

## 10. Cover settings, usage and watchdog

The active initial REALITY cover is Apple. Server cover information lives in
`/usr/local/etc/xray/cover-profiles.json`. Owner tools use these server helpers:

| Helper | Purpose |
| --- | --- |
| `get-cover.sh` | Inspect active profile and live REALITY destination |
| `vet-cover.sh` | Test a cover candidate before switching |
| `set-cover.sh` | Change the server's destination and accepted SNI names |
| `confirm-cover.sh` | Confirm a working timed cover change |
| `/home/opc/usage.sh` | Owner server usage readout |
| `tunnel-watchdog.timer` | Periodically check/recover Xray service/listener |

A cover change affects the server's devices. Their client SNI must match the
server; use the Owner workflow and test one change at a time. A service/listener
watchdog cannot prove Discord, every destination, or the whole network works.

## 11. Verify and troubleshoot

On the VM, these commands inspect configuration/service state without printing
the REALITY private key or device UUIDs:

```bash
/usr/local/bin/xray version
sudo /usr/local/bin/xray run -test -c /usr/local/etc/xray/config.json
systemctl is-active xray
systemctl is-enabled xray
sudo ss -ltnp 'sport = :443'
sudo journalctl -u xray -n 80 --no-pager
sudo firewall-cmd --list-all
systemctl status tunnel-watchdog.timer --no-pager
sudo jq '.inbounds[] | {port, protocol, sniffing}' /usr/local/etc/xray/config.json
```

Check ordinary IPv4 egress from the VM:

```bash
curl -4 --fail --max-time 10 https://api.ipify.org
```

On Windows while Conduit is connected, verify the observed IPv4 exit:

```powershell
curl.exe -4 --fail --max-time 10 https://api.ipify.org
```

Both should show the Conduit public IPv4 for this deployment. Then test Discord
messages and voice. A TCP connection or an HTTP response from `discord.com`
alone does not prove the desktop message gateway or voice path.

| Symptom | Check next |
| --- | --- |
| SSH timeout | Correct VM/IP, Oracle TCP 22 rule, guest ssh service/firewall and local network restrictions |
| SSH "unprotected private key" | Permissions on that exact key file |
| Permission denied (publickey) | Private key belongs to this VM and SSH user is `opc` |
| TCP 443 unreachable | Public IP, VNIC route/IGW, cloud ingress, guest firewall and Xray listener |
| TCP works but REALITY fails | Device UUID, public/private key pairing, short ID, SNI and client/server clock |
| Websites load but Discord voice/messages fail | Server hostname override, real Discord logs, TUN active, UDP/XUDP policy and one tunnel at a time |
| Exit is Culvert | Wrong imported profile or selected v2rayN server |
| Owner tools fail | Owner edition, native Conduit profile, `opc`, `%USERPROFILE%\.ssh\oracle.key` and `sudo -n` |
| Xray restart fails | Validate config; check journal, root:xray ownership, mode 640 and SELinux context |

Keep SELinux and firewalld enabled. Check the actual service account with
`systemctl cat xray` before changing permissions. On this existing VM it is
`nobody`; preserve readable configuration permissions and SELinux context.
Validate before restarting. A restart briefly interrupts all clients, including
an administration SSH session carried by the tunnel. Root-only manual backups
from section 6A are under `/var/backups/conduit`; legacy helpers may also create
`config.json.bak.*`. Select and validate the intended backup before restoring it.
Never replace an existing identity with new credentials to diagnose routing.

If Windows traffic remains blocked after a tunnel failure, use Conduit's
**Disconnect** or **Restore normal internet** control. Enabling the optional
network lock affects the whole PC. Closing the dashboard to the tray does not
disconnect the tunnel.

## 12. Release files and references

The 2.4.4 release includes Owner/Client Windows installers and signed update
bundles, signed update feeds, a checksum-verified server-helper archive, this
standalone guide, and `SHA256SUMS.txt`. Both Windows bundles also include this
guide. The release contains no enrolled identity or SSH/REALITY private key.
Conduit has no native Linux app package in this release.

Source: the public `Zykoraa/conduit` repository, tag `v2.4.4`.
Configuration references: `configs/xray.template.json`, `configs/singbox-tun.json`,
`engines.lock.json`, and `server/`. The historical private repository and its device profiles are not published.

- [Conduit release 2.4.4](https://github.com/Zykoraa/conduit/releases/tag/v2.4.4)
- [Oracle security rules](https://docs.oracle.com/en-us/iaas/Content/Network/Concepts/securityrules.htm)
- [Oracle security lists](https://docs.oracle.com/en-us/iaas/Content/Network/Concepts/securitylists.htm)
- [Xray inbound hostname detection and destination override](https://xtls.github.io/en/config/inbound.html)
- [Xray transport and REALITY fields](https://xtls.github.io/en/config/transport.html)
- [Xray outbound, multiplexing and XUDP fields](https://xtls.github.io/en/config/outbound.html)
- [sing-box DNS policy](https://sing-box.sagernet.org/configuration/dns/)

The server deployment snapshot was inspected on October 13, 2026. The user
previously confirmed Conduit message and voice operation; this documentation
release does not constitute a new end-to-end Discord test. Download integrity, unit tests and CI do not establish continuous
application availability, Wi-Fi roaming behavior or performance on every PC.
