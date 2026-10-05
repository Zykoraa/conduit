using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Windows.Win32;
using Windows.Win32.NetworkManagement.WindowsFilteringPlatform;
using WorkTunnel;

internal static class PrivacyChecks
{
    internal static byte[] Answer(byte[] query, byte[]? name = null)
    {
        var packet = query.ToList(); packet[2] = 0x81; packet[3] = 0x80; packet[7] = 1;
        packet.AddRange(name ?? [0xc0, 0x0c]);
        packet.AddRange([0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 192, 0, 2, 1]);
        return packet.ToArray();
    }
    public static async Task Run(Action<bool, string> check, Action<Action, string> reject)
    {
        byte[] query = TunnelDns.Query("probe.example"), answer = Answer(query);
        check(TunnelDns.ParseReply(answer, query).Single().Equals(IPAddress.Parse("192.0.2.1")), "Compressed tunnel DNS answer resolves the requested hostname");
        var aliasName = TunnelDns.Query("alias.example")[12..^4];
        var cname = query.ToList(); cname[2] = 0x81; cname[3] = 0x80; cname[7] = 2;
        cname.AddRange([0xc0, 0x0c, 0, 5, 0, 1, 0, 0, 0, 60, 0, (byte)aliasName.Length]);
        int aliasOffset = cname.Count; cname.AddRange(aliasName);
        cname.AddRange([(byte)(0xc0 | (aliasOffset >> 8)), (byte)aliasOffset, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 192, 0, 2, 8]);
        check(TunnelDns.ParseReply(cname.ToArray(), query).Single().Equals(IPAddress.Parse("192.0.2.8")), "Tunnel DNS accepts an answer reached through a matching CNAME chain");
        check(!TunnelHealthChecker.ValidDnsReply(answer[..12], query), "An answer-count header without the actual answer cannot pass DNS health");
        var truncated = (byte[])answer.Clone(); truncated[2] |= 2;
        check(TunnelDns.ParseReply(truncated, query).Length == 0 && TunnelDns.ParseReply(answer[..^1], query).Length == 0, "Truncated DNS flags and truncated resource data fail closed");
        var unrelated = Answer(query, TunnelDns.Query("unrelated.example")[12..^4]);
        check(TunnelDns.ParseReply(unrelated, query).Length == 0, "Unrelated DNS records cannot satisfy the health query");
        var cycle = (byte[])answer.Clone(); cycle[query.Length] = 0xc0; cycle[query.Length + 1] = (byte)query.Length;
        check(TunnelDns.ParseReply(cycle, query).Length == 0, "DNS compression loops are rejected");
        reject(() => TunnelDns.Query("invalid..host"), "Probe DNS rejects malformed hostnames");
        check((await TunnelDns.ResolveIPv4Async("probe.example", CancellationToken.None, (q, _) => Task.FromResult(Answer(q)))).Equals(IPAddress.Parse("192.0.2.1")),
            "Tunnel resolver uses its supplied exchange with a complete matching answer");
        bool rejected = false;
        try { await TunnelDns.ResolveIPv4Async("probe.example", CancellationToken.None, (_, _) => Task.FromResult(new byte[12])); }
        catch (IOException) { rejected = true; }
        check(rejected, "Invalid tunnel DNS fails without a fallback resolver");

        var now = DateTimeOffset.UtcNow; var good = new HealthCheck(true, "ok");
        var health = new TunnelHealth(good, good, good, good, "203.0.113.7", now, new(good, good, good));
        check(HealthFreshness.IsCurrent(health, now.AddSeconds(59)) && !HealthFreshness.IsCurrent(health, now.AddSeconds(60)) &&
            !HealthFreshness.IsCurrent(health with { CheckedAt = now.AddSeconds(1) }, now), "Expired and future-dated health results cannot verify current protection");
        var snapshot = new BrokerSnapshot(TunnelState.Connected, "ok", health.ExitIp, true, health, true, true, true, 1, 2);
        check(!BrokerFacts.Verified(snapshot, now.AddMinutes(2)) && !BrokerFacts.Verified(snapshot with { Verified = false }, now), "Dashboard rejects stale or explicitly invalidated host verification");
        check(!BrokerFacts.Recent(now, now.AddSeconds(5)), "An unanswered host cannot keep a current lock indication indefinitely");
        var outside = RouteProbe.Evaluate([7, 7], [4], false);
        check(ConnectionDiagnosis.For(health with { Adapter = new(false, "down"), Routes = new(outside, good, good) }).Happening.Contains("outside Conduit"),
            "A known bypass route outranks an unavailable adapter in the diagnosis");
        await Lifecycle(check, health);
        CheckBlocks(check);
        string root = Path.Combine(Path.GetTempPath(), "Conduit-visual-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "visual.json");
            check(VisualPerformance.Read(path) == VisualLoad.Balanced, "Missing visual settings use the balanced default");
            VisualPerformance.Write(path, VisualLoad.LowPower);
            check(VisualPerformance.Read(path) == VisualLoad.LowPower, "Low-power preference survives reloading");
            File.WriteAllText(path, "{broken"); check(VisualPerformance.Read(path) == VisualLoad.Balanced, "Damaged visual settings do not break startup");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        check(VisualPerformance.SampleInterval(false, false, false, false, false) == 0 && VisualPerformance.SampleInterval(true, true, true, false, false) == 0,
            "Hidden and minimized dashboard sampling is suspended");
        check(VisualPerformance.SampleInterval(true, false, false, false, false) > VisualPerformance.SampleInterval(true, false, true, false, false),
            "Background sampling is slower than foreground sampling");
        check(VisualPerformance.SampleInterval(false, false, false, true, true) > 0, "Compact view retains bounded sampling while the dashboard is hidden");
    }
    private static async Task Lifecycle(Action<bool, string> check, TunnelHealth health)
    {
        var profile = new Profile { Uuid = Guid.NewGuid().ToString(), Sni = "www.example.com", Server = "203.0.113.7" };
        Func<CancellationToken, Task<TunnelHealth>> probe = _ => Task.FromResult(health with { CheckedAt = DateTimeOffset.UtcNow });
        var platform = new ControllerPlatform(_ => Task.FromResult(true), () => { }, ct => probe(ct), _ => Task.CompletedTask);
        using var controller = new TunnelController(() => profile, platform: platform) { AutoHeal = false };
        await controller.ConnectAsync(); check(controller.Verified, "Privacy lifecycle starts with actual fresh verification");
        Task NetworkChange() => (Task)typeof(TunnelController).GetMethod("NetworkChangedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, ["test network change"])!;
        await NetworkChange();
        check(!controller.Verified && controller.Health == null && controller.ConnectionRequested, "Network change clears verification even when automatic recovery is off");
        await controller.CheckNowAsync(); check(controller.Verified, "A new check restores verification after a network change");
        probe = _ => throw new IOException("test check failure"); await controller.CheckNowAsync();
        check(!controller.Verified && controller.Health == null, "A thrown check clears the previously successful result");
        var pending = new TaskCompletionSource<TunnelHealth>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe = _ => { entered.TrySetResult(); return pending.Task; };
        Task checking = controller.CheckNowAsync(); await entered.Task;
        await NetworkChange(); pending.SetResult(health with { CheckedAt = DateTimeOffset.UtcNow }); await checking;
        check(!controller.Verified && controller.Health == null, "A probe begun on an older network cannot restore verification");
        probe = _ => Task.FromResult(health with { CheckedAt = DateTimeOffset.UtcNow.AddMinutes(-2) }); await controller.CheckNowAsync();
        check(!controller.Verified, "Controller rejects a completed probe containing expired evidence");
        await controller.DisconnectAsync();
    }
    private static unsafe void CheckBlocks(Action<bool, string> check)
    {
        ulong weight = 10;
        Guid key = new("95a2e590-31dd-440a-91a3-000000000001");
        var filter = new FWPM_FILTER0 { filterKey = key, subLayerKey = new("d6bd188e-9957-4a4a-99c8-811c1310bc35"),
            layerKey = PInvoke.FWPM_LAYER_OUTBOUND_TRANSPORT_V4, action = new() { type = FWP_ACTION_TYPE.FWP_ACTION_BLOCK },
            flags = FWPM_FILTER_FLAGS.FWPM_FILTER_FLAG_PERSISTENT, weight = new() { type = FWP_DATA_TYPE.FWP_UINT64 } };
        filter.weight.uint64 = &weight;
        check(NetworkLock.ValidBlock(filter, key, filter.layerKey), "Lock inspection accepts the exact persistent unconditional blocking rule");
        var changed = filter; changed.action.type = FWP_ACTION_TYPE.FWP_ACTION_PERMIT;
        check(!NetworkLock.ValidBlock(changed, key, filter.layerKey), "A permit rule cannot masquerade as the required lock block");
        changed = filter; changed.numFilterConditions = 1;
        check(!NetworkLock.ValidBlock(changed, key, filter.layerKey), "A conditional block cannot masquerade as whole-layer blocking");
        changed = filter; changed.flags = 0;
        check(!NetworkLock.ValidBlock(changed, key, filter.layerKey) && !NetworkLock.ValidBlock(filter, key, PInvoke.FWPM_LAYER_OUTBOUND_TRANSPORT_V6),
            "Transient or wrong-family filters cannot confirm the lock");
    }
}
