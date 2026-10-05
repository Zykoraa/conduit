using System.Reflection;
using System.Text.Json;
using WorkTunnel;

internal static class RemainingChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var window = new NetworkRecoveryWindow();
        check(!window.Ready(false, 0, now) && !window.Ready(false, 0, now.AddHours(1)), "Missing link never spends time toward the recovery grace period");
        check(!window.Ready(true, 1, now) && !window.Ready(true, 1, now.AddSeconds(14)) && window.Ready(true, 1, now.AddSeconds(15)), "Returned link receives a full fifteen-second grace period");
        check(!window.Ready(true, 2, now.AddSeconds(16)) && !window.Ready(true, 2, now.AddSeconds(30)) && window.Ready(true, 2, now.AddSeconds(31)), "Another network change restarts the stability period");
        var unknown = new NetworkRecoveryWindow();
        check(!unknown.Ready(null, 0, now) && unknown.Ready(null, 0, now.AddSeconds(15)), "Unknown link state allows verification after grace instead of waiting forever");
        var good = new HealthCheck(true, "ok"); var bad = new HealthCheck(false, "DNS did not answer");
        var healthy = new TunnelHealth(good, good, good, good, "203.0.113.7", now, new(good, good, good));
        var health = healthy;
        var profile = new Profile { Uuid = Guid.NewGuid().ToString(), Server = "203.0.113.7" };
        int starts = 0, stops = 0;
        var actions = new List<RecoveryAction>();
        var platform = new ControllerPlatform(_ => { starts++; return Task.FromResult(true); }, () => stops++, _ => Task.FromResult(health),
            delay => { now += delay; return Task.CompletedTask; },
            (action, _) => { actions.Add(action); health = healthy; return Task.FromResult(true); }, () => true, () => now);
        Task Change(TunnelController c) => (Task)typeof(TunnelController).GetMethod("NetworkChangedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(c, ["Test network change"])!;
        using (var c = new TunnelController(() => profile, platform: platform))
        {
            await c.ConnectAsync();
            var before = now; await Change(c);
            check(starts == 1 && stops == 0 && actions.Count == 0 && c.Verified && now - before >= RecoveryPolicy.NetworkGrace,
                "Healthy tunnel survives network grace without engine teardown or budget use");
            health = healthy with { Dns = bad }; before = now;
            await Change(c);
            check(actions.SequenceEqual([RecoveryAction.Tunnel]) && stops == 0 && c.Verified && now - before >= RecoveryPolicy.NetworkGrace + RecoveryPolicy.AdapterGrace,
                "Failed self-recovery targets the adapter and waits for routes before escalating");
        }
        foreach (string interrupt in new[] { "disconnect", "manual", "disable" })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var forever = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int restarts = 0;
            using var c = new TunnelController(() => profile, platform: platform with
            {
                LinkAvailable = () => false,
                Delay = delay => { if (delay == TimeSpan.Zero) return Task.CompletedTask; entered.TrySetResult(); return forever.Task; },
                Restart = (_, _) => { restarts++; return Task.FromResult(true); }
            });
            await c.ConnectAsync(); var pending = Change(c); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(restarts == 0 && !c.Verified && c.State == TunnelState.Connecting, "Link-down wait preserves engines and clears old verification: " + interrupt);
            if (interrupt == "disconnect") await c.DisconnectAsync();
            else if (interrupt == "manual") await c.ReconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
            else c.AutoHeal = false;
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
            check(interrupt == "manual" ? restarts == 1 && c.Verified : restarts == 0 && !c.Verified,
                "Network wait is interrupted safely by " + interrupt);
        }

        foreach (string interrupt in new[] { "disconnect", "manual", "disable" })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var forever = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int restarts = 0;
            health = healthy;
            using var c = new TunnelController(() => profile, platform: platform with
            {
                Delay = delay => { if (delay == RecoveryPolicy.AdapterGrace) { entered.TrySetResult(); return forever.Task; } now += delay; return Task.CompletedTask; },
                Restart = (_, _) => { restarts++; health = healthy; return Task.FromResult(true); }
            });
            await c.ConnectAsync(); health = healthy with { Dns = bad };
            var pending = Change(c); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (interrupt == "disconnect") await c.DisconnectAsync();
            else if (interrupt == "manual") await c.ReconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
            else c.AutoHeal = false;
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
            check(interrupt == "manual" ? restarts == 2 && c.Verified : restarts == 1 && !c.Verified && c.State == (interrupt == "disable" ? TunnelState.Faulted : TunnelState.Disconnected),
                "Adapter settling can be interrupted safely by " + interrupt);
        }

        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Conduit-incidents-" + Guid.NewGuid().ToString("N")));
        SecretStore.RestrictDirectory(root);
        try
        {
            string reports = Path.Combine(root, "reports"), log = Path.Combine(root, "core.log");
            File.WriteAllText(log, "error dial private.example.com [2001:db8::10] password=demo-credential failed\ninfo ordinary browsing.example.com\n");
            var recorder = new IncidentRecorder(reports, () => ["demo-credential"], () => "{\"Adapters\":[{\"Type\":\"Wireless80211\",\"Status\":\"Up\"}]}", false, [log]);
            recorder.Observe(TunnelState.Connected, "verified", healthy, true, false, true, now);
            recorder.Observe(TunnelState.Connecting, "recovering demo-credential", null, true, true, true, now.AddSeconds(1));
            await recorder.FlushAsync();
            string folder = Directory.GetDirectories(reports, "incident-*").Single();
            check(File.Exists(Path.Combine(folder, "last-healthy.json")) && File.Exists(Path.Combine(folder, "network.json")) && File.Exists(Path.Combine(folder, "events.jsonl")),
                "Incident captures failure, last verified state, network evidence and recent events");
            using var last = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "last-healthy.json")));
            check(last.RootElement.GetProperty("Health").GetProperty("Healthy").GetBoolean(), "Incident preserves the actual earlier successful health snapshot");
            string text = string.Join("\n", Directory.GetFiles(folder).Select(File.ReadAllText));
            check(!text.Contains("demo-credential") && !text.Contains("203.0.113.7") && !text.Contains("2001:db8::10") && !text.Contains("private.example.com") && !text.Contains("browsing.example.com"),
                "Incident strips credentials, IPv4, IPv6 and log destination names");
            recorder.Observe(TunnelState.Faulted, "another error", null, true, true, true, now.AddSeconds(2));
            recorder.Observe(TunnelState.Disconnected, "user disconnected", null, false, false, false, now.AddMinutes(6));
            await recorder.FlushAsync();
            check(Directory.GetDirectories(reports, "incident-*").Length == 1, "Repeated failures coalesce and deliberate Disconnect creates no incident");
            for (int i = 1; i <= 22; i++) recorder.Observe(TunnelState.Faulted, "failure " + i, null, true, true, true, now.AddMinutes(i * 6));
            await recorder.FlushAsync();
            check(Directory.GetDirectories(reports, "incident-*").Length == IncidentRecorder.Keep && !Directory.Exists(folder), "Incident retention keeps only the twenty newest owned folders");
            var export = IncidentRecorder.ReadRecent(reports);
            check(export.Select(r => r.Name.Split('/')[1]).Distinct().Count() == 5, "Diagnostic export includes the latest five incident reports");
            foreach (var file in export.Where(r => r.Name.EndsWith(".json"))) using (JsonDocument.Parse(file.Contents)) { }
            check(recorder.Error == null, "Persisted incident JSON remains valid after redaction and retention");
            var broken = new IncidentRecorder(Path.Combine(root, "broken"), () => [], () => throw new IOException("unavailable"), false, []);
            broken.Observe(TunnelState.Faulted, "failure", null, true, false, false, now);
            await broken.FlushAsync();
            check(broken.Error != null, "Diagnostic capture failure is contained instead of breaking recovery");
            string corrupt = Path.Combine(root, "corrupt"); Directory.CreateDirectory(corrupt);
            File.WriteAllText(Path.Combine(corrupt, "last-healthy.json"), "damaged JSON");
            var repaired = new IncidentRecorder(corrupt, () => [], () => "{}", false, []);
            repaired.Observe(TunnelState.Faulted, "failure", null, true, false, false, now);
            await repaired.FlushAsync();
            check(repaired.Error == null && File.Exists(Path.Combine(Directory.GetDirectories(corrupt, "incident-*").Single(), "summary.txt")),
                "A damaged saved healthy snapshot cannot prevent new incident reports");
        }
        finally
        {
            if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "Conduit-incidents-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(root, true);
        }
    }
}
