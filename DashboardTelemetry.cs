using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WorkTunnel;

internal sealed record AdapterSnapshot(string? Id, long Received, long Sent, string Tunnel, string Network, string Address, int? Mtu);

internal static class DashboardTelemetry
{
    // Read adapter counters only. This does not probe the internet or alter routes.
    public static AdapterSnapshot Read(string server, int port)
    {
        string network = "Unavailable", address = "No route information";
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            IPAddress? source = null;
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(IPAddress.Parse(server), port); // OS route lookup; no datagram is sent.
                source = (socket.LocalEndPoint as IPEndPoint)?.Address;
            }
            catch (Exception error) when (error is SocketException or FormatException or ArgumentException) { }
            var candidates = interfaces.Where(n => n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
                !new[] { "worktunnel", "wintun", "vmware", "tailscale", "vethernet", "virtual" }.Any(word => (n.Name + n.Description).Contains(word, StringComparison.OrdinalIgnoreCase)) &&
                n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork)).ToArray();
            var uplink = candidates.FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(source))) ?? candidates.FirstOrDefault();
            if (uplink != null)
            {
                network = uplink.Name + (candidates.Length > 1 ? $" (+{candidates.Length - 1})" : "");
                var gateway = uplink.GetIPProperties().GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                var local = uplink.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                address = $"{local}" + (gateway == null ? "" : $" → {gateway}");
            }
            var tun = interfaces.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(TunnelHealthChecker.TunAddress)));
            if (tun == null) return new(null, 0, 0, "Not available", network, address, null);
            var stats = tun.GetIPStatistics();
            int? mtu = tun.GetIPProperties().GetIPv4Properties()?.Mtu;
            return new(tun.Id, stats.BytesReceived, stats.BytesSent, tun.Name, network, address, mtu);
        }
        catch (Exception error) when (error is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        { return new(null, 0, 0, "Unavailable", network, address, null); }
    }
}

internal sealed class TrafficMeter
{
    private string? _id;
    private long _received, _sent;
    private double _at;
    public long Received { get; private set; }
    public long Sent { get; private set; }
    public void ResetBaseline() { _id = null; }
    public (double down, double up)? Sample(AdapterSnapshot snapshot, double seconds)
    {
        double elapsed = seconds - _at;
        bool continuous = snapshot.Id != null && snapshot.Id == _id && elapsed > 0 && elapsed < 5 &&
            snapshot.Received >= _received && snapshot.Sent >= _sent;
        var delta = continuous ? (down: snapshot.Received - _received, up: snapshot.Sent - _sent) : (down: 0L, up: 0L);
        _id = snapshot.Id; _received = snapshot.Received; _sent = snapshot.Sent; _at = seconds;
        if (!continuous) return null;
        Received += delta.down; Sent += delta.up;
        return (delta.down / elapsed, delta.up / elapsed);
    }
    public static string Bytes(double bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824:0.00} GiB" :
        bytes >= 1048576 ? $"{bytes / 1048576:0.0} MiB" : bytes >= 1024 ? $"{bytes / 1024:0.0} KiB" : $"{bytes:0} B";
    public static string Rate(double bytes) => Bytes(bytes) + "/s";
}
