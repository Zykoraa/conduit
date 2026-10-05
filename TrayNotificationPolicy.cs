namespace WorkTunnel;

/// <summary>Connection alerts follow lifecycle transitions, independently of probe results.</summary>
internal sealed class TrayNotificationPolicy
{
    private bool _connectionAnnounced;
    private TunnelState _previousState = TunnelState.Disconnected;

    public NoticeKind? Observe(StatusEventArgs status)
    {
        if (status.State != TunnelState.Connected) _connectionAnnounced = false;

        NoticeKind? notice = null;
        if (status.State == TunnelState.Connected && status.Verified && !_connectionAnnounced)
        {
            _connectionAnnounced = true;
            notice = NoticeKind.Info;
        }
        else if (status.State == TunnelState.Faulted && _previousState != TunnelState.Faulted)
            notice = NoticeKind.Error;

        _previousState = status.State;
        return notice;
    }
}
