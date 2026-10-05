using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace WorkTunnel;

internal sealed record LiveDestination(string Address, string Name, string Network, long Uploaded, long Downloaded, bool Active);
internal sealed record ConnectionSnapshot(string ServerAddress, LiveDestination[] Destinations, DateTimeOffset At, string? Problem = null)
{
    public static ConnectionSnapshot Empty(string? problem = null) => new("", [], DateTimeOffset.UtcNow, problem);
}

/// <summary>Reads only the authenticated, loopback-only API belonging to our sing-box.</summary>
internal sealed class ConnectionTelemetry : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private Dictionary<string, long> _previous = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    public int Port { get; }
    public string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public ConnectionTelemetry()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    }
    public void Configure(JsonNode config)
    {
        config["experimental"] = new JsonObject { ["clash_api"] = new JsonObject { ["external_controller"] = $"127.0.0.1:{Port}", ["secret"] = Secret } };
    }
    public async Task<ConnectionSnapshot> ReadAsync(string server, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port}/connections");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
            using var response = await _http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
            var snapshot = Parse(await response.Content.ReadAsStringAsync(ct), server, _previous);
            _previous = snapshot.Destinations.ToDictionary(d => d.Address + "|" + d.Network, d => d.Uploaded + d.Downloaded);
            return snapshot;
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
        { return ConnectionSnapshot.Empty("Live destination data is unavailable. No routes are being inferred."); }
        finally { _gate.Release(); }
    }
    internal static ConnectionSnapshot Parse(string json, string server, IReadOnlyDictionary<string, long> previous)
    {
        var root = JsonNode.Parse(json);
        var connections = (root?["connections"] as JsonArray)?.OfType<JsonObject>() ?? [];
        var values = connections.Select(c =>
        {
            var m = c["metadata"]; string address = m?["destinationIP"]?.GetValue<string>() ?? "";
            string name = m?["host"]?.GetValue<string>() ?? "";
            string network = m?["network"]?.GetValue<string>() ?? "";
            // Leave room for health/status fields in the 16 KiB broker envelope, even with JSON-escaped Unicode.
            return new LiveDestination(address, Clean(name, 48), network is "tcp" or "udp" ? network : "unknown", Math.Max(0, c["upload"]?.GetValue<long>() ?? 0), Math.Max(0, c["download"]?.GetValue<long>() ?? 0), false);
        }).Where(d => IPAddress.TryParse(d.Address, out _) && d.Address != server)
          .GroupBy(d => d.Address + "|" + d.Network)
          .Select(g =>
          {
              var first = g.First(); long up = g.Sum(d => d.Uploaded), down = g.Sum(d => d.Downloaded);
              bool active = previous.TryGetValue(g.Key, out long before) && up + down > before;
              return first with { Uploaded = up, Downloaded = down, Active = active };
          }).OrderByDescending(d => d.Active).ThenByDescending(d => d.Uploaded + d.Downloaded).Take(24).ToArray();
        return new(server, values, DateTimeOffset.UtcNow);
    }
    private static string Clean(string value, int length) => new(value.Where(c => !char.IsControl(c)).Take(length).ToArray());
    public void Dispose() => _http.Dispose();
}
