using System.Buffers.Binary;
using System.Net;
using WorkTunnel;

if (args.Length == 3 && args[0] == "--live-update-feed") { await LiveReleaseChecks.Feed(args[1], args[2]); return; }
if (args.Length == 3 && args[0] == "--live-update-feed-unbound") { await LiveReleaseChecks.Feed(args[1], args[2], includeBound: false); return; }
if (args.Length == 2 && args[0] == "--benchmark-profile") { LiveReleaseChecks.PrepareBenchmarkProfile(args[1]); return; }
if (args.Length == 2 && args[0] == "--benchmark-broker") { await LiveReleaseChecks.Broker(args[1]); return; }
if (args.Contains("--apply-lock-and-exit")) { NativeChecks.RequireDisposableRunner(); NetworkLock.Apply("1.1.1.1", 443, 0); return; }
if (args.Contains("--sleep-child")) { await Task.Delay(Timeout.Infinite); return; }
if (args.Contains("--job-parent"))
{
    using var job = ProcessLifetime.OwnHostAndChildren();
    using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { Arguments = "--sleep-child", UseShellExecute = false, CreateNoWindow = true })!;
    Console.WriteLine(child.Id); await Task.Delay(Timeout.Infinite); return;
}

int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch { Check(true, name); return; } throw new Exception("FAIL " + name); }

var meter = new TrafficMeter();
var adapter = new AdapterSnapshot("one", 1000, 2000, "tun", "wifi", "192.0.2.1", 1400);
Check(meter.Sample(adapter, 1) == null, "Traffic starts with a baseline, not a false spike");
var rate = meter.Sample(adapter with { Received = 3000, Sent = 3000 }, 3);
Check(rate == (1000d, 500d) && meter.Received == 2000 && meter.Sent == 1000, "Traffic rates use elapsed time and counter deltas");
Check(meter.Sample(adapter with { Received = 2, Sent = 2 }, 4) == null, "Adapter reset leaves a chart gap instead of a negative rate");
Check(meter.Sample(adapter with { Id = "two" }, 5) == null, "Adapter replacement resets the baseline");
Check(meter.Sample(adapter with { Id = "two", Received = 100000 }, 20) == null, "Hidden or suspended dashboard does not invent intervening samples");
Check(meter.Sample(adapter with { Id = null }, 21) == null, "Missing adapter has no throughput sample");

var profile = new Profile { User = "Phone & tablet #1", Server = "203.0.113.7", Uuid = Guid.NewGuid().ToString(),
    Sni = "www.example.com", PublicKey = new string('a', 43), ShortId = "aabb" };
var link = DeviceLink.Create(profile);
var configDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../configs"));
var generatedXray = System.Text.Json.Nodes.JsonNode.Parse(TunnelController.BuildXrayConfig(profile, File.ReadAllText(Path.Combine(configDir, "xray.template.json"))))!;
Check(new Profile().Server == "" && !new Profile().IsValid, "Blank profiles have no personal server default");
var generatedReality = generatedXray["outbounds"]![0]!["streamSettings"]!["realitySettings"]!;
Check(generatedReality["publicKey"]!.GetValue<string>() == profile.PublicKey && generatedReality["shortId"]!.GetValue<string>() == profile.ShortId,
    "Generated REALITY settings come from the imported profile");
var missingKeys = System.Text.Json.Nodes.JsonNode.Parse(TunnelController.BuildXrayConfig(new Profile { Server = profile.Server, Sni = profile.Sni, Uuid = profile.Uuid }, File.ReadAllText(Path.Combine(configDir, "xray.template.json"))))!;
Check(missingKeys["outbounds"]![0]!["streamSettings"]!["realitySettings"]!["publicKey"]!.GetValue<string>() == "",
    "An incomplete profile cannot inherit a bundled REALITY key");
Check(generatedXray["outbounds"]![0]!["settings"]!["vnext"]![0]!["address"]!.GetValue<string>() == profile.Server,
    "Imported endpoint is used by Xray");
var generatedTun = System.Text.Json.Nodes.JsonNode.Parse(TunnelController.BuildTunConfig(profile, File.ReadAllText(Path.Combine(configDir, "singbox-tun.json"))))!;
var rules = generatedTun["route"]!["rules"]!.AsArray();
Check(rules.Any(r => r?["action"]?.GetValue<string>() == "reject" && r["ip_cidr"]?[0]?.GetValue<string>() == "172.19.0.0/30"),
    "Import preserves the TUN loop guard");
Check(rules.Any(r => r?["outbound"]?.GetValue<string>() == "direct" && r["ip_cidr"]?[0]?.GetValue<string>() == profile.Server + "/32"),
    "Imported endpoint bypass route matches Xray");
var roundTrip = DeviceLink.Parse(link);
Check(roundTrip.User == profile.User && roundTrip.Uuid == profile.Uuid && roundTrip.PublicKey == profile.PublicKey, "Share link round trip preserves identity and encoded name");
Reject(() => DeviceLink.Parse(link.Replace("security=reality", "security=tls")), "Reject incompatible protocol");
Reject(() => DeviceLink.Parse(link.Replace("203.0.113.7", "-oProxyCommand=bad")), "Reject malformed server address");
Reject(() => DeviceLink.Parse(link.Replace("sid=aabb", "sid=xyz")), "Reject invalid short ID");
Reject(() => DeviceLink.Parse(link.Replace("&fp=chrome", "&sni=other&fp=chrome")), "Reject duplicate query parameters");

var request = new byte[20]; request[1] = 1;
BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), 0x2112a442);
Random.Shared.NextBytes(request.AsSpan(8));
var response = new byte[32]; response[0] = 1; response[1] = 1; response[3] = 12;
request.AsSpan(4, 16).CopyTo(response.AsSpan(4));
response[21] = 0x20; response[23] = 8; response[25] = 1;
var ip = IPAddress.Parse("203.0.113.7").GetAddressBytes();
for (int i = 0; i < 4; i++) response[28 + i] = (byte)(ip[i] ^ request[4 + i]);
Check(TunnelHealthChecker.ParseStun(response, request)?.ToString() == "203.0.113.7", "Decode STUN XOR mapped IPv4");
var wrongTransaction = (byte[])response.Clone(); wrongTransaction[8] ^= 1;
Check(TunnelHealthChecker.ParseStun(wrongTransaction, request) == null, "Reject unrelated UDP reply");
Check(TunnelHealthChecker.ParseStun(response[..29], request) == null, "Reject truncated STUN attribute");
var badLength = (byte[])response.Clone(); badLength[23] = 255;
Check(TunnelHealthChecker.ParseStun(badLength, request) == null, "Reject oversized STUN attribute");
var dnsQuery = TunnelDns.Query("example.com");
var dns = PrivacyChecks.Answer(dnsQuery);
Check(TunnelHealthChecker.ValidDnsReply(dns, dnsQuery), "Accept DNS answer matching query");
var otherQuery = (byte[])dnsQuery.Clone(); otherQuery[0] ^= 1;
Check(!TunnelHealthChecker.ValidDnsReply(dns, otherQuery), "Reject unrelated DNS response");
dns[3] = 0x83;
Check(!TunnelHealthChecker.ValidDnsReply(dns, dnsQuery), "NXDOMAIN does not pass DNS health");
var ok = new HealthCheck(true, "ok"); var fail = new HealthCheck(false, "failed");
var partial = new TunnelHealth(ok, ok, ok, fail, "203.0.113.7", DateTimeOffset.Now, new(ok, ok, ok));
Check(!partial.Healthy && !partial.NeedsRecovery, "STUN outage shows warning without restart storm");
Check((partial with { Dns = fail }).NeedsRecovery, "Tunnel DNS failure triggers recovery");
using (var controller = new TunnelController())
{
    await controller.DisconnectAsync();
    var method = typeof(TunnelController).GetMethod("NetworkChangedAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    await (Task)method.Invoke(controller, new object[] { "test wake" })!;
    Check(controller.State == TunnelState.Disconnected && !controller.ConnectionRequested, "Wake/network change does not reconnect after manual stop");
}
FeatureChecks.Run(Check, Reject);
NotificationChecks.Run(Check);
await SecurityChecks.Run(Check, Reject);
await LifecycleChecks.Run(Check);
await UpgradeChecks.Run(Check, Reject);
await RemainingChecks.Run(Check);
await PrivacyChecks.Run(Check, Reject);
await UpdateFeedChecks.Run(Check, Reject);
await NativeChecks.CheckJob(Check);
if (args.Contains("--broker-smoke")) await NativeChecks.CheckBroker(Check);
if (args.Length == 2 && args[0] == "--native") await NativeChecks.Run(Check, args[1]);
if (args.Length == 2 && args[0] == "--validate-package")
{
    string root = Path.Combine(Path.GetTempPath(), "WorkTunnel-package-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var plan = UpdateInstaller.Prepare(args[1], UpdateInstaller.Hash(args[1]), Path.Combine(root, "target"), false, root);
        await UpdateInstaller.ValidateCoresAsync(plan, profile);
        Check(true, "Released app-and-engine package validates both generated configurations");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
if (args.Length == 2 && args[0] == "--validate-signed")
{
    string root = Path.Combine(Path.GetTempPath(), "WorkTunnel-signed-package-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var plan = UpdateInstaller.PrepareSigned(args[1], Path.Combine(root, "target"), false, root);
        await UpdateInstaller.ValidateCoresAsync(plan, profile);
        Check(true, "Production signature and bundled engines validate as a matching release");
        using var archive = System.IO.Compression.ZipFile.OpenRead(Path.Combine(Directory.GetDirectories(root, "signed-*")[0], "package.zip"));
        Check(!archive.Entries.Any(e => e.FullName.Equals("configs/profile.json", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".key") || e.FullName.EndsWith(".dpapi")), "Signed release contains no enrolled profile or key files");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
if (args.Contains("--quality"))
{
    if (args.Length != 2 || args[0] != "--quality" || !IPAddress.TryParse(args[1], out _))
        throw new ArgumentException("Use --quality <expected-exit-IP> for an explicitly selected live server.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
    var quality = await QualityProbe.RunAsync(args[1], null, timeout.Token);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(quality));
}
if (args.Contains("--live"))
{
    if (args.Length != 2 || args[0] != "--live" || !IPAddress.TryParse(args[1], out _))
        throw new ArgumentException("Use --live <expected-exit-IP> for an explicitly selected live server.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var health = await new TunnelHealthChecker().CheckAsync(args[1], timeout.Token);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(health));
    Check(health.Healthy, "Live adapter, exit IP, DNS and UDP checks");
}
Console.WriteLine($"{passed} checks passed.");
