# Conduit

**Connected on your terms.**

A Windows x64 client for personal VLESS/REALITY tunnels and supported servers imported from v2rayN. Conduit pairs Xray with sing-box, shows current connection evidence, and offers an optional whole-PC network lock.

[Download Conduit](https://zykoraa.github.io/conduit/) · [Releases](https://github.com/Zykoraa/conduit/releases) · [Setup and recovery](SETUP.md) · [Architecture](ARCHITECTURE.md)

## Features

- An unelevated dashboard and separate UAC-approved tunnel host. Closing the interface can leave the tunnel running.
- Fresh exit, DNS, UDP and sampled IPv4/IPv6 route checks. Failed or stale evidence cannot retain a verified indication.
- Optional persistent Windows Filtering Platform network lock, with explicit Disconnect and Restore normal internet controls.
- Targeted engine recovery, bounded restart attempts and patient recovery after network changes.
- Import of supported VLESS, VMess, Trojan and Shadowsocks servers over TCP, WebSocket or gRPC. Conduit supplies its own routing and engines; custom v2rayN routing is not imported.
- Windows-user-encrypted profiles and authenticated, bounded local connection statistics.
- Local live destination activity and redacted diagnostics. Destination records are not sent to a geolocation provider.
- Balanced and Low power display modes, reduced-motion support, scalable text and suspended display sampling while minimized.
- Signed update feeds and bundles, independent elevated verification, repair, uninstall and rollback after ordinary replacement failures.
- Owner-only device enrollment/revocation and server usage tools using a separately supplied SSH key.

## Install

Download Client for ordinary use or Owner for server administration. Both include the engines and .NET runtime, and start without an enrolled identity. Import your own complete connection link or supported v2rayN server, then connect.

Network lock is off by default and affects the whole PC. Read [its exceptions and recovery controls](SETUP.md) before enabling it. Connection checks sample specific paths; a verified indication is not a guarantee covering every app or route.

Setup currently has no Windows Authenticode publisher certificate. Compare its SHA-256 with the release checksums. Installed Conduit independently verifies signed feeds and update bundles.

## Build and test

Use .NET 10 on Windows:

```powershell
dotnet restore --locked-mode
dotnet run --project tests/WorkTunnel.Checks.csproj -c Release
./scripts/check-engines.ps1
dotnet build -c Release
```

The Windows release workflow also tests actual IPv4/IPv6 packet filtering on a disposable runner, host lifetime, installation, damaged-file repair, reattachment and uninstall. Run native filtering tests only on a disposable machine. Windows Server fixtures do not establish Windows 11 roaming, sleep, battery or day-long reliability.

Official packaging uses a production signing key stored outside this repository. Contributors can build ordinary source without that key. For a separately trusted fork, initialize your own key/public trust with tools/ReleaseTool; it cannot sign an official Conduit update. See [the release process](ARCHITECTURE.md).

Packaging obfuscates the app and installer, verifies the resulting executables and checks an offline startup/UI memory budget. Private reverse maps, diagnostics and build intermediates stay in ignored directories.

## Privacy of this repository

This repository starts with a sanitized source snapshot and fresh Git history. It contains no enrolled device profile, personal server default, SSH private key, production signing private key or local diagnostic archive. Templates use placeholders and documentation addresses. The release verification public key is intentionally public.

scripts/check-public-source.ps1 checks tracked paths and template/default invariants. scripts/check-secrets.ps1 runs a pinned secret scanner against Git history. The release workflow runs both before building. These checks reduce accidental disclosure; review new files and reports before publishing.

Do not commit personal links, generated runtime configurations, credentials or diagnostic reports, or paste them into public issues. Complete legacy profiles remain importable; older profiles missing their REALITY public key or short ID require a full connection link.

Upstream engines are unmodified and pinned in engines.lock.json. Third-party notices remain with their dependencies. Internal WorkTunnel executable/data/IPC identifiers preserve compatibility with existing Conduit installations.
