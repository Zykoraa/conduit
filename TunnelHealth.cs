using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WorkTunnel;

internal sealed record HealthCheck(bool Ok, string Detail, long? Milliseconds = null, bool BypassDetected = false);
internal sealed record TunnelHealth(HealthCheck Adapter, HealthCheck Internet, HealthCheck Dns,
    HealthCheck Udp, string? ExitIp, DateTimeOffset CheckedAt, RouteHealth? Routes = null)
{
    public bool Healthy => Adapter.Ok && Internet.Ok && Dns.Ok && Udp.Ok && Routes?.Ok == true;
    // A third-party STUN outage alone must not cause an endless restart loop.
    public bool NeedsRecovery => !Adapter.Ok || !Internet.Ok || !Dns.Ok || Routes?.Ok != true;
}

internal static class HealthFreshness
{
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(60);
    internal static bool IsCurrent(TunnelHealth? health, DateTimeOffset now) => health != null &&
        now >= health.CheckedAt && now - health.CheckedAt < MaximumAge;
}

internal sealed class TunnelHealthChecker
{
    internal static readonly IPAddress TunAddress = IPAddress.Parse("172.19.0.1");

    public async Task<TunnelHealth> CheckAsync(string expectedExit, CancellationToken ct)
    {
        bool adapter = NetworkInterface.GetAllNetworkInterfaces().Any(n =>
            n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(TunAddress)));
        if (!adapter)
        {
            var absent = new HealthCheck(false, "Tunnel adapter is not ready");
            return new(absent, absent, absent, absent, null, DateTimeOffset.Now);
        }
        var internet = CheckInternetAsync(expectedExit, ct);
        var dns = CheckDnsAsync(ct);
        var udp = CheckUdpAsync(expectedExit, ct);
        var routes = RouteProbe.Read();
        // A known bypass already fails verification. Do not send an exit probe over it.
        var ordinary = routes.v4.Ok ? CheckInternetAsync(expectedExit, ct, bindTunnel: false)
            : Task.FromResult((check: new HealthCheck(false, "Exit probe skipped because ordinary IPv4 routing is not verified."), ip: (string?)null));
        await Task.WhenAll(internet, dns, udp, ordinary);
        return new(new(true, "Full-device adapter is up"), internet.Result.check, dns.Result,
            udp.Result, internet.Result.ip, DateTimeOffset.Now, new(routes.v4, routes.v6,
                ordinary.Result.check with { Detail = ordinary.Result.check.Ok ? "Ordinary IPv4 HTTPS traffic exits through the expected server."
                    : !routes.v4.Ok ? ordinary.Result.check.Detail : "Ordinary app exit is unconfirmed or unexpected. Check other VPNs and routes." }));
    }

    private static async Task<(HealthCheck check, string? ip)> CheckInternetAsync(string expected, CancellationToken ct, bool bindTunnel = true)
    {
        using var http = new HttpClient(TunnelTransport.CreateHandler(bindTunnel: bindTunnel))
            { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 8192 };
        foreach (var url in new[] { "https://api.ipify.org", "https://icanhazip.com" })
        {
            try
            {
                var clock = Stopwatch.StartNew();
                var ip = (await http.GetStringAsync(url, ct)).Trim();
                if (!IPAddress.TryParse(ip, out _)) continue;
                return (new(ip == expected, ip == expected ? "Traffic exits your VM" : "Exit does not match your VM", clock.ElapsedMilliseconds), ip);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
        ct.ThrowIfCancellationRequested();
        return (new(false, "No reply through the full-device tunnel"), null);
    }

    private static async Task<HealthCheck> CheckDnsAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var clock = Stopwatch.StartNew();
            await TunnelDns.ResolveIPv4Async("example.com", timeout.Token);
            return new(true, "DNS through tunnel", clock.ElapsedMilliseconds);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return new(false, "Tunnel DNS did not answer"); }
    }

    internal static bool ValidDnsReply(byte[] reply, byte[] query) => TunnelDns.ParseReply(reply, query).Length > 0;

    private static async Task<HealthCheck> CheckUdpAsync(string expected, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));
        try
        {
            var target = await TunnelDns.ResolveIPv4Async("stun.cloudflare.com", timeout.Token);
            using var client = new UdpClient(new IPEndPoint(TunAddress, 0));
            client.Connect(target, 3478);
            var packet = new byte[20]; packet[1] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 0x2112A442);
            RandomNumberGenerator.Fill(packet.AsSpan(8));
            var clock = Stopwatch.StartNew();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await client.SendAsync(packet, timeout.Token);
                using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                receiveTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    var response = (await client.ReceiveAsync(receiveTimeout.Token)).Buffer;
                    var mapped = ParseStun(response, packet);
                    if (mapped != null) return new(mapped.ToString() == expected,
                        mapped.ToString() == expected ? "UDP replies through your VM" : "UDP exit does not match your VM", clock.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
            }
            return new(false, "UDP test did not reply; voice may be affected");
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return new(false, "UDP test unavailable; voice is unverified"); }
    }

    internal static IPAddress? ParseStun(byte[] response, byte[] request)
    {
        if (response.Length < 20 || request.Length < 20 || response[0] != 1 || response[1] != 1 ||
            !response.AsSpan(4, 16).SequenceEqual(request.AsSpan(4, 16))) return null;
        int end = 20 + BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2));
        if (end > response.Length) return null;
        for (int offset = 20; offset + 4 <= end;)
        {
            int type = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset, 2));
            int length = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 2, 2));
            int start = offset + 4;
            if (start + length > end) return null;
            if ((type == 0x20 || type == 1) && length == 8 && response[start + 1] == 1)
            {
                var ip = response.AsSpan(start + 4, 4).ToArray();
                if (type == 0x20) for (int i = 0; i < 4; i++) ip[i] ^= request[4 + i];
                return new IPAddress(ip);
            }
            offset = start + ((length + 3) & ~3);
        }
        return null;
    }
}
