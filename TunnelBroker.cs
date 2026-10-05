using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace WorkTunnel;

internal sealed record BrokerRequest(string Command, Profile? Profile = null, bool? AutoHeal = null, bool? KillSwitch = null);
internal sealed record BrokerSnapshot(TunnelState State, string Message, string? ExitIp, bool Verified, TunnelHealth? Health,
    bool Desired, bool AutoHeal, bool KillSwitch, int? Xray, int? SingBox, string? Error = null, long Revision = 0, string HostId = "", bool LockActive = false,
    ConnectionSnapshot? Connections = null, NetworkLockObservation? LockObservation = null);

internal static class BrokerFacts
{
    internal static bool Verified(BrokerSnapshot? snapshot, DateTimeOffset now) => snapshot is { State: TunnelState.Connected, Verified: true } &&
        HealthFreshness.IsCurrent(snapshot.Health, now) && snapshot.Health!.Healthy;
    internal static bool Recent(DateTimeOffset received, DateTimeOffset now) => now >= received && now - received < TimeSpan.FromSeconds(5);
}

internal static class BrokerWire
{
    public static string Name => "WorkTunnel.v2." + AppPaths.UserSid;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length > 16384) throw new InvalidDataException("Message too large.");
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, data.Length);
        try { await stream.WriteAsync(prefix, ct); await stream.WriteAsync(data, ct); await stream.FlushAsync(ct); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(data); }
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        byte[] size = new byte[4]; await stream.ReadExactlyAsync(size, ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length is < 1 or > 16384) throw new InvalidDataException("Invalid message length.");
        byte[] data = new byte[length]; await stream.ReadExactlyAsync(data, ct);
        try { return JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("Missing message."); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(data); }
    }
}

internal static class TunnelBroker
{
    public static async Task RunAsync()
    {
        InstallSecurity.RequireInstalled();
        if (!InstallSecurity.IsAdministrator) throw new UnauthorizedAccessException("The tunnel host requires elevation.");
        using var singleton = new Mutex(true, "Global\\WorkTunnel.Host.v2", out bool created);
        if (!created) throw new InvalidOperationException("A tunnel host is already running. Disconnect it before starting another installation or Windows user session.");
        using var children = ProcessLifetime.OwnHostAndChildren();
        InstallSecurity.ProtectMachineDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkTunnel"));
        InstallSecurity.ProtectMachineDirectory(Path.GetDirectoryName(AppPaths.RuntimeDir)!);
        var sid = WindowsIdentity.GetCurrent().User!;
        SecretStore.RestrictDirectory(AppPaths.RuntimeDir, sid);
        SecretStore.RestrictDirectory(AppPaths.LogDir, sid);
        Profile active = new();
        using var engine = new TunnelController(() => active, manageFirewall: true);
        engine.KillSwitch = NetworkLock.HasPolicy();
        string message = engine.KillSwitch ? "An earlier network lock remains active. Connect or Restore normal internet." : "Tunnel host ready.";
        engine.StatusChanged += (_, e) => message = e.Message;
        using var stop = new CancellationTokenSource();
        var tasks = new List<Task>();
        using var commands = new SemaphoreSlim(1, 1);
        long revision = 0;
        string hostId = Guid.NewGuid().ToString("N");
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        BrokerSnapshot Snapshot(string? error = null)
        {
            var observation = NetworkLock.Observe();
            return new(engine.State, message, engine.ExitIp, engine.Verified && (!engine.KillSwitch || observation.BlockingVerified), engine.Health,
                engine.ConnectionRequested, engine.AutoHeal, engine.KillSwitch, engine.CoreProcessIds.Xray, engine.CoreProcessIds.SingBox,
                error, Interlocked.Increment(ref revision), hostId, observation.BlockingVerified, LockObservation: observation);
        }
        async Task Serve(NamedPipeServerStream pipe)
        {
            using (pipe)
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
            {
                try
                {
                    var request = await BrokerWire.ReadAsync<BrokerRequest>(pipe, deadline.Token);
                    bool sameUser = false;
                    pipe.RunAsClient(() => sameUser = WindowsIdentity.GetCurrent().User == sid);
                    if (!sameUser) throw new UnauthorizedAccessException("The tunnel host belongs to another Windows user.");
                    try
                    {
                        // Status remains responsive while connection work runs. Serialize mutations.
                        bool mutation = request.Command is not ("status" or "connections");
                        ConnectionSnapshot? connections = null;
                        if (mutation) await commands.WaitAsync(deadline.Token);
                        try
                        {
                        switch (request.Command)
                        {
                            case "status": break;
                            case "connections": connections = await engine.ReadConnectionsAsync(); break;
                            case "connect":
                                if (request.Profile?.IsValid != true) throw new InvalidDataException("Import a valid device profile first.");
                                if (engine.ConnectionRequested) throw new InvalidOperationException("Disconnect before changing the connection.");
                                active = request.Profile;
                                engine.AutoHeal = request.AutoHeal ?? true; engine.KillSwitch = request.KillSwitch ?? false;
                                await engine.ConnectAsync(); break;
                            case "disconnect": await engine.DisconnectAsync(); break;
                            case "reconnect": await engine.ReconnectAsync(); break;
                            case "check": await engine.CheckNowAsync(); break;
                            case "options":
                                if (request.KillSwitch.HasValue && engine.ConnectionRequested && request.KillSwitch != engine.KillSwitch)
                                    throw new InvalidOperationException("Disconnect before changing the kill switch.");
                                if (request.AutoHeal.HasValue) engine.AutoHeal = request.AutoHeal.Value;
                                if (request.KillSwitch.HasValue) engine.KillSwitch = request.KillSwitch.Value;
                                break;
                            case "shutdown": await engine.DisconnectAsync(); stop.Cancel(); break;
                            default: throw new InvalidDataException("Unsupported tunnel-host command.");
                        }
                        }
                        finally { if (mutation) commands.Release(); }
                        await BrokerWire.WriteAsync(pipe, Snapshot() with { Connections = connections }, deadline.Token);
                    }
                    catch (Exception e) { await BrokerWire.WriteAsync(pipe, Snapshot(DiagnosticRedactor.Scrub(e.Message)), deadline.Token); }
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException) { }
            }
        }
        try
        {
            while (!stop.IsCancellationRequested)
            {
                tasks.RemoveAll(t => t.IsCompleted);
                var pipe = NamedPipeServerStreamAcl.Create(BrokerWire.Name, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16384, 16384, security);
                try { await pipe.WaitForConnectionAsync(stop.Token); tasks.Add(Serve(pipe)); }
                catch { pipe.Dispose(); if (!stop.IsCancellationRequested) throw; }
                if (tasks.Count >= 7) await Task.WhenAny(tasks);
            }
        }
        finally { await engine.DisconnectAsync(); await Task.WhenAll(tasks); }
    }
}

internal sealed class BrokerClient : ITunnelController
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint process);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _launch = new(1, 1);
    private bool _auto = Preferences.AutoReconnect(), _kill = Preferences.NetworkLock();
    private string _message = "", _health = "";
    private BrokerSnapshot? _snapshot;
    private DateTimeOffset _receivedAt;
    private ConnectionSnapshot _connections = ConnectionSnapshot.Empty();
    public event EventHandler<StatusEventArgs>? StatusChanged;
    public event Action<string, NoticeKind>? Notice;
    public event Action<TunnelHealth?>? HealthChanged;
    public TunnelState State => _snapshot?.State ?? TunnelState.Disconnected;
    public string? ExitIp => _snapshot?.ExitIp;
    public bool Verified => BrokerFacts.Recent(_receivedAt, DateTimeOffset.UtcNow) && BrokerFacts.Verified(_snapshot, DateTimeOffset.UtcNow);
    public TunnelHealth? Health => _snapshot?.Health;
    public bool ConnectionRequested => _snapshot?.Desired ?? false;
    public NetworkLockObservation LockObservation => BrokerFacts.Recent(_receivedAt, DateTimeOffset.UtcNow)
        ? _snapshot?.LockObservation ?? NetworkLockObservation.Unavailable : NetworkLockObservation.Unavailable;
    public bool NetworkLockActive => LockObservation is { Known: true, BlockingVerified: true };
    public (int? Xray, int? SingBox) CoreProcessIds => (_snapshot?.Xray, _snapshot?.SingBox);
    public bool AutoHeal { get => _auto; set { _auto = value; if (_snapshot != null && _snapshot.AutoHeal != value) _ = SetOptionsAsync(); } }
    public bool KillSwitch
    {
        get => _kill;
        set
        {
            if (ConnectionRequested && value != _kill) throw new InvalidOperationException("Disconnect before changing the network lock.");
            Preferences.SaveNetworkLock(value); _kill = value;
            if (_snapshot != null && _snapshot.KillSwitch != value) _ = SetOptionsAsync();
        }
    }
    public BrokerClient() => _ = PollAsync();
    private async Task SetOptionsAsync() { try { await SendAsync(new("options", AutoHeal: _auto, KillSwitch: _kill)); } catch (Exception e) { Notice?.Invoke(e.Message, NoticeKind.Warning); } }
    private async Task PollAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await SendAsync(new("status"), connectMs: 200); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch
            {
                if (_snapshot != null && _message != "host-unavailable")
                {
                    _message = "host-unavailable";
                    _snapshot = _snapshot with { State = TunnelState.Faulted, Verified = false, Health = null, LockActive = false, LockObservation = NetworkLockObservation.Unavailable };
                    StatusChanged?.Invoke(this, new(TunnelState.Faulted, "Tunnel host unavailable. Reconnect or use Restore normal internet."));
                    HealthChanged?.Invoke(null);
                }
            }
            try { await Task.Delay(1500, _stop.Token); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task SendAsync(BrokerRequest request, int connectMs = 3000)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(request.Command == "status" ? 3 : request.Command == "connections" ? 5 : 85));
        using var pipe = new NamedPipeClientStream(".", BrokerWire.Name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(connectMs, deadline.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || !InstallSecurity.ProcessPath(pid).Equals(Path.Combine(InstallSecurity.InstallRoot, "WorkTunnel.exe"), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Unrecognized tunnel host. Nothing was sent.");
        await BrokerWire.WriteAsync(pipe, request, deadline.Token);
        var snapshot = await BrokerWire.ReadAsync<BrokerSnapshot>(pipe, deadline.Token);
        if (snapshot.Error != null) throw new InvalidOperationException(snapshot.Error);
        if (snapshot.Connections != null) _connections = snapshot.Connections;
        if (_snapshot?.HostId == snapshot.HostId && snapshot.Revision < _snapshot.Revision) return;
        if (_snapshot == null && snapshot.LockActive) _kill = true;
        _snapshot = snapshot;
        _receivedAt = DateTimeOffset.UtcNow;
        if (snapshot.Desired) { _auto = snapshot.AutoHeal; _kill = snapshot.KillSwitch; }
        bool verified = Verified;
        string status = snapshot.State + snapshot.Message + verified + LockObservation;
        if (status != _message) { _message = status; StatusChanged?.Invoke(this, new(snapshot.State, snapshot.Message, snapshot.ExitIp, verified)); }
        string health = JsonSerializer.Serialize(snapshot.Health);
        if (health != _health) { _health = health; HealthChanged?.Invoke(snapshot.Health); }
    }
    public async Task EnsureHostAsync()
    {
        await _launch.WaitAsync();
        try
        {
            try { await SendAsync(new("status"), 200); return; } catch (TimeoutException) { } catch (IOException) { }
            InstallSecurity.RequireInstalled();
            var start = new ProcessStartInfo(Path.Combine(AppPaths.BaseDir, "WorkTunnel.exe")) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--tunnel-host"); Process.Start(start)?.Dispose();
            for (int i = 0; i < 25; i++)
            {
                try { await SendAsync(new("status"), 500); return; } catch (TimeoutException) { } catch (IOException) { }
                await Task.Delay(200);
            }
            throw new IOException("Tunnel host did not start. Accept the UAC prompt and try again.");
        }
        finally { _launch.Release(); }
    }
    public async Task ConnectAsync() { await EnsureHostAsync(); await SendAsync(new("connect", Profile.Load(), _auto, _kill)); }
    public async Task DisconnectAsync() { await EnsureHostAsync(); await SendAsync(new("disconnect")); }
    public async Task ReconnectAsync() { await EnsureHostAsync(); if (!ConnectionRequested) await ConnectAsync(); else await SendAsync(new("reconnect")); }
    public Task CheckNowAsync() => SendAsync(new("check"));
    public async Task<ConnectionSnapshot> ReadConnectionsAsync()
    {
        if (State != TunnelState.Connected) return ConnectionSnapshot.Empty();
        try { await SendAsync(new("connections")); return _connections; }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
        { return ConnectionSnapshot.Empty("Tunnel destination data is unavailable."); }
    }
    public async Task ShutdownAsync() { if (_snapshot != null) { await EnsureHostAsync(); await SendAsync(new("shutdown")); _snapshot = null; _stop.Cancel(); } }
    public void Dispose() { _stop.Cancel(); /* The independently running host keeps an active call alive. */ }
}
