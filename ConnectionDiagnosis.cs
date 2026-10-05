namespace WorkTunnel;

internal sealed record ConnectionDiagnosis(string Happening, string Evidence, string NextStep)
{
    public override string ToString() => $"What's happening\r\n{Happening}\r\n\r\nEvidence\r\n{Evidence}\r\n\r\nWhat to do\r\n{NextStep}";
    public static ConnectionDiagnosis For(TunnelHealth? h)
    {
        if (h == null) return new("Connection is not verified yet.", "No completed checks are available.", "Wait for the checks, or choose Check connection.");
        if (!HealthFreshness.IsCurrent(h, DateTimeOffset.UtcNow)) return new("Connection results are stale or have an invalid timestamp.", "The last checks cannot establish current protection.", "Run Check connection. Old results cannot confirm current protection.");
        if (h.Routes?.Ipv4.BypassDetected == true || h.Routes?.Ipv6.BypassDetected == true)
            return new("Windows reports an internet route outside Conduit.", $"{h.Routes.Ipv4.Detail} {h.Routes.Ipv6.Detail}",
                "Keep network lock enabled, close competing VPNs and reconnect. This route finding does not establish whether a firewall blocked the traffic.");
        if (!h.Adapter.Ok) return new("The tunnel adapter is unavailable.", h.Adapter.Detail, "Check the latest engine event and choose Reconnect. Keep the network lock enabled to block fallback traffic.");
        if (!h.Dns.Ok && !h.Internet.Ok) return new("DNS and internet checks failed.", "The cause is unknown: the local network, tunnel, DNS, or test services may be responsible.", "Check Wi-Fi or Ethernet and any sign-in page. Reconnect, then save diagnostics if the failure continues.");
        if (!h.Internet.Ok) return new("The tunnel's internet exit is unverified.", h.Internet.Detail, "Check the server and its expected exit IP. A server behind NAT can have a different exit address. Then reconnect.");
        if (!h.Dns.Ok) return new("Tunnel DNS is not responding.", h.Dns.Detail, "Retry Check connection. Conduit first tries restarting the tunnel adapter; check other VPNs if it persists.");
        if (h.Routes == null) return new("Ordinary application routes are not verified.", "Tunnel-bound probes passed, but route checks are unavailable.", "Run Check connection. Restart an older tunnel host after updating Conduit.");
        if (!h.Routes.Ipv4.Ok || !h.Routes.Ipv6.Ok) return new("An internet route is missing or bypasses Conduit.", $"{h.Routes.Ipv4.Detail} {h.Routes.Ipv6.Detail}", "Close competing VPNs and choose Reconnect. A route check does not establish whether a firewall blocked traffic.");
        if (!h.Routes.OrdinaryInternet.Ok) return new("Ordinary application traffic is not verified.", h.Routes.OrdinaryInternet.Detail, "Check other VPNs, network routes and the expected exit IP. Keep the network lock enabled while investigating.");
        if (!h.Udp.Ok) return new("UDP is unverified; voice may be affected.", "HTTPS, DNS and ordinary route checks passed. The STUN service may be blocked or unavailable.", "Try Measure latency, then test your voice app. A failed STUN check alone does not trigger repeated engine restarts.");
        return new("Connection and sampled routes are verified.", "Tunnel HTTPS, ordinary IPv4 HTTPS, DNS, UDP and sampled IPv4/IPv6 routes passed. No IPv6 route is also acceptable.", "Use your actual voice or video app to check call quality. Sampled routes cannot prove every destination or per-app policy.");
    }
}
