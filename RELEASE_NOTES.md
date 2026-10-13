# Conduit 2.4.4

Detailed Oracle VM and Xray setup documentation, bundled with both Windows editions.

- Documents Conduit's existing VM `147.224.209.249` / `10.0.0.30`, SSH access, Oracle routes/security rules and guest firewall.
- Explains VLESS, REALITY, Vision, hostname routing, prefer-IPv4 DNS, TUN, XUDP, device identity and recovery.
- Preserves the existing Conduit server, saved profiles, network policy, UI and release signing identity. No server migration or restart is required.
- Adds a standalone guide and checksum-verified public server-helper ZIP. Executable scripts use Unix line endings.
- Both blank-profile Windows installers and signed update bundles include VM_SETUP.md. The installer lifecycle check verifies the installed guide matches source.

Culvert is a separate application and VM. Never use its profile or keys for Conduit. Public downloads exclude the older private repository's historical identity files.

Validation: production package smoke/resource checks, signed feeds/bundles, archive/checksum audits and disposable GitHub Windows installation/repair/uninstall checks. The existing VM was inspected without changing its configuration. This documentation release is not a new Discord or roaming performance test.
