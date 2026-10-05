using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WorkTunnel;

internal enum TunnelState { Disconnected, Connecting, Connected, Faulted }

/// <summary>Severity for one-off toast notifications raised by <see cref="TunnelController.Notice"/>.</summary>
internal enum NoticeKind { Info, Warning, Error }

// Deterministic platform seam for lifecycle tests; production always uses the native path.
internal sealed record ControllerPlatform(Func<CancellationToken, Task<bool>> Start, Action Stop,
    Func<CancellationToken, Task<TunnelHealth>> Check, Func<TimeSpan, Task> Delay,
    Func<RecoveryAction, CancellationToken, Task<bool>>? Restart = null,
    Func<bool?>? LinkAvailable = null, Func<DateTimeOffset>? Now = null);

internal sealed class StatusEventArgs(TunnelState state, string message, string? exitIp = null, bool verified = false)
    : EventArgs
{
    public TunnelState State { get; } = state;
    public string Message { get; } = message;
    public string? ExitIp { get; } = exitIp;
    public bool Verified { get; } = verified;
}

/// <summary>
/// Runs and supervises the two pinned cores (xray = proxy, sing-box = TUN),
/// replicating the working v2rayN model but with configs we control so the
/// sing-box version gotcha can't recur.
///
/// Self-healing: if a core dies mid-session, or the exit IP stops verifying,
/// the controller restarts the tunnel automatically (bounded retries with
/// backoff) instead of just blocking and waiting for a manual click. Mirrors
/// the server-side watchdog on the client.
/// </summary>
internal sealed class TunnelController : ITunnelController
{
    private const int SocksPort = 10808;
    private const int MaxHealAttempts = RecoveryPolicy.AttemptsPerCycle;
    private const int VerifyFailThreshold = 3; // ~3 x 15s of a bad/unverified exit before self-heal

    public event EventHandler<StatusEventArgs>? StatusChanged;

    /// <summary>One-off notifications (toasts) for drop/reconnect moments.</summary>
    public event Action<string, NoticeKind>? Notice;

    public TunnelState State { get; private set; } = TunnelState.Disconnected;
    public string? ExitIp { get; private set; }
    private bool _verified;
    private readonly object _healthSync = new();
    private long _healthRevision;
    public bool Verified
    {
        get { lock (_healthSync) return _verified && State == TunnelState.Connected &&
            _healthRevision == Interlocked.Read(ref _networkRevision) && HealthFreshness.IsCurrent(Health, DateTimeOffset.UtcNow); }
        private set { _verified = value; }
    }
    public TunnelHealth? Health { get; private set; }
    public event Action<TunnelHealth?>? HealthChanged;
    public bool ConnectionRequested => _desired;
    public (int? Xray, int? SingBox) CoreProcessIds => (RunningId(_xray), RunningId(_sing));
    private static int? RunningId(Process? process)
    {
        try { return process is { HasExited: false } ? process.Id : null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Opt-in persistent Windows Filtering Platform policy, removed by explicit Disconnect.</summary>
    public bool KillSwitch { get; set; }

    /// <summary>When on, an unexpected drop (core death or exit no longer
    /// verifying) triggers an automatic bounded reconnect. When off, a drop
    /// just faults and waits for a manual Reconnect.</summary>
    private bool _autoHeal = true;
    public bool AutoHeal
    {
        get => _autoHeal;
        set { _autoHeal = value; if (!value) { CancelSlowRetry(); CancelNetworkWait(); } else if (_desired && State == TunnelState.Faulted) ScheduleSlowRetry(); }
    }

    private Process? _xray;
    private Process? _sing;
    private StreamWriter? _xrayLog;
    private StreamWriter? _singLog;
    private volatile bool _stopping;
    private volatile bool _healing;
    private int _healAttempts;
    private int _verifyFailStreak;
    private System.Threading.Timer? _verifyTimer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _healGate = new(1, 1);
    private readonly SemaphoreSlim _verifyGate = new(1, 1);
    private readonly TunnelHealthChecker _healthChecker = new();
    private readonly NetworkMonitor _network;
    private readonly RecoveryPolicy _recovery = new();
    private System.Threading.Timer? _retryTimer;
    private Profile? _activeProfile;
    private ConnectionTelemetry? _telemetry;
    private CancellationTokenSource? _networkWait;
    private long _networkRevision;
    private int _manualWaiters;
    private readonly IncidentRecorder? _incidents;
    private bool? LinkAvailable => _platform != null ? _platform.LinkAvailable?.Invoke() ?? true : NetworkMonitor.LinkAvailable();
    private DateTimeOffset Now => _platform?.Now?.Invoke() ?? DateTimeOffset.UtcNow;
    private string ExpectedExit => ActiveProfile.ExpectedExitIp.Length > 0 ? ActiveProfile.ExpectedExitIp : ActiveProfile.Server;
    private Profile ActiveProfile => _activeProfile ?? _profile();
    private CancellationTokenSource _session = new();
    private volatile bool _desired;
    private bool _disposed;
    private int _generation;
    private int _intentVersion;

    private readonly Func<Profile> _profile;
    private readonly bool _manageFirewall;
    private readonly ControllerPlatform? _platform;
    public TunnelController(Func<Profile>? profile = null, bool manageFirewall = false, ControllerPlatform? platform = null)
    {
        _profile = profile ?? Profile.Load;
        _manageFirewall = manageFirewall;
        _platform = platform;
        if (manageFirewall && platform == null) _incidents = new(AppPaths.IncidentsDir, () => ActiveProfile.Secrets);
        if (platform != null && manageFirewall) throw new ArgumentException("A simulated platform cannot change native firewall state.");
        _network = new(reason => _ = NetworkChangedAsync(reason));
    }
    private async Task NetworkChangedAsync(string reason)
    {
        Interlocked.Increment(ref _networkRevision);
        if (!_desired || _disposed) return;
        InvalidateHealth();
        Raise(new(State, "Network changed; the connection needs fresh verification."));
        if (!AutoHeal) return;
        await HealAsync(reason, cause: RecoveryCause.NetworkChanged);
    }
    public Task CheckNowAsync() => VerifyOnceAsync();
    public Task<ConnectionSnapshot> ReadConnectionsAsync() => State == TunnelState.Connected && _telemetry != null
        ? _telemetry.ReadAsync(ActiveProfile.Server, _session.Token) : Task.FromResult(ConnectionSnapshot.Empty());
    public Task ReconnectAsync() => _desired ? HealAsync("Reconnect requested", manual: true) : ConnectAsync();
    private void NewSession()
    {
        _session.Cancel();
        _session.Dispose();
        _session = new();
        Interlocked.Increment(ref _generation);
        InvalidateHealth();
    }

    private void InvalidateHealth()
    {
        lock (_healthSync) { Verified = false; Health = null; ExitIp = null; }
        HealthChanged?.Invoke(null);
    }

    // ---------------------------------------------------------------- connect

    public async Task ConnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            CancelSlowRetry();
            if (State is TunnelState.Connecting or TunnelState.Connected) return;

            _desired = true;
            Interlocked.Increment(ref _intentVersion);
            _stopping = false;
            NewSession();
            Verified = false;
            ExitIp = null;
            _healAttempts = 0;
            _verifyFailStreak = 0;
            SetState(TunnelState.Connecting, "Starting proxy core…");

            if (_platform == null) KillStrayCores();

            // Preflight: after clearing our own strays, if 10808 is still taken it's a
            // FOREIGN client (v2rayN). Refuse clearly instead of letting xray fail to bind
            // and then blackholing traffic — the confusing "internet cut out" case.
            if (_platform == null && !PortFree(SocksPort))
            {
                bool v2 = IsRunning("v2rayN");
                SetState(TunnelState.Faulted, v2
                    ? "v2rayN is running and using port 10808. Exit v2rayN (tray V → Exit), then Connect."
                    : "Port 10808 is in use by another tunnel client. Close it, then Connect.");
                return;
            }

            var profile = _profile();
            if (!profile.IsValid)
            {
                SetState(TunnelState.Faulted, "Import a valid device connection first.");
                return;
            }

            _activeProfile = System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(profile))!;
            if (!ImportedProxy.IsV4(_activeProfile.Server))
            {
                using var resolving = CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
                resolving.CancelAfter(TimeSpan.FromSeconds(8));
                // A retained lock may block bootstrap DNS. Use the address saved during the explicit import.
                if (_manageFirewall && NetworkLock.HasPolicy() && ImportedProxy.IsV4(_activeProfile.Imported?.ResolvedAddress))
                    _activeProfile.Server = _activeProfile.Imported!.ResolvedAddress;
                else
                {
                    await V2rayImport.ResolveAsync(_activeProfile, resolving.Token);
                    _activeProfile.Server = _activeProfile.Imported!.ResolvedAddress;
                }
            }
            profile = _activeProfile;
            if (_platform == null)
            {
                MaterializeXrayConfig(profile);
                await EngineConfiguration.ValidateFilesAsync(AppPaths.XrayRunConfig, AppPaths.SingBoxRunConfig, _session.Token);
            }
            if (_manageFirewall && KillSwitch) NetworkLock.Apply(profile.Server, profile.Port, 0, profile.Imported?.Protocol == "shadowsocks");
            else if (_manageFirewall) NetworkLock.Remove();

            if (!await StartCoresAsync()) { ScheduleSlowRetry(); return; }

            await VerifyOnceAsync();
            if (!_stopping) StartVerifyTimer();
        }
        catch (Exception) when (!_disposed)
        {
            StopCoresOnly();
            if (!_stopping) SetState(TunnelState.Faulted, "Could not start the connection. Check your profile and logs.");
            ScheduleSlowRetry();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Starts and orders the two cores (xray → wait for SOCKS → sing-box → wait for TUN).
    /// Assumes the caller holds <see cref="_gate"/> and that the xray config is already
    /// materialized. On any failure it stops whatever started and faults with a specific
    /// message, returning false. On success State is Connected ("verifying exit…").
    /// Shared by both the initial connect and the self-heal path.
    /// </summary>
    private async Task<bool> StartCoresAsync()
    {
        if (_platform != null)
        {
            bool started = await _platform.Start(_session.Token);
            if (!_stopping) SetState(started ? TunnelState.Connected : TunnelState.Faulted, started ? "Connected — verifying exit…" : "Engine startup failed.");
            return started && !_stopping;
        }
        // 1) xray first (opens SOCKS 10808)
        _xray = StartCore(AppPaths.XrayExe, $"run -c \"{AppPaths.XrayRunConfig}\"",
                          AppPaths.XrayLog, ref _xrayLog, OnXrayExited);
        if (_xray is null)
        {
            SetState(TunnelState.Faulted, "Could not start the proxy core (xray).");
            return false;
        }

        if (!await WaitForPortAsync(SocksPort, TimeSpan.FromSeconds(6)))
        {
            StopCoresOnly();
            SetState(TunnelState.Faulted, "Proxy core did not open its port — check logs.");
            return false;
        }

        // 2) sing-box TUN (the privileged host has administrator rights)
        SetState(TunnelState.Connecting, "Starting tunnel adapter…");
        _sing = StartCore(AppPaths.SingBoxExe, $"run -c \"{AppPaths.SingBoxRunConfig}\"",
                         AppPaths.SingBoxLog, ref _singLog, OnSingExited);
        if (_sing is null)
        {
            StopCoresOnly();
            SetState(TunnelState.Faulted, "Could not start the tunnel adapter (sing-box).");
            return false;
        }

        await Task.Delay(900); // let the TUN adapter come up
        if (_sing.HasExited)
        {
            StopCoresOnly();
            SetState(TunnelState.Faulted, "Tunnel adapter exited immediately — check logs.");
            return false;
        }

        if (_manageFirewall && KillSwitch)
        {
            ulong luid = NetworkLock.TunnelLuid();
            if (luid == 0) throw new IOException("The tunnel adapter could not be identified. Network lock remains active.");
            var profile = ActiveProfile; NetworkLock.Apply(profile.Server, profile.Port, luid, profile.Imported?.Protocol == "shadowsocks");
        }
        SetState(TunnelState.Connected, "Connected — verifying exit…");
        return true;
    }

    private void StartVerifyTimer()
    {
        _verifyTimer?.Dispose();
        _verifyTimer = new System.Threading.Timer(_ => _ = VerifyOnceAsync(),
            null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    // --------------------------------------------------------------- disconnect

    public async Task DisconnectAsync()
    {
        _desired = false;
        CancelSlowRetry();
        Interlocked.Increment(ref _intentVersion);
        _stopping = true;
        _session.Cancel();
        await _gate.WaitAsync();
        try
        {
            await StopInternalAsync();
            if (_manageFirewall) NetworkLock.Remove();
            SetState(TunnelState.Disconnected, "Disconnected.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopInternalAsync()
    {
        _stopping = true;
        _session.Cancel();
        _verifyTimer?.Dispose();
        _verifyTimer = null;

        // sing-box first so system routing is restored before xray goes.
        StopCoresOnly();

        Verified = false;
        ExitIp = null;
        Health = null;
        HealthChanged?.Invoke(null);
        if (_manageFirewall)
            foreach (string file in new[] { AppPaths.XrayRunConfig, AppPaths.SingBoxRunConfig })
                try { if (File.Exists(file)) File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notify("A restricted runtime configuration could not be removed. Network recovery will continue.", NoticeKind.Warning); }
        await Task.CompletedTask;
    }

    /// <summary>Kill both cores and release their logs, without touching connection
    /// state or the _stopping flag (used by both teardown and the self-heal restart).</summary>
    private void StopCoresOnly()
    {
        _platform?.Stop();
        KillProcess(_sing);
        KillProcess(_xray);
        _sing = null;
        _xray = null;

        _singLog?.Dispose(); _singLog = null;
        _xrayLog?.Dispose(); _xrayLog = null;
    }

    // ----------------------------------------------------------------- verify

    private async Task VerifyOnceAsync()
    {
        if (State != TunnelState.Connected || _stopping || !await _verifyGate.WaitAsync(0)) return;
        var generation = _generation;
        long revision = Interlocked.Read(ref _networkRevision);
        bool lockCheckFailed = false;
        try
        {
            if (_manageFirewall && KillSwitch && !NetworkLock.IsActive())
            {
                lockCheckFailed = true;
                // Rebuild our policy transactionally if its required blocking rules are missing.
                var profile = ActiveProfile;
                NetworkLock.Apply(profile.Server, profile.Port, NetworkLock.TunnelLuid(), profile.Imported?.Protocol == "shadowsocks");
                if (!NetworkLock.IsActive()) throw new IOException("Network lock could not be verified.");
                lockCheckFailed = false;
            }
            var health = _platform == null ? await _healthChecker.CheckAsync(ExpectedExit, _session.Token) : await _platform.Check(_session.Token);
            lock (_healthSync)
            {
                if (_stopping || generation != _generation || revision != Interlocked.Read(ref _networkRevision) || State != TunnelState.Connected) return;
                Health = health;
                ExitIp = health.ExitIp;
                _healthRevision = revision;
                Verified = health.Healthy && HealthFreshness.IsCurrent(health, DateTimeOffset.UtcNow);
            }
            HealthChanged?.Invoke(health);
            _verifyFailStreak = health.NeedsRecovery ? _verifyFailStreak + 1 : 0;
            var message = Verified ? $"Connected — exit {ExitIp}; sampled routes verified"
                : ConnectionDiagnosis.For(health).Happening;
            Raise(new StatusEventArgs(State, message, ExitIp, Verified));
            if (AutoHeal && !_healing && _verifyFailStreak >= VerifyFailThreshold)
                _ = HealAsync("Full-device connection checks failed", cause: RecoveryCause.HealthFailed);
        }
        catch (OperationCanceledException) { ClearFailedCheck(); }
        catch (Exception) { ClearFailedCheck(); }
        finally { _verifyGate.Release(); }

        void ClearFailedCheck()
        {
            if (_stopping || generation != _generation || revision != Interlocked.Read(ref _networkRevision)) return;
            InvalidateHealth();
            if (lockCheckFailed)
            {
                StopCoresOnly();
                _verifyTimer?.Dispose(); _verifyTimer = null;
                SetState(TunnelState.Faulted, "Network lock could not be verified. Reconnect or Restore normal internet.");
                ScheduleSlowRetry();
                return;
            }
            Raise(new(State, "Connection check could not complete; current protection is unverified."));
            if (AutoHeal && !_healing && ++_verifyFailStreak >= VerifyFailThreshold)
                _ = HealAsync("Connection checks could not complete", cause: RecoveryCause.HealthFailed);
            Notify("Connection check could not complete.", NoticeKind.Warning);
        }
    }

    // ------------------------------------------------------------- supervision

    private void OnXrayExited(object? sender, EventArgs e) { if (ReferenceEquals(sender, _xray)) OnCoreExited("proxy core stopped", proxyDied: true); }
    private void OnSingExited(object? sender, EventArgs e) { if (ReferenceEquals(sender, _sing)) OnCoreExited("tunnel adapter stopped", proxyDied: false); }

    private void OnCoreExited(string reason, bool proxyDied)
    {
        // Ignore during deliberate teardown, during our own heal-restart, or during
        // startup (a failed initial connect is handled inline by ConnectAsync).
        if (_stopping || _healing || State != TunnelState.Connected) return;
        var generation = _generation;
        InvalidateHealth();
        Raise(new(State, "An engine stopped; current protection is unverified."));

        _ = Task.Run(async () =>
        {
            if (_stopping || _healing || generation != _generation) return;
            if (AutoHeal)
            {
                await HealAsync(reason, cause: proxyDied ? RecoveryCause.ProxyExited : RecoveryCause.TunnelExited);
                return;
            }

            // Auto-heal off: preserve the original fault behaviour.
            if (KillSwitch)
            {
                // The persistent WFP policy remains even if either core has exited.
                _verifyTimer?.Dispose(); _verifyTimer = null;
                Verified = false; ExitIp = null;
                SetState(TunnelState.Faulted,
                    "Tunnel stopped — network lock retained. Reconnect or Disconnect to restore normal internet.");
            }
            else
            {
                await _gate.WaitAsync();
                try
                {
                    if (_stopping || generation != _generation) return;
                    await StopInternalAsync();
                    SetState(TunnelState.Faulted,
                        proxyDied ? "Proxy core stopped — tunnel torn down." : "Tunnel adapter stopped — tunnel torn down.");
                }
                finally { _gate.Release(); }
            }
        });
    }

    /// <summary>
    /// Uses the pure recovery policy for targeted restart, escalation and a cross-cycle budget.
    /// A cycle makes at most three attempts. Failed cycles schedule a five-minute retry.
    /// Rebuilding the TUN restores ordinary routing unless the network lock is enabled.
    /// Disconnect cancels retry intent; only one recovery runs at a time.
    /// </summary>
    private async Task HealAsync(string reason, bool manual = false, RecoveryCause cause = RecoveryCause.Manual)
    {
        if (_stopping || !_desired || _disposed || (!AutoHeal && !manual)) return;
        if (manual)
        {
            Interlocked.Increment(ref _manualWaiters);
            try { CancelNetworkWait(); await _healGate.WaitAsync(); }
            finally { Interlocked.Decrement(ref _manualWaiters); }
        }
        else if (!await _healGate.WaitAsync(0)) return;
        var intent = _intentVersion;
        CancelSlowRetry();
        var previousHealth = Health;
        _healing = true;
        try
        {
            if (_stopping || !_desired || _disposed || (!AutoHeal && !manual)) return;
            if (!manual && (cause == RecoveryCause.NetworkChanged || LinkAvailable == false))
            {
                if (await WaitForNetworkAsync(reason, intent)) return;
                previousHealth = Health;
                cause = RecoveryCause.NetworkChanged;
            }
            _healAttempts = 0;
            _session.Cancel();
            _verifyTimer?.Dispose(); _verifyTimer = null;
            Verified = false; ExitIp = null;

            var profile = ActiveProfile;
            if (!profile.IsValid)
            {
                SetState(TunnelState.Faulted, "Import a valid connection profile first.");
                return;
            }

            while (!_stopping && intent == _intentVersion && _healAttempts < MaxHealAttempts)
            {
                if (!manual && (!AutoHeal || Volatile.Read(ref _manualWaiters) > 0)) throw new OperationCanceledException();
                if (!manual && LinkAvailable == false)
                {
                    if (await WaitForNetworkAsync("Network connection lost", intent)) return;
                    previousHealth = Health;
                }
                _healAttempts++;
                if (!_recovery.TryReserve(Now, manual)) break;
                var step = RecoveryPolicy.Step(cause, _healAttempts, previousHealth);
                if (_healAttempts == 1)
                    Notify("Connection dropped — reconnecting…", NoticeKind.Warning);
                SetState(TunnelState.Connecting,
                    $"Reconnecting (attempt {_healAttempts}/{MaxHealAttempts}) — {reason}…");

                var delay = step.Delay;
                // Disconnect interrupts even a recovery backoff.
                if (_platform == null)
                {
                    for (var remaining = delay; remaining > TimeSpan.Zero && !_stopping; remaining -= TimeSpan.FromMilliseconds(100))
                        await Task.Delay(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100));
                }
                else await _platform.Delay(delay);
                if (_stopping || intent != _intentVersion) return;
                if (!manual && (!AutoHeal || Volatile.Read(ref _manualWaiters) > 0)) throw new OperationCanceledException();

                bool started;
                await _gate.WaitAsync();
                try
                {
                    if (_stopping || intent != _intentVersion) return;
                    if (!manual && (!AutoHeal || Volatile.Read(ref _manualWaiters) > 0)) throw new OperationCanceledException();
                    NewSession();
                    if (_platform?.Restart != null)
                    {
                        started = await _platform.Restart(step.Action, _session.Token);
                        if (!_stopping) SetState(started ? TunnelState.Connected : TunnelState.Faulted, started ? "Rechecking connection…" : "Engine restart failed.");
                    }
                    else if (_platform == null && step.Action != RecoveryAction.Both)
                        started = await RestartPartAsync(step.Action);
                    else
                    {
                        if (_manageFirewall && KillSwitch) NetworkLock.Apply(profile.Server, profile.Port, 0, profile.Imported?.Protocol == "shadowsocks");
                        StopCoresOnly();
                        if (_platform == null) MaterializeXrayConfig(profile);
                        started = await StartCoresAsync();
                    }
                }
                finally { _gate.Release(); }

                if (!started) continue;      // StartCoresAsync already faulted; try again
                if (_stopping || intent != _intentVersion) return;

                if (!manual && cause == RecoveryCause.NetworkChanged && _healAttempts == 1)
                {
                    SetState(TunnelState.Connected, "Tunnel adapter restarted; allowing 20 seconds for routes to settle…");
                    using var settling = CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
                    _networkWait = settling;
                    try
                    {
                        if (!AutoHeal || Volatile.Read(ref _manualWaiters) > 0) throw new OperationCanceledException();
                        await RecoveryDelayAsync(RecoveryPolicy.AdapterGrace, settling.Token);
                    }
                    finally { Interlocked.CompareExchange(ref _networkWait, null, settling); }
                    if (_stopping || intent != _intentVersion) return;
                }

                await VerifyOnceAsync();
                if (_stopping || intent != _intentVersion) return;

                if (Health is { NeedsRecovery: false })
                {
                    _healAttempts = 0;
                    _verifyFailStreak = 0;
                    SetState(TunnelState.Connected, Verified ? $"Reconnected — exit {ExitIp}" : "Reconnected — UDP/voice reachability is unverified");
                    StartVerifyTimer();
                    return;
                }
                // Came up but exits the wrong IP / unverified — loop and retry.
            }

            if (_stopping || intent != _intentVersion) return;

            _verifyTimer?.Dispose(); _verifyTimer = null;
            Verified = false; ExitIp = null;
            SetState(TunnelState.Faulted, "Recovery is paused for five minutes. Check Wi-Fi sign-in or other VPNs; Reconnect retries now.");
            Notify("Recovery will retry in five minutes. Reconnect retries now; Disconnect stops retries.", NoticeKind.Warning);
            ScheduleSlowRetry();
        }
        catch (OperationCanceledException)
        {
            if (!_stopping && _desired && !AutoHeal && intent == _intentVersion)
                SetState(TunnelState.Faulted, "Automatic recovery is off. Reconnect when the network is ready.");
        }
        catch (Exception) when (!_disposed)
        {
            if (!_stopping && intent == _intentVersion) { SetState(TunnelState.Faulted, "Recovery could not finish. Reconnect now or wait for the next retry."); ScheduleSlowRetry(); }
        }
        finally
        {
            _healing = false;
            _healGate.Release();
        }
    }

    private void CancelNetworkWait()
    {
        try { _networkWait?.Cancel(); } catch (ObjectDisposedException) { }
    }
    private Task RecoveryDelayAsync(TimeSpan delay, CancellationToken ct) =>
        _platform == null ? Task.Delay(delay, ct) : _platform.Delay(delay).WaitAsync(ct);
    private async Task<bool> WaitForNetworkAsync(string reason, int intent)
    {
        await _gate.WaitAsync();
        CancellationTokenSource waiting;
        try
        {
            if (_stopping || !_desired || intent != _intentVersion) throw new OperationCanceledException();
            NewSession(); // Cancel old probes, but keep the engines and any network lock in place.
            _verifyTimer?.Dispose(); _verifyTimer = null;
            Verified = false; ExitIp = null;
            waiting = CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
            _networkWait = waiting;
        }
        finally { _gate.Release(); }
        try
        {
            var window = new NetworkRecoveryWindow();
            string? previous = null;
            while (true)
            {
                waiting.Token.ThrowIfCancellationRequested();
                if (!_desired || _stopping || !AutoHeal || intent != _intentVersion || Volatile.Read(ref _manualWaiters) > 0) throw new OperationCanceledException();
                bool? link = LinkAvailable;
                string message = link == false ? "Network link unavailable. Waiting for it to return; engines retained."
                    : $"{reason}. Allowing 15 seconds for the network to settle; engines retained.";
                if (message != previous) { SetState(TunnelState.Connecting, message); previous = message; }
                if (window.Ready(link, Interlocked.Read(ref _networkRevision), Now)) break;
                await RecoveryDelayAsync(TimeSpan.FromSeconds(1), waiting.Token);
            }
            waiting.Token.ThrowIfCancellationRequested();
            SetState(TunnelState.Connected, "Checking whether the existing tunnel recovered…");
            await VerifyOnceAsync();
            waiting.Token.ThrowIfCancellationRequested();
            if (Health is not { NeedsRecovery: false }) return false;
            _verifyFailStreak = 0;
            SetState(TunnelState.Connected, Verified ? "Connection recovered without restarting the engines." : "Connection recovered; UDP/voice reachability is unverified.");
            StartVerifyTimer(); return true;
        }
        finally { Interlocked.CompareExchange(ref _networkWait, null, waiting); waiting.Dispose(); }
    }

    private void CancelSlowRetry() { Interlocked.Exchange(ref _retryTimer, null)?.Dispose(); }
    private void ScheduleSlowRetry()
    {
        CancelSlowRetry();
        if (!_desired || !AutoHeal || _disposed || _platform != null) return;
        _retryTimer = new System.Threading.Timer(_ =>
        {
            if (_desired && AutoHeal && !_disposed) _ = HealAsync("Scheduled retry", cause: RecoveryCause.HealthFailed);
        }, null, RecoveryPolicy.SlowRetry, Timeout.InfiniteTimeSpan);
    }
    private async Task<bool> RestartPartAsync(RecoveryAction action)
    {
        if (action == RecoveryAction.Proxy && RunningId(_sing) != null)
        {
            KillProcess(_xray); _xray = null; _xrayLog?.Dispose(); _xrayLog = null;
            _xray = StartCore(AppPaths.XrayExe, $"run -c \"{AppPaths.XrayRunConfig}\"", AppPaths.XrayLog, ref _xrayLog, OnXrayExited);
            if (_xray == null || !await WaitForPortAsync(SocksPort, TimeSpan.FromSeconds(6)) || _xray.HasExited) return false;
            if (!_stopping) SetState(TunnelState.Connected, "Proxy restarted; tunnel adapter retained. Rechecking…");
            return !_stopping;
        }
        if (action == RecoveryAction.Tunnel && RunningId(_xray) != null)
        {
            if (_manageFirewall && KillSwitch) NetworkLock.Apply(ActiveProfile.Server, ActiveProfile.Port, 0, ActiveProfile.Imported?.Protocol == "shadowsocks");
            KillProcess(_sing); _sing = null; _singLog?.Dispose(); _singLog = null;
            await Task.Delay(600, _session.Token);
            _sing = StartCore(AppPaths.SingBoxExe, $"run -c \"{AppPaths.SingBoxRunConfig}\"", AppPaths.SingBoxLog, ref _singLog, OnSingExited);
            if (_sing == null) return false;
            await Task.Delay(900, _session.Token);
            if (_sing.HasExited) return false;
            if (_manageFirewall && KillSwitch)
            {
                ulong luid = NetworkLock.TunnelLuid(); if (luid == 0) return false;
                NetworkLock.Apply(ActiveProfile.Server, ActiveProfile.Port, luid, ActiveProfile.Imported?.Protocol == "shadowsocks");
            }
            if (!_stopping) SetState(TunnelState.Connected, "Tunnel adapter restarted; proxy retained. Rechecking…");
            return !_stopping;
        }
        if (_manageFirewall && KillSwitch) NetworkLock.Apply(ActiveProfile.Server, ActiveProfile.Port, 0, ActiveProfile.Imported?.Protocol == "shadowsocks");
        StopCoresOnly();
        return await StartCoresAsync();
    }

    // --------------------------------------------------------------- internals

    private void MaterializeXrayConfig(Profile p)
    {
        File.WriteAllText(AppPaths.XrayRunConfig, BuildXrayConfig(p, File.ReadAllText(AppPaths.XrayTemplate)), AppPaths.Utf8NoBom);
        _telemetry ??= new ConnectionTelemetry();
        var tun = System.Text.Json.Nodes.JsonNode.Parse(BuildTunConfig(p, File.ReadAllText(AppPaths.SingBoxConfig)))!;
        _telemetry.Configure(tun);
        File.WriteAllText(AppPaths.SingBoxRunConfig, tun.ToJsonString(), AppPaths.Utf8NoBom);
    }

    internal static string BuildXrayConfig(Profile p, string tpl)
    {
        var cfg = tpl.Replace("__UUID__", p.Uuid).Replace("__SNI__", p.Sni);
        var root = System.Text.Json.Nodes.JsonNode.Parse(cfg)!;
        if (p.Imported != null)
        {
            root["outbounds"] = new System.Text.Json.Nodes.JsonArray(p.Imported.Outbound(p));
            return root.ToJsonString();
        }
        var outbound = root["outbounds"]![0]!;
        outbound["settings"]!["vnext"]![0]!["address"] = p.Server;
        outbound["settings"]!["vnext"]![0]!["port"] = p.Port;
        outbound["streamSettings"]!["realitySettings"]!["publicKey"] = p.PublicKey;
        outbound["streamSettings"]!["realitySettings"]!["shortId"] = p.ShortId;
        return root.ToJsonString();
    }

    internal static string BuildTunConfig(Profile p, string template)
    {
        var tun = System.Text.Json.Nodes.JsonNode.Parse(template)!;
        string endpoint = ImportedProxy.IsV4(p.Server) ? p.Server : p.Imported?.ResolvedAddress ?? "";
        if (!ImportedProxy.IsV4(endpoint)) throw new InvalidDataException("Resolve the imported server before building its tunnel routes.");
        foreach (var rule in tun["route"]!["rules"]!.AsArray())
            if (rule?["outbound"]?.GetValue<string>() == "direct" && rule["ip_cidr"] != null)
            { rule["ip_cidr"] = new System.Text.Json.Nodes.JsonArray(endpoint + "/32"); rule["port"] = p.Port; }
        return tun.ToJsonString();
    }

    private static Process? StartCore(string exe, string args, string logPath,
                                      ref StreamWriter? logField, EventHandler onExited)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var sw = new StreamWriter(logPath, append: false) { AutoFlush = true };
            logField = sw;
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, ev) => WriteLog(sw, ev.Data);
            p.ErrorDataReceived += (_, ev) => WriteLog(sw, ev.Data);
            p.Exited += onExited;
            if (!p.Start()) return null;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteLog(StreamWriter writer, string? line)
    {
        if (line == null) return;
        try { lock (writer) writer.WriteLine(line); } catch (ObjectDisposedException) { } catch (IOException) { }
    }

    private static void KillProcess(Process? p)
    {
        if (p is null) return;
        try
        {
            // Exited handlers no-op during shutdown/heal via the _stopping/_healing guards.
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(4000);
            }
        }
        catch { /* already gone / access */ }
        finally { try { p.Dispose(); } catch { } }
    }

    /// <summary>Kill leftover cores from *our* install (crash recovery / no double-run).</summary>
    private static void KillStrayCores()
    {
        foreach (var name in new[] { "xray", "sing-box" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path != null && path.StartsWith(AppPaths.CoresDir, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(3000);
                    }
                }
                catch { /* not ours or no access */ }
                finally { try { p.Dispose(); } catch { } }
            }
        }
    }

    /// <summary>True if we can bind the loopback port (i.e. no other client holds it).</summary>
    private static bool PortFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool IsRunning(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) { try { p.Dispose(); } catch { } } }
    }

    private static async Task<bool> WaitForPortAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var c = new TcpClient();
                var connect = c.ConnectAsync("127.0.0.1", port);
                if (await Task.WhenAny(connect, Task.Delay(500)) == connect && c.Connected)
                    return true;
            }
            catch { /* not up yet */ }
            await Task.Delay(200);
        }
        return false;
    }

    private void SetState(TunnelState s, string msg)
    {
        State = s;
        Raise(new StatusEventArgs(s, msg, ExitIp, Verified));
    }

    private void Raise(StatusEventArgs e)
    {
        _incidents?.Observe(e.State, e.Message, Health, _desired, _healing, KillSwitch, Now);
        StatusChanged?.Invoke(this, e);
    }

    private void Notify(string message, NoticeKind kind) => Notice?.Invoke(message, kind);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelSlowRetry();
        _desired = false;
        _network.Dispose();
        _telemetry?.Dispose();
        _session.Cancel();
        _stopping = true;
        _verifyTimer?.Dispose();
        KillProcess(_sing);
        KillProcess(_xray);
        _singLog?.Dispose();
        _xrayLog?.Dispose();
        // In-flight async operations may still release their gates after cancellation.
    }
}
