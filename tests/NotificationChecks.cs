using WorkTunnel;

internal static class NotificationChecks
{
    public static void Run(Action<bool, string> check)
    {
        var policy = new TrayNotificationPolicy();
        NoticeKind? Observe(TunnelState state, bool verified = false, string message = "Fixture") =>
            policy.Observe(new(state, message, verified: verified));

        check(Observe(TunnelState.Connecting) == null && Observe(TunnelState.Connected) == null,
            "Connection notifications wait for successful verification");
        check(Observe(TunnelState.Connected, true) == NoticeKind.Info,
            "Initial verified connection is announced once");
        check(Observe(TunnelState.Connected, true, "Updated status") == null,
            "Updated connected status does not repeat the connection alert");
        bool silent = true;
        for (int i = 0; i < 20; i++)
            silent &= Observe(TunnelState.Connected, false, "UDP is unverified; voice may be affected.") == null &&
                Observe(TunnelState.Connected, true, "Connected; sampled routes verified") == null;
        check(silent, "Transient UDP check failures and recoveries do not generate new connection alerts");
        check(Observe(TunnelState.Connected, false, "Evidence expired") == null &&
            Observe(TunnelState.Connected, true) == null,
            "Fresh verification of an existing connection stays silent");
        check(Observe(TunnelState.Connecting, message: "Reconnecting") == null &&
            Observe(TunnelState.Connected) == null && Observe(TunnelState.Connected, true) == NoticeKind.Info &&
            Observe(TunnelState.Connected, true, "Reconnected") == null,
            "Actual recovery is announced once after verification");
        check(Observe(TunnelState.Faulted) == NoticeKind.Error &&
            Observe(TunnelState.Faulted, message: "Updated fault details") == null,
            "A fault alerts once while repeated fault details stay silent");
        check(Observe(TunnelState.Connected) == null && Observe(TunnelState.Connected, true) == NoticeKind.Info,
            "Recovery from a host fault receives a verified connection alert");
        check(Observe(TunnelState.Disconnected) == null && Observe(TunnelState.Connecting) == null &&
            Observe(TunnelState.Connected, true) == NoticeKind.Info,
            "A new connection after deliberate Disconnect can be announced");

        var attached = new TrayNotificationPolicy();
        check(attached.Observe(new(TunnelState.Connected, "Existing tunnel", verified: true)) == NoticeKind.Info &&
            attached.Observe(new(TunnelState.Connected, "Existing tunnel", verified: true)) == null,
            "Attaching a dashboard to an existing verified tunnel announces it once");
    }
}
