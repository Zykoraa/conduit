#if OWNER_BUILD
namespace WorkTunnel;

/// <summary>
/// Owner-only one-click cover switch with the auto-revert safety net. Because a
/// switch restarts xray (dropping the tunnel this SSH rides), the server arms a
/// 90s auto-revert; if the new cover doesn't carry traffic, the server falls
/// back on its own and the client follows — so a bad switch can't strand you.
/// </summary>
internal sealed class CoverSwitcher(ITunnelController ctrl)
{
    private readonly ExitIpChecker _checker = new();

    public event Action<string>? Progress;

    public async Task<(bool ok, string message)> SwitchAsync(CoverSite target)
    {
        if (!Ssh.KeyPresent) return (false, "SSH key not found — cannot switch cover.");

        var profile = Profile.Load();
        if (profile.Imported != null) return (false, "Cover switching is unavailable for an imported v2rayN server.");
        var prevSni = profile.Sni;
        var prevSite = CoverProfiles.BySni(prevSni);
        var server = profile.Server;
        if (string.Equals(prevSni, target.Sni, StringComparison.OrdinalIgnoreCase))
            return (true, $"Already on {target.Name}.");

        // 1) Flip the server with a 90s auto-revert. This restarts xray and drops
        //    this very SSH, so a non-zero result here is expected, not a failure.
        Progress?.Invoke($"Switching server to {target.Name} (auto-revert armed)…");
        await Ssh.RunAsync($"set-cover.sh {target.Key} --revert 90", timeoutMs: 20000);

        // 2) Point the client at the new SNI and reconnect.
        profile.Sni = target.Sni; profile.Save();
        Progress?.Invoke($"Reconnecting with {target.Sni}…");
        await ctrl.DisconnectAsync();
        await Task.Delay(2000); // let the server finish restarting xray
        await ctrl.ConnectAsync();

        // 3) Verify the new cover actually reaches the VPS, inside the revert window.
        Progress?.Invoke("Verifying new cover…");
        if (await PollExitAsync(server, TimeSpan.FromSeconds(45)))
        {
            await Ssh.RunAsync("confirm-cover.sh", timeoutMs: 20000);
            return (true, $"Switched to {target.Name} ({target.Sni}) — working, auto-revert cancelled.");
        }

        // 4) It didn't carry traffic: the server auto-reverts to the previous cover
        //    at ~90s. Put the client back on the old SNI and reconnect once it's back.
        Progress?.Invoke($"{target.Name} didn't work — waiting for auto-revert to {prevSite?.Name ?? prevSni}…");
        profile.Sni = prevSni; profile.Save();
        await Task.Delay(TimeSpan.FromSeconds(50)); // cross the 90s revert mark
        await ctrl.DisconnectAsync();
        await ctrl.ConnectAsync();
        bool back = await PollExitAsync(server, TimeSpan.FromSeconds(40));
        return (false, back
            ? $"{target.Name} didn't carry traffic — safely reverted to {prevSite?.Name ?? prevSni}."
            : $"{target.Name} failed; the auto-revert is still settling. If it doesn't recover shortly, reconnect manually.");
    }

    private async Task<bool> PollExitAsync(string server, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            if (await _checker.GetExitIpAsync() == server) return true;
            await Task.Delay(3000);
        }
        return false;
    }
}
#endif
