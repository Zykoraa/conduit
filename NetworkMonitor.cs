using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace WorkTunnel;

/// <summary>Debounces physical uplink changes, ignoring our own TUN changes.</summary>
internal sealed class NetworkMonitor : IDisposable
{
    private readonly Action<string> _changed;
    private readonly System.Threading.Timer _timer;
    private string _snapshot = Snapshot();
    private int _resume;
    private bool _disposed;
    public NetworkMonitor(Action<string> changed)
    {
        _changed = changed;
        _timer = new(_ => Check(), null, Timeout.Infinite, Timeout.Infinite);
        NetworkChange.NetworkAddressChanged += AddressChanged;
        NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
    }
    private void Schedule() { try { if (!_disposed) _timer.Change(4000, Timeout.Infinite); } catch (ObjectDisposedException) { } }
    private void AddressChanged(object? s, EventArgs e) => Schedule();
    private void AvailabilityChanged(object? s, NetworkAvailabilityEventArgs e) => Schedule();
    private void PowerChanged(object s, PowerModeChangedEventArgs e)
    { if (e.Mode == PowerModes.Resume) { Interlocked.Exchange(ref _resume, 1); Schedule(); } }
    private void Check()
    {
        if (_disposed) return;
        var next = Snapshot();
        bool resume = Interlocked.Exchange(ref _resume, 0) == 1;
        bool changed = next != _snapshot;
        _snapshot = next;
        if (resume || changed) _changed(resume ? "Computer woke from sleep" : "Network changed");
    }
    internal static string Snapshot()
    {
        try
        {
            return string.Join("|", NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && EligibleUplink(n))
                .Select(n => n.Id + ":" + string.Join(",", n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6).Select(a => a.Address.ToString()).Order()) + ":" +
                    string.Join(",", n.GetIPProperties().GatewayAddresses.Select(a => a.Address.ToString()).Order()))
                .Order());
        }
        catch { return ""; }
    }
    internal static bool? LinkAvailable()
    {
        try
        {
            var links = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && EligibleUplink(n)).ToArray();
            if (links.Any(n => n.GetIPProperties().GatewayAddresses.Count > 0)) return true;
            // PPP/mobile and IPv6-only uplinks may not expose gateways through this API.
            // Unknown still allows timed verification; it must not wait forever.
            if (links.Any(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211) ||
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal))) return null;
            return false;
        }
        catch (NetworkInformationException) { return null; }
    }
    private static bool EligibleUplink(NetworkInterface n) =>
        n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
        !new[] { "worktunnel", "wintun", "vmware", "tailscale", "vethernet", "virtual" }
            .Any(word => (n.Name + n.Description).Contains(word, StringComparison.OrdinalIgnoreCase));
    public void Dispose()
    {
        _disposed = true;
        NetworkChange.NetworkAddressChanged -= AddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        _timer.Dispose();
    }
}
