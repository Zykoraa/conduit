using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkTunnel;

internal static class UpgradeChecks
{
    public static async Task Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var good = new HealthCheck(true, "ok"); var bad = new HealthCheck(false, "failed");
        var health = new TunnelHealth(good, good, good, good, "203.0.113.7", DateTimeOffset.UtcNow, new(good, good, good));
        check(!(health with { Routes = null }).Healthy, "Old or missing route results cannot claim verification");
        check((health with { Routes = new(good, bad, good) }).NeedsRecovery, "IPv6 route bypass triggers recovery");
        check((health with { Routes = new(good, good, bad) }).NeedsRecovery, "Ordinary app exit failure cannot be hidden by successful bound probes");
        check(RouteProbe.Evaluate([4, 4], [4], false).Ok, "Both sampled IPv4 routes must use the tunnel");
        check(!RouteProbe.Evaluate([4, 7], [4], false).Ok, "One competing route is reported as bypass");
        check(RouteProbe.Evaluate([null, null], [4], true).Ok && !RouteProbe.Evaluate([null, null], [4], false).Ok, "No IPv6 route is distinguished from missing IPv4 connectivity");
        check(RecoveryPolicy.Step(RecoveryCause.ProxyExited, 1, health).Action == RecoveryAction.Proxy, "Proxy crash initially preserves the TUN");
        check(RecoveryPolicy.Step(RecoveryCause.TunnelExited, 1, health).Action == RecoveryAction.Tunnel, "TUN crash initially preserves the proxy");
        check(RecoveryPolicy.Step(RecoveryCause.HealthFailed, 1, health with { Dns = bad }).Action == RecoveryAction.Tunnel, "DNS failure first restarts only the adapter");
        check(RecoveryPolicy.Step(RecoveryCause.ProxyExited, 2, health).Action == RecoveryAction.Both, "Failed targeted restart escalates to both engines");
        var policy = new RecoveryPolicy(); var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 5; i++) check(policy.TryReserve(now, false), $"Automatic restart budget allows attempt {i + 1}");
        check(!policy.TryReserve(now, false) && !policy.TryReserve(now.AddMinutes(4), false), "Restart storm is throttled across recovery cycles");
        check(policy.TryReserve(now.AddMinutes(5), false) && !policy.TryReserve(now.AddMinutes(5), false), "Limited recovery allows only one retry per five minutes");
        check(policy.TryReserve(now, true), "Explicit reconnect remains available during automatic backoff");
        await TargetedLifecycle(check, health);
        var worstNames = new JsonArray(Enumerable.Range(1, 30).Select(i => (JsonNode)new JsonObject
        {
            ["metadata"] = new JsonObject { ["destinationIP"] = "2001:4860:4860::" + i, ["host"] = new string('\u4e00', 300), ["network"] = "tcp" },
            ["upload"] = 100L, ["download"] = 200L
        }).ToArray());
        var bounded = ConnectionTelemetry.Parse(new JsonObject { ["connections"] = worstNames }.ToJsonString(), "203.0.113.7", new Dictionary<string, long>());
        var envelope = new BrokerSnapshot(TunnelState.Connected, new string('m', 512), "203.0.113.7", true, health, true, true, true, 1, 2, Connections: bounded);
        using (var wire = new MemoryStream())
        {
            await BrokerWire.WriteAsync(wire, envelope, CancellationToken.None);
            check(bounded.Destinations.Length == 24 && wire.Length < 16388, "Unicode destination names fit the bounded broker envelope");
        }

        string root = Path.Combine(Path.GetTempPath(), "Conduit-upgrade-" + Guid.NewGuid().ToString("N"));
        SecretStore.RestrictDirectory(root);
        try
        {
            string preferences = Path.Combine(root, "preferences.json");
            check(!Preferences.Read(preferences).NetworkLock, "A new user's network lock stays opt-in");
            File.WriteAllText(preferences, "{\"autoReconnect\":false}");
            Preferences.Write(preferences, Preferences.Read(preferences) with { NetworkLock = true });
            var saved = Preferences.Read(preferences);
            check(saved.NetworkLock && !saved.AutoReconnect, "Saving the lock preserves legacy recovery preference and survives reload");
            Preferences.Write(preferences, saved with { AutoReconnect = true });
            check(Preferences.Read(preferences).NetworkLock, "Changing recovery cannot erase the remembered lock");
            var profiles = new List<Profile>();
            foreach (string protocol in new[] { "vless", "vmess", "trojan", "shadowsocks" })
            {
                var p = Fixture(protocol);
                string json = new JsonObject { ["outbounds"] = new JsonArray(p.Imported!.Outbound(p)), ["routing"] = new JsonObject { ["domainStrategy"] = "AsIs" } }.ToJsonString();
                var imported = V2rayImport.Parse(json); profiles.Add(imported);
                check(imported.IsValid && imported.Imported!.Protocol == protocol && imported.Imported.Credential == p.Imported.Credential, $"Import {protocol} preserves its connection credential");
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(imported);
                var protectedBytes = SecretStore.Protect(bytes);
                check(!System.Text.Encoding.UTF8.GetString(protectedBytes).Contains(p.Imported.Credential), $"Imported {protocol} credential is encrypted at rest");
                reject(() => DeviceLink.Create(imported), $"Imported {protocol} cannot export a misleading REALITY-only link");
            }
            foreach (string network in new[] { "ws", "grpc" })
            {
                var p = Fixture("vless"); p.Imported!.Transport = network; p.Imported.Path = "/socket"; p.Imported.Host = "example.com"; p.Imported.ServiceName = "tunnel";
                var imported = V2rayImport.Parse(new JsonObject { ["outbounds"] = new JsonArray(p.Imported.Outbound(p)) }.ToJsonString()); profiles.Add(imported);
                check(imported.Imported!.Transport == network && (network != "ws" || imported.Imported.Path == "/socket"), $"Import preserves {network} transport settings");
            }
            var reality = Fixture("vless"); reality.Imported!.Security = "reality"; reality.Imported.Flow = "xtls-rprx-vision";
            reality.PublicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"; reality.ShortId = "aabb";
            var outbound = reality.Imported.Outbound(reality);
            outbound["streamSettings"]!["realitySettings"]!["mldsa65Verify"] = "";
            var config = new JsonObject { ["outbounds"] = new JsonArray(outbound) };
            var parsed = V2rayImport.Parse(config.ToJsonString()); profiles.Add(parsed);
            check(parsed.Imported!.Security == "reality" && parsed.PublicKey == reality.PublicKey, "REALITY import accepts empty optional generated fields");
            outbound["streamSettings"]!["realitySettings"]!["mldsa65Verify"] = "unsupported-custom-key";
            reject(() => V2rayImport.Parse(config.ToJsonString()), "Nonempty unsupported security settings cannot silently disappear");
            outbound["streamSettings"]!["realitySettings"]!.AsObject().Remove("mldsa65Verify");
            outbound["streamSettings"]!["sockopt"] = new JsonObject { ["dialerProxy"] = "chain" };
            reject(() => V2rayImport.Parse(config.ToJsonString()), "Proxy chains cannot be silently imported as a single server");
            outbound["streamSettings"]!.AsObject().Remove("sockopt");
            outbound["streamSettings"]!["network"] = "xhttp";
            reject(() => V2rayImport.Parse(config.ToJsonString()), "Unsupported transport is rejected instead of falling back to TCP");
            var tls = Fixture("trojan").Imported!.Outbound(Fixture("trojan"));
            tls["streamSettings"]!["tlsSettings"]!["allowInsecure"] = true;
            reject(() => V2rayImport.Parse(new JsonObject { ["outbounds"] = new JsonArray(tls) }.ToJsonString()), "Import refuses disabled TLS verification");
            var hostname = Fixture("vless"); hostname.Server = "vps.example.com"; hostname.Imported!.ResolvedAddress = "203.0.113.20"; profiles.Add(hostname);
            check(hostname.IsValid, "Hostname endpoints remain valid encrypted profile data");
            var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
            string template = File.ReadAllText(Path.Combine(repo, "configs", "xray.template.json")), tun = File.ReadAllText(Path.Combine(repo, "configs", "singbox-tun.json"));
            foreach (var p in profiles) await ValidateEngines(p, template, tun, repo, root);
            check(true, "Bundled engines accept all imported protocol and transport fixtures, hostname route and IPv6 TUN");
            var jsonTun = JsonNode.Parse(TunnelController.BuildTunConfig(hostname, tun))!;
            check(jsonTun["route"]!["rules"]!.AsArray().Any(r => r?["ip_cidr"]?[0]?.GetValue<string>() == "203.0.113.20/32"), "Hostname TUN bypass uses its resolved address");
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "v2rayN", "binConfigs", "config.json");
            if (File.Exists(local))
            {
                var imported = V2rayImport.Read(local);
                if (ImportedProxy.IsV4(imported.Server))
                { await ValidateEngines(imported, template, tun, repo, root); check(true, "Existing local v2rayN selected server imports and validates without changing either app"); }
            }
            await CheckTelemetry(repo, root, check);
        }
        finally { Directory.Delete(root, true); }

    }
    private static Profile Fixture(string protocol) => new() { Server = "203.0.113.7", User = "Test", Sni = "example.com", Imported = new()
        { Protocol = protocol, Security = protocol == "shadowsocks" ? "none" : "tls", Credential = protocol is "vless" or "vmess" ? "11111111-1111-4111-8111-111111111111" : "example-test-password", Method = protocol == "shadowsocks" ? "aes-128-gcm" : "auto" } };
    private static async Task ValidateEngines(Profile p, string template, string tun, string repo, string root)
    {
        string x = Path.Combine(root, "xray.json"), t = Path.Combine(root, "tun.json");
        File.WriteAllText(x, TunnelController.BuildXrayConfig(p, template)); File.WriteAllText(t, TunnelController.BuildTunConfig(p, tun));
        foreach (var (exe, args) in new[] { ("cores/xray/xray.exe", new[] { "run", "-test", "-c", x }), ("cores/sing_box/sing-box.exe", new[] { "check", "-c", t }) })
        {
            using var process = new Process { StartInfo = new(Path.Combine(repo, exe)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (string arg in args) process.StartInfo.ArgumentList.Add(arg);
            process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(10000);
            try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
            await stdout; await stderr;
            if (process.ExitCode != 0) throw new Exception("Engine rejected import fixture: " + p.Imported?.Protocol + "/" + p.Imported?.Transport);
        }
    }
    private static async Task TargetedLifecycle(Action<bool, string> check, TunnelHealth health)
    {
        var profile = new Profile { Uuid = Guid.NewGuid().ToString(), Server = "203.0.113.7" };
        var actions = new List<RecoveryAction>(); int starts = 0, stops = 0;
        var platform = new ControllerPlatform(_ => { starts++; return Task.FromResult(true); }, () => stops++, _ => Task.FromResult(health), _ => Task.CompletedTask,
            (action, _) => { actions.Add(action); return Task.FromResult(true); });
        using var controller = new TunnelController(() => profile, platform: platform);
        await controller.ConnectAsync();
        var heal = typeof(TunnelController).GetMethod("HealAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)heal.Invoke(controller, ["test proxy crash", false, RecoveryCause.ProxyExited])!;
        check(starts == 1 && stops == 0 && actions.SequenceEqual([RecoveryAction.Proxy]) && controller.Verified, "Controller performs targeted proxy recovery and re-verifies without full teardown");
        await controller.DisconnectAsync(); actions.Clear();
        await (Task)heal.Invoke(controller, ["late crash", false, RecoveryCause.ProxyExited])!;
        check(actions.Count == 0 && controller.State == TunnelState.Disconnected, "Late recovery after Disconnect cannot restart engines");
    }
    private static async Task CheckTelemetry(string repo, string root, Action<bool, string> check)
    {
        using var telemetry = new ConnectionTelemetry();
        using var target = new TcpListener(IPAddress.Loopback, 0); target.Start(); int targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start(); int socksPort = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        var config = new JsonObject { ["inbounds"] = new JsonArray(new JsonObject { ["type"] = "socks", ["listen"] = "127.0.0.1", ["listen_port"] = socksPort }), ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" }), ["route"] = new JsonObject { ["final"] = "direct" } };
        telemetry.Configure(config);
        string path = Path.Combine(root, "telemetry.json"); File.WriteAllText(path, config.ToJsonString());
        using var process = new Process { StartInfo = new(Path.Combine(repo, "cores/sing_box/sing-box.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (string arg in new[] { "run", "-c", path }) process.StartInfo.ArgumentList.Add(arg);
        process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(10000);
        try
        {
            using var socket = new TcpClient();
            for (int attempt = 0; ; attempt++)
            {
                try { await socket.ConnectAsync(IPAddress.Loopback, socksPort, timeout.Token); break; }
                catch (SocketException) when (attempt < 25) { await Task.Delay(100, timeout.Token); }
            }
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            using var denied = await http.GetAsync($"http://127.0.0.1:{telemetry.Port}/connections", timeout.Token);
            check(denied.StatusCode == HttpStatusCode.Unauthorized, "Live destination API rejects requests without the session secret");
            var stream = socket.GetStream(); await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token); var handshake = new byte[2]; await stream.ReadExactlyAsync(handshake, timeout.Token);
            byte[] connect = [5, 1, 0, 1, 127, 0, 0, 1, (byte)(targetPort >> 8), (byte)targetPort]; await stream.WriteAsync(connect, timeout.Token);
            var reply = new byte[10]; await stream.ReadExactlyAsync(reply, timeout.Token);
            using var peer = await target.AcceptTcpClientAsync(timeout.Token);
            var first = await telemetry.ReadAsync("203.0.113.7", timeout.Token);
            check(first.Destinations.Any(d => d.Address == "127.0.0.1"), "Bundled sing-box reports actual local test connection destinations without a TUN or route changes");
            await stream.WriteAsync(new byte[500], timeout.Token); var data = new byte[500]; await peer.GetStream().ReadExactlyAsync(data, timeout.Token);
            var second = await telemetry.ReadAsync("203.0.113.7", timeout.Token);
            check(second.Destinations.Any(d => d.Active && d.Uploaded >= 500), "Live activity derives from measured connection-byte deltas");
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } await stdout; await stderr; }
    }
}
