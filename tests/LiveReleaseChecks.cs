using System.IO.Pipes;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using WorkTunnel;

internal static class LiveReleaseChecks
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint process);
    internal static async Task Feed(string address, string report, bool includeBound = true)
    {
        var results = new List<object>();
        foreach (bool bound in includeBound ? new[] { false, true } : new[] { false })
        {
            using ITunnelController controller = bound ? new BrokerClient() : new TunnelController { AutoHeal = false };
            if (controller is BrokerClient broker) await broker.EnsureHostAsync();
            if (bound && !controller.Verified) throw new InvalidOperationException("The current tunnel must have fresh verification for the bound feed check.");
            using var discovery = UpdateDiscovery.ForController(controller);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var release = await discovery.CheckAsync(new(address), deadline.Token);
            string storage = Path.Combine(Path.GetTempPath(), "Conduit-feed-check-" + Guid.NewGuid().ToString("N"));
            string? bundle = null;
            try
            {
                bundle = await discovery.DownloadAsync(release, storage, null, deadline.Token);
                using var archive = System.IO.Compression.ZipFile.OpenRead(bundle);
                using var metadata = archive.GetEntry("release.json")!.Open(); using var signature = archive.GetEntry("release.sig")!.Open();
                using var json = new MemoryStream(); using var sig = new MemoryStream(); metadata.CopyTo(json); signature.CopyTo(sig);
                var signed = ReleaseSignature.Verify(json.ToArray(), sig.ToArray(), ReleaseTrust.PublicKey);
                if (signed.Version != release.Release.Version || signed.Flavor != release.Release.Flavor) throw new InvalidDataException("Feed and bundle disagree.");
                results.Add(new { Mode = bound ? "Verified tunnel with bound DNS/TCP" : "Direct manual HTTPS transport", Version = signed.Version, Flavor = signed.Flavor, Bytes = new FileInfo(bundle).Length, HashVerified = true, BundleSignatureVerified = true });
                Console.WriteLine("PASS hosted feed and full signed download: " + (bound ? "tunnel" : "direct") + " " + signed.Version);
            }
            finally
            {
                if (bundle != null) File.Delete(bundle);
                if (Directory.Exists(storage)) Directory.Delete(storage, true);
            }
        }
        File.WriteAllText(report, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
    internal static void PrepareBenchmarkProfile(string exit)
    {
        NativeChecks.RequireDisposableRunner();
        if (!IPAddress.TryParse(exit, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new ArgumentException("Expected IPv4 exit required.");
        new Profile { User = "Disposable benchmark fixture", Server = "127.0.0.1", Port = 18443, Uuid = "00000000-0000-4000-8000-000000000123", ExpectedExitIp = exit,
            Imported = new ImportedProxy { Protocol = "vless", Transport = "tcp", Security = "none", Credential = "00000000-0000-4000-8000-000000000123" } }.Save();
        Preferences.Save(true); Preferences.SaveNetworkLock(false);
        Console.WriteLine("Disposable fixture profile prepared; no production identity used.");
    }
    internal static async Task Broker(string command)
    {
        NativeChecks.RequireDisposableRunner();
        if (command is not ("status" or "shutdown")) throw new ArgumentException("Invalid benchmark command.");
        using var pipe = new NamedPipeClientStream(".", BrokerWire.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await pipe.ConnectAsync(deadline.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint host)) throw new IOException("Host PID unavailable.");
        await BrokerWire.WriteAsync(pipe, new BrokerRequest(command), deadline.Token);
        var snapshot = await BrokerWire.ReadAsync<BrokerSnapshot>(pipe, deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { Host = host, snapshot.Xray, snapshot.SingBox, snapshot.Desired, snapshot.Verified, State = snapshot.State.ToString(), snapshot.Error }));
    }
}
