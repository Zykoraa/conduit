namespace WorkTunnel;

internal interface ITunnelController : IDisposable
{
    event EventHandler<StatusEventArgs>? StatusChanged;
    event Action<string, NoticeKind>? Notice;
    event Action<TunnelHealth?>? HealthChanged;
    TunnelState State { get; }
    string? ExitIp { get; }
    bool Verified { get; }
    TunnelHealth? Health { get; }
    bool ConnectionRequested { get; }
    bool KillSwitch { get; set; }
    bool AutoHeal { get; set; }
    (int? Xray, int? SingBox) CoreProcessIds { get; }
    Task ConnectAsync();
    Task DisconnectAsync();
    Task ReconnectAsync();
    Task CheckNowAsync();
    Task<ConnectionSnapshot> ReadConnectionsAsync();
}
