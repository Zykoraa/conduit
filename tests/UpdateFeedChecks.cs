using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorkTunnel;

internal static class UpdateFeedChecks
{
    private sealed class Responses(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(respond(request)); }
    }
    private sealed class Unseekable(byte[] bytes, Action? onSecondRead = null) : Stream
    {
        private int _offset, _reads;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (++_reads == 2 && onSecondRead != null) { onSecondRead(); await Task.Delay(Timeout.Infinite, ct); }
            ct.ThrowIfCancellationRequested(); int count = Math.Min(Math.Min(buffer.Length, 128), bytes.Length - _offset);
            bytes.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    public static async Task Run(Action<bool, string> check, Action<Action, string> reject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] publicKey = key.ExportSubjectPublicKeyInfo();
        string flavor = UpdateInstaller.Flavor; var now = DateTimeOffset.UtcNow;
        byte[] bundle = Encoding.UTF8.GetBytes(new string('x', 1024));
        var release = new UpdateFeedManifest(1, "stable", flavor, "99.1.0", $"Conduit-{flavor}-99.1.0.wtupdate",
            Convert.ToHexString(SHA256.HashData(bundle)), bundle.Length, now, now.AddDays(30), Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant());
        byte[] document = UpdateFeedCodec.Sign(release, key);
        check(UpdateFeedCodec.Verify(document, publicKey, flavor, now) == release, "Authentic update feed binds flavor, version, asset, size and hash");
        check(UpdateFeedCodec.VersionNumber("2.4.0") == UpdateFeedCodec.Normalize(new Version(2, 4, 0, 0)), "Feed and assembly versions compare without missing-revision ambiguity");
        reject(() => UpdateFeedCodec.Verify(UpdateFeedCodec.Sign(release, otherKey), publicKey, flavor, now), "Another signing key cannot authorize update discovery");
        var changed = document.ToArray(); changed[^8] ^= 1;
        reject(() => UpdateFeedCodec.Verify(changed, publicKey, flavor, now), "Altered feed cannot authorize a download");
        reject(() => UpdateFeedCodec.Verify(document, publicKey, flavor == "Owner" ? "Client" : "Owner", now), "Wrong-flavor update feed is rejected");
        reject(() => UpdateFeedCodec.Verify(document, publicKey, flavor, now, "99.2.0"), "Previously verified newer metadata prevents feed rollback");
        foreach (var (bad, name) in new (UpdateFeedManifest, string)[]
        {
            (release with { ExpiresAt = now.AddSeconds(-1) }, "Expired feed is rejected"),
            (release with { PublishedAt = now.AddMinutes(6) }, "Future feed beyond clock tolerance is rejected"),
            (release with { ExpiresAt = now.AddDays(91) }, "Feed validity cannot extend beyond ninety days"),
            (release with { Channel = "beta" }, "Stable channel rejects another release channel"),
            (release with { Schema = 2 }, "Unsupported feed schema is rejected"),
            (release with { Asset = "../other.wtupdate" }, "Signed asset cannot escape its feed directory"),
            (release with { BundleBytes = 300_000_001 }, "Signed bundle size is bounded"),
            (release with { BundleSha256 = "bad" }, "Malformed signed download hash is rejected"),
            (release with { Version = "99.1.0-preview" }, "Stable feed refuses ambiguous prerelease versions")
        }) reject(() => UpdateFeedCodec.Verify(UpdateFeedCodec.Sign(bad, key), publicKey, flavor, now), name);
        byte[] duplicated = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(release).Replace("\"Schema\":1", "\"Schema\":1,\"Schema\":1"));
        byte[] signedDuplicate = JsonSerializer.SerializeToUtf8Bytes(new UpdateFeedEnvelope(Convert.ToBase64String(duplicated),
            Convert.ToBase64String(key.SignData(duplicated, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));
        reject(() => UpdateFeedCodec.Verify(signedDuplicate, publicKey, flavor, now), "Even authentic metadata rejects duplicate fields");
        foreach (string address in new[] { "http://updates.example/feed.json", "https://user:secret@updates.example/feed.json", "https://updates.example/feed.json?token=secret", "https://updates.example:8080/feed.json", "https://localhost/feed.json", "https://updates.example/feed.json#fragment" })
            reject(() => UpdateFeedCodec.FeedAddress(address), "Reject unsafe update feed address " + address.Split(':')[0]);
        var uri = UpdateFeedCodec.FeedAddress("https://updates.example/conduit/owner.json");
        var update = new VerifiedUpdate(release, uri);
        var options = new UpdateOptions(uri.AbsoluteUri);
        string root = Path.Combine(Path.GetTempPath(), "Conduit-feed-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        async Task RejectAsync(Func<Task> action, string name)
        {
            bool refused = false;
            try { await action(); } catch (Exception e) when (e is IOException or InvalidDataException or CryptographicException or OperationCanceledException) { refused = true; }
            check(refused, name);
        }
        try
        {
            var good = new Responses(request => new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri == uri ? document : bundle) });
            using (var discovery = new UpdateDiscovery(good, () => true, publicKey, flavor))
            {
                check((await discovery.CheckAsync(options, CancellationToken.None)).Release == release, "Discovery reads a complete authenticated feed");
                int progress = 0;
                string downloaded = await discovery.DownloadAsync(update, root, p => progress = p, CancellationToken.None);
                check(File.ReadAllBytes(downloaded).SequenceEqual(bundle) && progress == 100 && good.Calls == 2, "Download streams bytes and verifies exact signed size and hash");
                File.Delete(downloaded);
            }
            var denied = new Responses(_ => throw new Exception("An unsafe request must never reach HTTP"));
            using (var discovery = new UpdateDiscovery(denied, () => false, publicKey, flavor))
            {
                await RejectAsync(() => discovery.CheckAsync(options, CancellationToken.None), "Unverified connection refuses update network access before sending");
                check(denied.Calls == 0, "Denied update access sends no HTTP request");
            }
            foreach (var (response, name) in new (Func<HttpResponseMessage>, string)[]
            {
                (() => new(HttpStatusCode.Redirect) { Content = new ByteArrayContent([]) }, "Update discovery refuses redirects"),
                (() => new(HttpStatusCode.OK) { Content = new StreamContent(new Unseekable(new byte[16385])) }, "Chunked metadata without a length is still bounded")
            })
            {
                using var discovery = new UpdateDiscovery(new Responses(_ => response()), () => true, publicKey, flavor);
                await RejectAsync(() => discovery.CheckAsync(options, CancellationToken.None), name);
            }
            foreach (var (bytes, name) in new (byte[], string)[]
            {
                (bundle[..^1], "Truncated download is refused"),
                (bundle.Concat(new byte[] { 1 }).ToArray(), "Oversized chunked download is refused"),
                (new byte[bundle.Length], "Equal-size altered download is refused")
            })
            {
                using var discovery = new UpdateDiscovery(new Responses(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new Unseekable(bytes)) }), () => true, publicKey, flavor);
                await RejectAsync(() => discovery.DownloadAsync(update, root, null, CancellationToken.None), name);
                check(!Directory.EnumerateFiles(root).Any(), "Refused download leaves no partial or completed bundle");
            }
            using (var cancel = new CancellationTokenSource())
            using (var discovery = new UpdateDiscovery(new Responses(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new Unseekable(bundle, () => cancel.Cancel())) }), () => true, publicKey, flavor))
            {
                await RejectAsync(() => discovery.DownloadAsync(update, root, null, cancel.Token), "Interrupted download honours cancellation");
                check(!Directory.EnumerateFiles(root).Any(), "Cancellation removes the partially written bundle");
            }
            bool allowed = true;
            using (var discovery = new UpdateDiscovery(new Responses(_ => { allowed = false; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bundle) }; }), () => allowed, publicKey, flavor))
                await RejectAsync(() => discovery.DownloadAsync(update, root, null, CancellationToken.None), "Connection change stops an update before consuming its body");
            string preferences = Path.Combine(root, "preferences.json");
            var defaults = UpdatePreferences.Read(preferences);
            check(!defaults.Automatic && defaults.Feed == UpdatePreferences.OfficialFeed(UpdateInstaller.Flavor), "Official release feed is available without enabling network traffic");
            check(UpdateFeedCodec.FeedAddress(UpdatePreferences.OfficialFeed("Client")).AbsolutePath.EndsWith("Conduit-Client-stable.json") &&
                UpdatePreferences.OfficialFeed("Client") != UpdatePreferences.OfficialFeed("Owner"), "Official release feeds are direct HTTPS and distinct per flavor");
            UpdatePreferences.Write(preferences, options with { Automatic = true, HighestSeen = release.Version, LastChecked = now });
            var saved = UpdatePreferences.Read(preferences);
            check(saved.HighestSeen == release.Version && !UpdatePreferences.Due(saved, now.AddHours(11)) && UpdatePreferences.Due(saved, now.AddHours(12)),
                "Verified version and twelve-hour check cadence survive reload");
            File.WriteAllText(preferences, "{damaged");
            check(UpdatePreferences.Read(preferences) == defaults && !defaults.Automatic, "Damaged update preferences disable automatic network discovery safely");
            UpdatePreferences.Write(preferences, new());
            check(UpdatePreferences.Read(preferences).Feed == "", "An explicitly cleared feed stays cleared across reload");
        }
        finally { Directory.Delete(root, true); }
    }
}
