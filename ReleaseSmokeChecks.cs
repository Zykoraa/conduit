using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WorkTunnel;

/// <summary>Offline checks of the actual published executable. Never connects or installs.</summary>
internal static class ReleaseSmokeChecks
{
    internal static int Measure(string report)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int result = Run(report + ".checks.txt");
        clock.Stop();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = System.Diagnostics.Process.GetCurrentProcess(); process.Refresh();
        File.WriteAllText(report, JsonSerializer.Serialize(new {
            Scenario = "Offline published-executable checks and dashboard construction/rendering; no engines or connection",
            ExitCode = result, ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
            PrivateMiB = process.PrivateMemorySize64 / 1048576.0, WorkingSetMiB = process.WorkingSet64 / 1048576.0,
            CpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds
        }, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }
    public static int Run(string report, bool installer = false)
    {
        var passed = new List<string>();
        try
        {
            void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); passed.Add("PASS " + name); }
            T RoundTrip<T>(T value)
            {
                string json = JsonSerializer.Serialize(value);
                var copy = JsonSerializer.Deserialize<T>(json)!;
                Check(JsonSerializer.Serialize(copy) == json, "JSON round trip " + typeof(T).Name);
                return copy;
            }
            Check(typeof(TunnelController).FullName != "WorkTunnel.TunnelController", "Implementation types are renamed in the published executable");
            Check(typeof(DeviceLink).GetMethod("Parse") == null, "Implementation methods are renamed");
            Check(new Profile().Server == "" && !new Profile().IsValid, "Published app starts without a default server or device identity");
            var profile = new Profile { User = "Release fixture", Server = "203.0.113.7", Uuid = "11111111-2222-4333-8444-555555555555", Sni = "example.com", PublicKey = new string('A', 43), ShortId = "aabb" };
            Check(DeviceLink.Parse(DeviceLink.Create(profile)).Uuid == profile.Uuid, "Hidden strings and profile link parsing");
            var copy = RoundTrip(profile);
            Check(copy.Server == profile.Server && JsonSerializer.Serialize(copy).Contains("\"uuid\""), "Existing profile field names are preserved");
            profile.Imported = new ImportedProxy { Protocol = "trojan", Security = "tls", Credential = "fixture-password", Transport = "ws", Host = "example.com" };
            Check(RoundTrip(profile).Imported!.Credential == "fixture-password", "Imported server JSON remains compatible");
            var check = new HealthCheck(true, "Fixture", 12);
            var health = new TunnelHealth(check, check, check, check, profile.Server, DateTimeOffset.UtcNow, new(check, check, check));
            var connections = new ConnectionSnapshot(profile.Server, [new("198.51.100.2", "fixture", "tcp", 12, 34, true)], DateTimeOffset.UtcNow);
            var snapshot = RoundTrip(new BrokerSnapshot(TunnelState.Connected, "Fixture", profile.Server, true, health, true, true, false, 1, 2,
                Connections: connections, LockObservation: new(true, true, true, "Fixture")));
            Check(snapshot.Health!.Healthy && snapshot.Connections!.Destinations[0].Uploaded == 12, "Nested health and connection contracts");
            Check(snapshot.LockObservation is { Known: true, BlockingVerified: true }, "Observed network-lock JSON contract");
            var notifications = new TrayNotificationPolicy();
            bool initialNotice = notifications.Observe(new(TunnelState.Connected, "Fixture", verified: true)) == NoticeKind.Info;
            bool checkSilent = notifications.Observe(new(TunnelState.Connected, "UDP unverified")) == null &&
                notifications.Observe(new(TunnelState.Connected, "Fixture", verified: true)) == null;
            bool recoveryNotice = notifications.Observe(new(TunnelState.Connecting, "Recovering")) == null &&
                notifications.Observe(new(TunnelState.Connected, "Recovered", verified: true)) == NoticeKind.Info;
            Check(initialNotice && checkSilent && recoveryNotice, "Connection alerts distinguish probe results from lifecycle recovery after obfuscation");
            Check(!BrokerFacts.Verified(snapshot with { Health = health with { CheckedAt = DateTimeOffset.UtcNow.AddMinutes(-2) } }, DateTimeOffset.UtcNow), "Expired broker evidence is rejected after obfuscation");
            Check(JsonSerializer.Serialize(snapshot).Contains("\"Verified\":true"), "IPC property names remain stable");
            using (var stream = new MemoryStream())
            {
                BrokerWire.WriteAsync(stream, new BrokerRequest("connect", profile, true, false), CancellationToken.None).GetAwaiter().GetResult();
                stream.Position = 0;
                Check(BrokerWire.ReadAsync<BrokerRequest>(stream, CancellationToken.None).GetAwaiter().GetResult().Profile!.Imported!.Host == "example.com", "Framed broker serialization");
            }
            RoundTrip(new ReleaseManifest("2.4.0", UpdateInstaller.Flavor));
            RoundTrip(new SignedRelease("2.4.0", UpdateInstaller.Flavor, new string('0', 64), "fixture"));
            RoundTrip(new UpdatePlan("source", "target", "2.4.0", ["app.exe"], new() { ["app.exe"] = "fixture" }, 1, 2, false));
            using (var feedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                byte[] feedPublic = feedKey.ExportSubjectPublicKeyInfo(); var now = DateTimeOffset.UtcNow;
                var feed = new UpdateFeedManifest(1, "stable", UpdateInstaller.Flavor, "99.0.0", $"Conduit-{UpdateInstaller.Flavor}-99.0.0.wtupdate",
                    new string('0', 64), 1024, now, now.AddDays(30), Convert.ToHexString(SHA256.HashData(feedPublic)).ToLowerInvariant());
                Check(UpdateFeedCodec.Verify(UpdateFeedCodec.Sign(feed, feedKey), feedPublic, UpdateInstaller.Flavor, now) == feed,
                    "Signed update-feed contracts and verification survive obfuscation");
                RoundTrip(new UpdateOptions("https://updates.example/feed.json", true, "99.0.0", now));
            }
            RoundTrip(new RollbackInfo("source", "target", "2.2.1", ["app.exe"], new() { ["app.exe"] = "fixture" }, []));
            RoundTrip(new HistoryEntry(DateTimeOffset.UtcNow, "Fixture", "Message"));
            RoundTrip(new IncidentStatus(DateTimeOffset.UtcNow, "fixture", "Connected", true, false, health, "Fixture"));
            RoundTrip(QualityResult.FromSamples([20, null, 30]));
            Check(JsonSerializer.Serialize(new { action = "status", job = "fixture" }) == "{\"action\":\"status\",\"job\":\"fixture\"}", "Anonymous JSON fields remain stable");
#if OWNER_BUILD
            var devices = JsonSerializer.Deserialize<DeviceList>("{\"Devices\":[{\"Id\":\"fixture\",\"Name\":\"Test\",\"Managed\":true}],\"Sni\":\"example.com\",\"ShortId\":\"aabb\",\"Port\":443}")!;
            Check(devices.Devices[0].Name == "Test" && typeof(TunnelDevice).GetProperty("DisplayName") != null, "Owner response and display binding contracts");
#endif
            byte[] plaintext = Encoding.UTF8.GetBytes("offline release fixture");
            Check(SecretStore.Unprotect(SecretStore.Protect(plaintext)).SequenceEqual(plaintext), "DPAPI native imports");
            Check(TunnelState.Connected.ToString() == "Connected", "Diagnostic enum names remain readable");
            using var icon = BrandIcon.WithStatus(Color.Cyan);
            Check(icon.Width > 0, "Embedded icon and native handle cleanup");
            Check(!typeof(ReleaseSmokeChecks).Assembly.GetManifestResourceNames().Contains("Conduit.world-land.json"), "Unused map geometry is absent from the release");
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
            using var controller = new TunnelController { AutoHeal = false };
            using var dashboard = new DashboardForm(controller, persistHistory: false);
            AnimationSettings.SetEnabled(dashboard, false);
            _ = dashboard.Handle; // Exercise native frame entry points without showing or starting sampling.
            foreach (string page in new[] { "Overview", "Connections", "Diagnostics", "Settings" })
            {
                dashboard.Navigate(page); dashboard.PerformLayout();
                using var bitmap = new Bitmap(dashboard.Width, dashboard.Height);
                dashboard.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            }
            Check(true, "All dashboard pages construct and render without starting a tunnel");
            if (installer)
            {
                using var payload = typeof(ReleaseSmokeChecks).Assembly.GetManifestResourceStream("WorkTunnel.payload") ?? throw new InvalidDataException("Missing installer payload");
                using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
                byte[] Read(string name) { using var source = zip.GetEntry(name)!.Open(); using var memory = new MemoryStream(); source.CopyTo(memory); return memory.ToArray(); }
                var signed = ReleaseSignature.Verify(Read("release.json"), Read("release.sig"), ReleaseTrust.PublicKey);
                using var package = zip.GetEntry("package.zip")!.Open();
                Check(Convert.ToHexString(SHA256.HashData(package)).Equals(signed.PackageSha256, StringComparison.OrdinalIgnoreCase) && signed.Flavor == UpdateInstaller.Flavor, "Installer embedded signed payload verifies");
            }
            File.WriteAllLines(report, passed.Append($"{passed.Count} published-executable checks passed ({UpdateInstaller.Flavor})."));
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllLines(report, passed.Append("FAIL " + e));
            return 1;
        }
    }
}
