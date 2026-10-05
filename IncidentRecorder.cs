using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json;

namespace WorkTunnel;

internal sealed record IncidentStatus(DateTimeOffset At, string Session, string State, bool ConnectionRequested,
    bool NetworkLockSelected, TunnelHealth? Health, string Message);

/// <summary>Bounded, local, redacted reports collected by the host even when the dashboard is closed.</summary>
internal sealed class IncidentRecorder
{
    public const int Keep = 20;
    private readonly string _root;
    private readonly Func<string[]> _secrets;
    private readonly Func<string> _network;
    private readonly bool _protect;
    private readonly string[] _logs;
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly object _gate = new();
    private readonly Queue<HistoryEntry> _events = new();
    private Task _pending = Task.CompletedTask;
    private string? _lastGood;
    private DateTimeOffset _savedGood, _captured;
    private string? _lastMessage;
    public string? Error { get; private set; }
    public IncidentRecorder(string root, Func<string[]> secrets, Func<string>? network = null, bool protect = true, string[]? logs = null)
    {
        _root = Path.GetFullPath(root); _secrets = secrets; _network = network ?? NetworkEvidence.Capture;
        _protect = protect; _logs = logs ?? [AppPaths.XrayLog, AppPaths.SingBoxLog];
        try
        {
            string path = Path.Combine(_root, "last-healthy.json");
            if (File.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) && new FileInfo(path).Length < 65536)
            {
                string saved = File.ReadAllText(path);
                var snapshot = JsonSerializer.Deserialize<IncidentStatus>(saved);
                if (snapshot is { State: nameof(TunnelState.Connected), Health.Healthy: true }) _lastGood = saved;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }
    public void Observe(TunnelState state, string message, TunnelHealth? health, bool desired, bool recovering, bool lockSelected, DateTimeOffset now)
    {
        try
        {
            string[] secrets = _secrets();
            string status = DiagnosticRedactor.ScrubJson(JsonSerializer.Serialize(new IncidentStatus(now, _session,
                state.ToString(), desired, lockSelected, health, message)), secrets);
            lock (_gate)
            {
                string safeMessage = DiagnosticRedactor.Scrub(message, secrets);
                if (_lastMessage != safeMessage)
                {
                    _events.Enqueue(new(now, state.ToString(), safeMessage));
                    while (_events.Count > 100) _events.Dequeue();
                    _lastMessage = safeMessage;
                }
                if (state == TunnelState.Connected && health?.Healthy == true)
                {
                    _lastGood = status;
                    if (now - _savedGood >= TimeSpan.FromMinutes(1))
                    {
                        _savedGood = now;
                        Queue(() => AtomicWrite(Path.Combine(_root, "last-healthy.json"), status));
                    }
                }
                bool failure = desired && (state == TunnelState.Faulted || health?.NeedsRecovery == true || recovering && state == TunnelState.Connecting);
                if (!failure || now - _captured < TimeSpan.FromMinutes(5)) return;
                _captured = now;
                string? lastGood = _lastGood;
                var events = _events.ToArray();
                Queue(() => Capture(status, lastGood, events, secrets, now));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.Security.SecurityException or System.Security.Cryptography.CryptographicException)
        { Error = "An incident could not be recorded. Connection recovery continued."; }
    }
    private void Queue(Action write)
    {
        _pending = _pending.ContinueWith(_ =>
        {
            try
            {
                if (_protect) SecretStore.RestrictDirectory(_root, WindowsIdentity.GetCurrent().User!);
                else Directory.CreateDirectory(_root);
                write(); Error = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
            { Error = "An incident could not be recorded. Connection recovery continued."; }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
    public Task FlushAsync() { lock (_gate) return _pending; }
    private void Capture(string status, string? lastGood, HistoryEntry[] events, string[] secrets, DateTimeOffset now)
    {
        string folder = Path.Combine(_root, "incident-" + now.UtcDateTime.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        AtomicWrite(Path.Combine(folder, "status.json"), status);
        if (lastGood != null) AtomicWrite(Path.Combine(folder, "last-healthy.json"), DiagnosticRedactor.ScrubJson(lastGood, secrets));
        AtomicWrite(Path.Combine(folder, "events.jsonl"), string.Join("\n", events.Select(e => JsonSerializer.Serialize(e))));
        AtomicWrite(Path.Combine(folder, "network.json"), DiagnosticRedactor.ScrubJson(_network(), secrets));
        using var data = JsonDocument.Parse(status);
        string explanation = data.RootElement.GetProperty("Message").GetString() ?? "Connection requires attention.";
        AtomicWrite(Path.Combine(folder, "summary.txt"), $"Conduit incident · {now:O}\n\nObserved: {explanation}\n\n" +
            "Compare status.json with last-healthy.json (when available). Each snapshot has its own time and host session. Network facts are sampled during report collection.\n" +
            "The report records evidence; it does not establish a root cause. NetworkLockSelected records the preference, not proof of active filtering.\n" +
            "Repeated failures are grouped for five minutes. Up to 20 reports are retained. No files are uploaded. Credentials, raw configs, interface names, SSIDs and browsing history are excluded. Review before sharing.\n");
        for (int i = 0; i < _logs.Length; i++)
            AtomicWrite(Path.Combine(folder, $"engine-{i + 1}.redacted.txt"), DiagnosticLog.Read(_logs[i], secrets));
        Prune();
    }
    private static void AtomicWrite(string path, string text)
    {
        string temp = path + ".new";
        try { File.WriteAllText(temp, text, AppPaths.Utf8NoBom); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private void Prune()
    {
        foreach (string path in Directory.GetDirectories(_root, "incident-*").OrderDescending(StringComparer.Ordinal).Skip(Keep))
        {
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint) || Directory.GetDirectories(full).Length != 0) continue;
            var files = Directory.GetFiles(full);
            if (files.Any(f => File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint))) continue;
            foreach (string file in files) File.Delete(file);
            Directory.Delete(full);
        }
    }
    internal static IReadOnlyList<(string Name, string Contents)> ReadRecent(string root)
    {
        var result = new List<(string, string)>();
        try
        {
            if (!Directory.Exists(root) || File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) return result;
            string[] names = ["summary.txt", "status.json", "last-healthy.json", "network.json", "events.jsonl", "engine-1.redacted.txt", "engine-2.redacted.txt"];
            foreach (string folder in Directory.GetDirectories(root, "incident-*").OrderDescending(StringComparer.Ordinal).Take(5))
            {
                if (File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint)) continue;
                foreach (string name in names)
                {
                    string path = Path.Combine(folder, name);
                    if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).Length > 262144) continue;
                    result.Add(("incidents/" + Path.GetFileName(folder) + "/" + name, File.ReadAllText(path)));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { result.Add(("incidents/unavailable.txt", "Some incident reports could not be read.")); }
        return result;
    }
}

internal static class DiagnosticLog
{
    public static string Read(string path, string[] secrets)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 65536), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            if (stream.Position > 0) reader.ReadLine();
            return string.Join("\n", reader.ReadToEnd().Split('\n')
                .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, "(?i)warning|error|fatal|failed|exception"))
                .TakeLast(80).Select(l => System.Text.RegularExpressions.Regex.Replace(DiagnosticRedactor.Scrub(l, secrets),
                    @"(?i)\b(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}\b", "[host removed]")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "Log unavailable."; }
    }
}

internal static class NetworkEvidence
{
    public static string Capture()
    {
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces().Take(32).Select(n =>
            {
                var p = n.GetIPProperties();
                int? Index(bool v6) { try { return v6 ? p.GetIPv6Properties()?.Index : p.GetIPv4Properties()?.Index; } catch (NetworkInformationException) { return null; } }
                return new { Type = n.NetworkInterfaceType.ToString(), Status = n.OperationalStatus.ToString(), Ipv4Index = Index(false), Ipv6Index = Index(true),
                    HasIpv4 = p.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork),
                    HasNonLinkLocalIpv6 = p.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal && !IPAddress.IsLoopback(a.Address)),
                    DnsServerCount = p.DnsAddresses.Count, GatewayCount = p.GatewayAddresses.Count };
            }).ToArray();
            object Route(string label, string address)
            {
                try { return new { Target = label, InterfaceIndex = RouteProbe.BestInterface(IPAddress.Parse(address)), Error = (string?)null }; }
                catch (Exception e) when (e is NetworkInformationException or InvalidOperationException) { return new { Target = label, InterfaceIndex = (uint?)null, Error = "Route lookup unavailable" }; }
            }
            return JsonSerializer.Serialize(new { CapturedAt = DateTimeOffset.UtcNow, Adapters = adapters, SampledRoutes = new[] {
                Route("Cloudflare IPv4", "1.1.1.1"), Route("Google IPv4", "8.8.8.8"),
                Route("Cloudflare IPv6", "2606:4700:4700::1111"), Route("Google IPv6", "2001:4860:4860::8888") },
                Scope = "Route samples and adapter capabilities. No interface names, addresses, SSIDs or destination history are collected." });
        }
        catch (Exception e) when (e is NetworkInformationException or InvalidOperationException) { return "{\"Error\":\"Network facts unavailable\"}"; }
    }
}
