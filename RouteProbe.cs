using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace WorkTunnel;

internal sealed record RouteHealth(HealthCheck Ipv4, HealthCheck Ipv6, HealthCheck OrdinaryInternet)
{
    public bool Ok => Ipv4.Ok && Ipv6.Ok && OrdinaryInternet.Ok;
}

internal static class RouteProbe
{
    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterfaceEx(IntPtr destination, out uint index);

    // No traffic is emitted: ask Windows where an ordinary socket would be routed.
    internal static uint? BestInterface(IPAddress address)
    {
        var bytes = new byte[28];
        bool v6 = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        BitConverter.GetBytes((ushort)(v6 ? 23 : 2)).CopyTo(bytes, 0);
        address.GetAddressBytes().CopyTo(bytes, v6 ? 8 : 4);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            uint result = GetBestInterfaceEx(memory, out uint index);
            if (result == 0) return index;
            if (result is 1231 or 1168) return null; // No network route / element not found.
            throw new NetworkInformationException((int)result);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    internal static HealthCheck Evaluate(IReadOnlyList<uint?> routes, IReadOnlyCollection<uint> tunnelIndexes, bool ipv6)
    {
        string family = ipv6 ? "IPv6" : "IPv4";
        if (routes.Any(i => i.HasValue && !tunnelIndexes.Contains(i.Value)))
            return new(false, $"{family} has an ordinary internet route outside Conduit. Check other VPNs and reconnect.", BypassDetected: true);
        if (routes.All(i => i is null))
            return new(ipv6, ipv6 ? "No public IPv6 route is available." : "No public IPv4 route is available.");
        return new(true, $"Sampled {family} internet routes use Conduit.");
    }
    public static (HealthCheck v4, HealthCheck v6) Read()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(TunnelHealthChecker.TunAddress)));
            var indexes = new List<uint>();
            if (nic != null)
            {
                if (nic.Supports(NetworkInterfaceComponent.IPv4)) indexes.Add((uint)nic.GetIPProperties().GetIPv4Properties().Index);
                if (nic.Supports(NetworkInterfaceComponent.IPv6)) indexes.Add((uint)nic.GetIPProperties().GetIPv6Properties().Index);
            }
            return (Evaluate(new[] { "1.1.1.1", "8.8.8.8" }.Select(s => BestInterface(IPAddress.Parse(s))).ToArray(), indexes, false),
                Evaluate(new[] { "2606:4700:4700::1111", "2001:4860:4860::8888" }.Select(s => BestInterface(IPAddress.Parse(s))).ToArray(), indexes, true));
        }
        catch (Exception e) when (e is NetworkInformationException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        { return (new(false, "IPv4 route check unavailable."), new(false, "IPv6 route check unavailable.")); }
    }
}
