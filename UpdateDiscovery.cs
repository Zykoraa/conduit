using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace WorkTunnel;

internal sealed record UpdateOptions(string Feed = "", bool Automatic = false, string? HighestSeen = null, DateTimeOffset? LastChecked = null);
internal static class UpdatePreferences
{
    internal static string OfficialFeed(string flavor) => flavor is "Owner" or "Client"
        ? "https://zykoraa.github.io/conduit/Conduit-" + flavor + "-stable.json"
        : throw new ArgumentException("Invalid update flavor.");
    private static UpdateOptions DefaultOptions() => new(OfficialFeed(UpdateInstaller.Flavor));
    internal static string PathName => Path.Combine(AppPaths.DataDir, "update-feed.json");
    internal static UpdateOptions Read(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 4096) return DefaultOptions();
            var options = JsonSerializer.Deserialize<UpdateOptions>(File.ReadAllText(path)) ?? DefaultOptions();
            if (options.Feed.Length > 0) _ = UpdateFeedCodec.FeedAddress(options.Feed);
            if (options.HighestSeen != null) _ = UpdateFeedCodec.VersionNumber(options.HighestSeen);
            return options;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or NullReferenceException)
        { return DefaultOptions(); }
    }
    internal static void Write(string path, UpdateOptions options)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(options), AppPaths.Utf8NoBom);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static bool Due(UpdateOptions options, DateTimeOffset now) => options.Automatic && options.Feed.Length > 0 &&
        (!options.LastChecked.HasValue || now - options.LastChecked.Value >= TimeSpan.FromHours(12));
}

internal sealed class UpdateDiscovery : IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<bool> _allowed;
    private readonly byte[] _publicKey;
    private readonly string _flavor;
    internal UpdateDiscovery(HttpMessageHandler handler, Func<bool> allowed, byte[]? publicKey = null, string? flavor = null)
    {
        _http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Conduit/" + typeof(UpdateDiscovery).Assembly.GetName().Version);
        _allowed = allowed; _publicKey = publicKey ?? ReleaseTrust.PublicKey; _flavor = flavor ?? UpdateInstaller.Flavor;
    }
    internal static UpdateDiscovery ForController(ITunnelController controller)
    {
        bool tunnel = controller.ConnectionRequested;
        bool Allowed() => tunnel ? controller.ConnectionRequested && controller.Verified : !controller.ConnectionRequested;
        if (!Allowed()) throw new IOException("Wait for a verified tunnel before checking updates, or disconnect for a direct manual check.");
        var handler = tunnel ? TunnelTransport.CreateHandler(allowed: Allowed) : new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
        return new(handler, Allowed);
    }
    private void DemandAllowed() { if (!_allowed()) throw new IOException("Connection state changed. The update request was stopped."); }
    private async Task<HttpResponseMessage> Get(Uri uri, CancellationToken ct)
    {
        DemandAllowed();
        var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            // Static signed feeds and assets are direct HTTPS URLs. Never follow redirects or return partial ranges.
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count != 0)
                throw new IOException("The update server did not return a direct complete response.");
            DemandAllowed(); return response;
        }
        catch { response.Dispose(); throw; }
    }
    internal async Task<VerifiedUpdate> CheckAsync(UpdateOptions options, CancellationToken ct)
    {
        Uri address = UpdateFeedCodec.FeedAddress(options.Feed);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await Get(address, deadline.Token);
        if (response.Content.Headers.ContentLength > UpdateFeedCodec.MaximumDocumentBytes) throw new InvalidDataException("Update feed is too large.");
        using var source = await response.Content.ReadAsStreamAsync(deadline.Token); using var memory = new MemoryStream();
        byte[] buffer = new byte[4096]; int count;
        while ((count = await source.ReadAsync(buffer, deadline.Token)) != 0)
        {
            DemandAllowed();
            if (memory.Length + count > UpdateFeedCodec.MaximumDocumentBytes) throw new InvalidDataException("Update feed is too large.");
            memory.Write(buffer, 0, count);
        }
        DemandAllowed();
        return new(UpdateFeedCodec.Verify(memory.ToArray(), _publicKey, _flavor, DateTimeOffset.UtcNow, options.HighestSeen), address);
    }
    internal async Task<string> DownloadAsync(VerifiedUpdate update, string storage, Action<int>? progress, CancellationToken ct)
    {
        UpdateFeedCodec.Validate(update.Release, _publicKey, _flavor, DateTimeOffset.UtcNow);
        _ = UpdateFeedCodec.FeedAddress(update.Feed.AbsoluteUri);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(5));
        Directory.CreateDirectory(storage);
        string path = Path.Combine(storage, "download-" + Guid.NewGuid().ToString("N") + ".wtupdate");
        string partial = path + ".partial"; bool complete = false;
        try
        {
            using var response = await Get(update.Download, deadline.Token);
            if (response.Content.Headers.ContentLength is long length && length != update.Release.BundleBytes)
                throw new InvalidDataException("Download size differs from its signed metadata.");
            using var source = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0; int lastProgress = -1;
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[65536]; int count;
                while ((count = await source.ReadAsync(buffer, deadline.Token)) != 0)
                {
                    DemandAllowed(); total += count;
                    if (total > update.Release.BundleBytes) throw new InvalidDataException("Download exceeds its signed size.");
                    hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                    int percent = (int)(total * 100 / update.Release.BundleBytes);
                    if (percent != lastProgress) { progress?.Invoke(percent); lastProgress = percent; }
                }
                DemandAllowed();
                if (total != update.Release.BundleBytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Release.BundleSha256, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException("Download does not match the signed size and SHA-256. Nothing was installed.");
            }
            deadline.Token.ThrowIfCancellationRequested();
            File.Move(partial, path); complete = true; return path;
        }
        finally { if (!complete && File.Exists(partial)) File.Delete(partial); }
    }
    public void Dispose() => _http.Dispose();
}
