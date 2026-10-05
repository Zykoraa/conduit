using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace WorkTunnel;

internal sealed record QualityResult(DateTimeOffset At, int Sent, int Replied, double? MedianMs, double? VariationMs, double? P95Ms, double[] Samples, string Note)
{
    public double FailurePercent => Sent == 0 ? 0 : (Sent - Replied) * 100d / Sent;
    public static QualityResult FromSamples(IEnumerable<double?> input, string note = "Cloudflare STUN via tunnel; not Discord or RTP jitter")
    {
        var all = input.ToArray(); var good = all.Where(v => v.HasValue).Select(v => v!.Value).Order().ToArray();
        var differences = new List<double>();
        for (int i = 1; i < all.Length; i++) if (all[i].HasValue && all[i - 1].HasValue) differences.Add(Math.Abs(all[i]!.Value - all[i - 1]!.Value));
        return new(DateTimeOffset.Now, all.Length, good.Length,
            good.Length == 0 ? null : (good[(good.Length - 1) / 2] + good[good.Length / 2]) / 2,
            differences.Count == 0 ? null : differences.Average(),
            good.Length == 0 ? null : good[Math.Max(0, (int)Math.Ceiling(good.Length * .95) - 1)],
            all.Select(v => v ?? -1).ToArray(), note);
    }
}

internal static class QualityProbe
{
    public static async Task<QualityResult> RunAsync(string expected, IProgress<string>? progress, CancellationToken ct)
    {
        var values = new List<double?>();
        using var resolve = CancellationTokenSource.CreateLinkedTokenSource(ct); resolve.CancelAfter(5000);
        var address = await TunnelDns.ResolveIPv4Async("stun.cloudflare.com", resolve.Token);
        using var client = new UdpClient(new IPEndPoint(TunnelHealthChecker.TunAddress, 0));
        client.Connect(address, 3478);
        for (int i = 0; i < 12; i++)
        {
            ct.ThrowIfCancellationRequested();
            var packet = new byte[20]; packet[1] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 0x2112a442);
            RandomNumberGenerator.Fill(packet.AsSpan(8));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(1500);
            double? elapsed = null; var clock = Stopwatch.StartNew();
            try
            {
                await client.SendAsync(packet, timeout.Token);
                while (true)
                {
                    var response = (await client.ReceiveAsync(timeout.Token)).Buffer;
                    var mapped = TunnelHealthChecker.ParseStun(response, packet);
                    if (mapped == null) continue; // Ignore late replies to an earlier transaction.
                    if (mapped.ToString() != expected) throw new InvalidOperationException("UDP used an unexpected exit. No quality result was accepted.");
                    elapsed = clock.Elapsed.TotalMilliseconds; break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { }
            values.Add(elapsed); progress?.Report($"UDP probe {i + 1}/12: {(elapsed.HasValue ? $"{elapsed:0} ms" : "no reply within 1.5s")}");
            if (i < 11) await Task.Delay(650, ct);
        }
        return QualityResult.FromSamples(values);
    }
}
