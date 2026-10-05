using System.Reflection;
using WorkTunnel;

internal static class LifecycleChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var profile = new Profile { Uuid = Guid.NewGuid().ToString(), Sni = "www.apple.com", Server = "203.0.113.7" };
        var good = new HealthCheck(true, "ok"); var bad = new HealthCheck(false, "failed");
        var health = new TunnelHealth(good, good, good, good, profile.Server, DateTimeOffset.Now, new(good, good, good));
        int starts = 0; bool startWorks = true;
        var now = DateTimeOffset.UtcNow;
        var platform = new ControllerPlatform(_ => { starts++; return Task.FromResult(startWorks); }, () => { }, _ => Task.FromResult(health), delay => { now += delay; return Task.CompletedTask; }, Now: () => now);
        using var controller = new TunnelController(() => profile, platform: platform);
        await controller.ConnectAsync(); check(controller.Verified && starts == 1, "Initial connection verifies before reporting healthy");
        Task Wake() => (Task)typeof(TunnelController).GetMethod("NetworkChangedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, ["test wake"])!;
        await Wake(); check(controller.Verified && starts == 1, "Wake verifies the existing tunnel without an unnecessary restart");
        startWorks = false; await controller.ReconnectAsync();
        check(starts == 4 && controller.State == TunnelState.Faulted, "Engine failure stops after exactly three recovery attempts");
        await controller.DisconnectAsync(); await Wake(); check(starts == 4 && controller.State == TunnelState.Disconnected, "Manual disconnect prevents wake from reconnecting");
        startWorks = true; health = health with { Dns = bad }; await controller.ConnectAsync();
        await controller.ReconnectAsync();
        check(!controller.Verified && controller.State == TunnelState.Faulted && starts == 8, "DNS failure cannot falsely complete recovery");
        await controller.DisconnectAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = platform with { Start = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return true; } };
        using var cancelable = new TunnelController(() => profile, platform: waiting);
        var connecting = cancelable.ConnectAsync(); await entered.Task;
        await cancelable.DisconnectAsync(); await connecting;
        check(cancelable.State == TunnelState.Disconnected && !cancelable.ConnectionRequested, "Disconnect cancels an in-flight connection without resurrecting it");
        var late = new TaskCompletionSource<TunnelHealth>(TaskCreationOptions.RunContinuationsAsynchronously);
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stale = new TunnelController(() => profile, platform: platform with { Check = _ => { entered.TrySetResult(); return late.Task; } });
        var pending = stale.ConnectAsync(); await entered.Task;
        var stopping = stale.DisconnectAsync(); late.SetResult(health with { Dns = good });
        await Task.WhenAll(pending, stopping);
        check(!stale.Verified && stale.Health == null && stale.State == TunnelState.Disconnected, "A late successful probe cannot overwrite manual disconnect");
    }
}
