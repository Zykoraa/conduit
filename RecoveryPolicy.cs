namespace WorkTunnel;

internal enum RecoveryCause { ProxyExited, TunnelExited, HealthFailed, NetworkChanged, Manual }
internal enum RecoveryAction { Proxy, Tunnel, Both }
internal sealed record RecoveryStep(RecoveryAction Action, TimeSpan Delay);

/// <summary>Decisions only. The controller owns processes, cancellation and the clock.</summary>
internal sealed class RecoveryPolicy
{
    public const int AttemptsPerCycle = 3;
    public static readonly TimeSpan SlowRetry = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan NetworkGrace = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan AdapterGrace = TimeSpan.FromSeconds(20);
    private readonly Queue<DateTimeOffset> _restarts = new();
    private DateTimeOffset? _limitedUntil;

    public static RecoveryStep Step(RecoveryCause cause, int attempt, TunnelHealth? health)
    {
        var action = attempt > 1 ? RecoveryAction.Both : cause switch
        {
            RecoveryCause.ProxyExited => RecoveryAction.Proxy,
            RecoveryCause.TunnelExited => RecoveryAction.Tunnel,
            RecoveryCause.NetworkChanged => RecoveryAction.Tunnel,
            RecoveryCause.HealthFailed when health?.Adapter.Ok == false || health?.Routes?.Ok == false || health?.Dns.Ok == false => RecoveryAction.Tunnel,
            _ => RecoveryAction.Both
        };
        return new(action, TimeSpan.FromSeconds(cause == RecoveryCause.Manual ? 0 : attempt == 1 ? 1 : 2 + attempt));
    }
    public bool TryReserve(DateTimeOffset now, bool manual)
    {
        while (_restarts.TryPeek(out var at) && now - at >= TimeSpan.FromMinutes(10)) _restarts.Dequeue();
        // An explicit Reconnect gets a bounded cycle even during automatic backoff.
        if (!manual && _restarts.Count >= 5)
        {
            if (_limitedUntil is null) { _limitedUntil = now + SlowRetry; return false; }
            if (now < _limitedUntil) return false;
            _limitedUntil = now + SlowRetry;
        }
        _restarts.Enqueue(now);
        return true;
    }
}

/// <summary>A returned link must stay stable before recovery touches the engines.</summary>
internal sealed class NetworkRecoveryWindow
{
    private DateTimeOffset? _stableSince;
    private long _revision = -1;
    public bool Ready(bool? linkAvailable, long revision, DateTimeOffset now)
    {
        if (_revision != revision || linkAvailable == false) _stableSince = null;
        _revision = revision;
        if (linkAvailable == false) return false;
        _stableSince ??= now;
        return now - _stableSince >= RecoveryPolicy.NetworkGrace;
    }
}
