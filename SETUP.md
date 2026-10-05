# Conduit 2.4.3 — setup and daily use

## Dashboard interactions

The main window has a dark integrated title bar. Drag its empty area to move the window, double-click to maximize/restore, or right-click for the Windows system menu. The edges resize normally, and the maximize button exposes the Windows Snap target. Close hides the dashboard to the tray; it does not disconnect the tunnel.

Windows connection notifications appear once when a connection or recovery first verifies. Routine checks, including a temporary UDP warning followed by a passing result, update the dashboard without repeating the connection notification. Fault notifications still appear when entering a faulted state.

Use the sidebar to move between **Overview**, **Connections**, **Diagnostics** and **Settings**. The menu button collapses the sidebar to icons; tooltips and accessible names retain each page name. The connection state and main Connect/Disconnect control remain above every page.

Select a destination row on Connections to open the side inspector. It shows the current address, protocol, observed activity and transferred bytes. Close it or press Escape to dismiss it; destinations that disappear from the next live sample are deselected. **Diagnostics → Show engine and adapter details** expands the technical cards. Incident reports, history and latency tests are within Diagnostics; profile import, visual load, animation, network lock and update controls are within Settings. Smaller windows scroll page content while keeping the connection controls visible.

Double-click a destination row (or press Enter on it) to inspect its local record. The globe and destination location lookups have been removed. No location service receives the destinations in this list.

Hover a chart to inspect a sample, or focus it and use Left/Right. Missing measurements remain gaps. Short metric highlights, button feedback and tool-page transitions honor Windows animation settings. The **Animations** switch also pauses effects in Connection tools opened from the dashboard. **Visual load → Low power** suppresses decorative motion and reduces display sampling; battery power also suppresses motion. Hidden/minimized dashboards stop display sampling unless Compact view is visible. Protection checks and recovery continue in the host. Animation never delays reported connection state.

## Install on a Windows x64 PC

1. Get `Conduit-Owner-Setup.exe` (your administration PC) or `Conduit-Client-Setup.exe` (another device) from the private GitHub release.
2. In the old app, choose Disconnect and exit. Stop v2rayN or any other tunnel too.
3. Open Setup, choose Install, and accept Windows' administrator prompt. These builds have signed update bundles but no Windows publisher certificate, so an unknown-publisher warning is possible.
4. Open Conduit from the Start menu normally. Import your personal device link, or use **Import from v2rayN**. Both installers start without an enrolled identity.
5. Upgrading from 1.x: choose Import previous profile and select `configs/profile.json` in the old app folder. Older files must include their own REALITY public key and short ID. If those fields are missing, import a complete connection link. Keep your old files until the new version works.
6. Click Connect and approve the tunnel host's UAC prompt. Check internet, DNS and UDP results, then test your actual voice/video app.

The dashboard runs without administrator privileges. Its separate tunnel host needs elevation. The current host expects the same Windows account to approve UAC; entering a different administrator's credentials for a standard account is not supported. A device needs its own link, but does not need another VM.

## Import an existing v2rayN server

1. Select the desired server in v2rayN and connect once so it generates `binConfigs/config.json`.
2. In Conduit, choose **Import server → Import from v2rayN**. Choose an automatically found config or browse to that JSON file.
3. If the server's public exit differs from its ingress address, enter the expected exit IPv4. Otherwise leave it blank.
4. Choose **Read and validate**, inspect the protocol/server summary, then **Save imported server**. Failed validation leaves the old profile intact.
5. Exit v2rayN before connecting with Conduit.

Conduit imports one selected Xray server: VLESS, VMess (alterId 0), Trojan or supported Shadowsocks methods; TCP/raw, WebSocket or gRPC; TLS/REALITY where accepted by its bundled Xray. IPv4 and hostname endpoints are supported, with a resolved IPv4 cached at import for reconnecting under a retained network lock. If a hostname's address changes while locked, restore normal internet and re-import it. IPv6-only servers, XHTTP, proxy chains, custom certificates and plugins are unsupported and rejected. Conduit replaces v2rayN routing, DNS and multiplexing with its own configuration; subscriptions and custom routing rules are not imported. Owner server administration and Conduit link export are unavailable for imported servers; use v2rayN to export the original connection link.

## Connection monitoring

Overview shows measured traffic, exit IP and internet-check timing. Live destination records come from Conduit's sing-box, including TCP and UDP connections. The Connections table shows the most active 24 aggregated destinations. Active means observed byte counters increased between samples; an open connection can be idle. No connection or traffic values are generated for display.

Destination records stay local and in memory. The Connections page samples them every two seconds in Balanced mode or five seconds in Low power mode, and stops when inactive or hidden. External destination geolocation is unavailable in this release. Built-in health checks still contact their configured DNS, HTTPS and STUN services.

The connection checks include a tunnel-bound HTTPS probe, ordinary IPv4 HTTPS exit verification, DNS, UDP and sampled IPv4/IPv6 routes. A known route bypass skips the ordinary request. A missing IPv6 route is explicitly acceptable. These checks cannot prove every destination or per-app routing policy. Verification expires after 60 seconds and clears on network changes, engine exit or a failed check. DNS for these probes goes through the tunnel without Windows-resolver fallback. Explanations in Connection tools distinguish observed failures from possible causes.

Automatic recovery first targets a failed proxy or tunnel adapter, then escalates to both. It makes up to three attempts per cycle, limits automatic restarts across cycles, and retries every five minutes after failure. **Reconnect** requests a bounded immediate retry; **Disconnect** cancels all automatic retry intent.

After a network change or wake, Conduit keeps the existing engines running while the uplink is unavailable. Once it returns, the link receives 15 seconds to settle before Conduit checks whether the existing tunnel recovered. Another network change resets that period. If verification still fails, it restarts the tunnel adapter first and allows 20 seconds for routes to settle before checking again. A link whose status cannot be determined still receives timed verification. Reconnect, Disconnect, or turning automatic recovery off interrupts these waits.

## Stop, close, and recover

- **Disconnect:** stop the tunnel and remove Conduit's network lock. Normal routing returns.
- **Close the window:** keep the dashboard in the tray.
- **Close interface (keep tunnel running):** exit the dashboard while the independent host keeps the connection alive. Open Conduit again to reattach.
- **Disconnect and exit:** stop the host and exit the dashboard.
- **Connection tools → Connection → Restore normal internet:** remove Conduit's lock and disconnect, including after a host crash.
- **Start with Windows:** opt-in dashboard startup only; it does not silently connect or elevate. Setup removes the former 1.x elevated startup task.

If the interface cannot recover a leftover network lock, open an administrator PowerShell and run the installed app's recovery command:

```powershell
& "$env:ProgramFiles\WorkTunnel-Client\WorkTunnel.exe" --restore-network
```

Use `WorkTunnel-Owner` instead for the Owner installation. This removes only Conduit's WFP filters; it does not disable Windows Firewall. Disconnect any still-running tunnel to restore its routes.

## Optional network lock

Turn it on while disconnected in **Connection tools → Settings → Block internet if tunnel fails (whole PC)**, then connect. The dashboard's **Use privacy defaults** action also enables the saved lock and automatic recovery while disconnected; the whole-PC block applies on the next connection. The lock is off for new users; your choice is remembered across dashboard restarts. The dashboard distinguishes the saved choice from observed filtering. It reads both required IPv4/IPv6 block filters before reporting the lock active; missing or unreadable filtering is shown as needing attention or unconfirmed. A selected lock attempts repair during verification. Disconnect removes active filters without forgetting your choice.

The lock blocks ordinary physical-interface outbound IPv4 and IPv6. Exceptions are loopback, the configured IPv4 server's TCP port (also its UDP port for imported Shadowsocks), traffic routed over the current tunnel adapter, and DHCPv4 broadcast. Local LAN access is blocked too. The lock remains during reconnects and after host/adapter failure. Persistent filters can remain after reboot until you use Disconnect or recovery. This is not a boot-time filtering guarantee or a boundary against other privileged networking software. The server endpoint exception applies to all applications.

Finish a public Wi-Fi sign-in page before connecting. If switching networks needs a sign-in page, Disconnect first. An unreachable VM or a network without usable internet can still prevent connection.

## Profiles and other devices

New saved profiles are encrypted for the current Windows user with DPAPI. Temporary engine configurations require readable credentials; their directory is restricted to that user and administrators. Old 1.x folders and exported links are not automatically scrubbed.

Use **Connection tools → Settings → Export this device's link** only when you intend to create a credential file. Treat it like a password. Send each person only their own link and the blank Client installer. They do not need access to the private repository.

The Owner app's **Your devices** screen adds/revokes devices. Device administration and shared VM usage need your existing SSH key at `%USERPROFILE%\.ssh\oracle.key`; it is never included in an installer. Server-side device changes may briefly interrupt other connections.

The dashboard is Windows x64 only. On other operating systems, use a client supporting VLESS, REALITY and XTLS Vision from the [Project X client directory](https://xtls.github.io/en/document/install). Import a personal link and enable full-device VPN/TUN mode for UDP/voice. Those clients have their own update and security behavior and are not covered by the Windows tests.

## Dashboard and troubleshooting

Connection tools provides measured connection explanations, local diagnostic export, event history, effective settings, and a 12-probe UDP latency test. HTTPS timing is a request duration, not Discord ping. Failed STUN probes are not proof of Discord packet loss. No telemetry or diagnostic upload occurs automatically.

**Incident reports** opens the reports recorded by the tunnel host, even while the dashboard is closed. A report contains the failure state, the last verified health snapshot when available, up to 100 recent events, sampled route/interface facts, and scrubbed engine warnings. Snapshots carry their own timestamps and may come from an earlier host session. Reports describe evidence, not a proven root cause. Repeated failures are grouped for five minutes; only the newest 20 reports are kept. **Save diagnostics** includes the latest five reports plus current network facts. Credentials, raw configs, interface names, SSIDs and destination history are excluded from automatic reports.

Choose 100%, 125% or 150% text in Settings. Dashboard and compact-window placement are remembered. Use Tab to move between controls and Ctrl+Enter to connect/disconnect. Scrolling keeps larger layouts accessible on a smaller display. The quiet blue/rose theme remains.

The shared usage meter estimates VM interface outbound traffic, combining all users. It is not Oracle's billing meter or a spending cap. Check your cloud account for its actual allowances and charges.

For delay, compare the same UDP test on Ethernet and Wi-Fi with similar load. Avoid saturating uploads during calls. A closer VM can improve a route; more CPU or a different cover name does not itself shorten the route. The tested MTU 1400, DNS-first ordering, TUN loop guard and disabled TCP mux remain.

## Updates, repair and uninstall

Open **Settings → Updates** and choose **Check now**. Conduit supplies the official signed feed for your edition by default. Existing custom or explicitly cleared addresses stay as saved; to restore the official feed, use `https://zykoraa.github.io/conduit/Conduit-Owner-stable.json` or `https://zykoraa.github.io/conduit/Conduit-Client-stable.json`, then choose **Save feed**. A verified newer release enables **Download and review**. The download is streamed and must match its signed size and SHA-256; the signed bundle and engines are checked before installation confirmation. The elevated installer independently verifies the bundle again. Your encrypted profile is preserved. Installation briefly disconnects, updates and relaunches normally; reconnect may need UAC approval.

**Check automatically through a verified tunnel** is opt-in. It checks metadata at most every 12 hours, defers while disconnected/unverified and never automatically downloads or installs. Connected checks and downloads bind to tunnel DNS and transport. A manual check while disconnected uses ordinary internet. Connection changes stop in-progress requests. Feed URLs cannot contain credentials/query tokens, and redirects are rejected. The official feeds and versioned assets are direct HTTPS files on the [download site](https://zykoraa.github.io/conduit/).

The existing manual flow remains: download the matching Owner/Client `.wtupdate` and choose **Install signed update**. Expired metadata, a replay older than a version already verified on this PC, an invalid signature or a wrong-flavor feed fails discovery. A stale feed is a publishing problem; it does not disable the current tunnel.

**Roll back** restores files saved by the previous installer/updater transaction; it does not undo server changes or profile edits. A fresh install has no earlier app to restore. Use the latest Setup for **Repair**. An older installer refuses to downgrade a newer app. Windows Installed apps opens Setup for **Uninstall**. Uninstall keeps your encrypted profile and diagnostic history, and removes Conduit's lock.

The Setup executable is not Authenticode-signed. Obtain the initial installer from the [official download site](https://zykoraa.github.io/conduit/) and compare its SHA-256 with the published checksum. Signed updates establish continuity with the release key embedded in your trusted installation; they do not create Windows publisher reputation.
