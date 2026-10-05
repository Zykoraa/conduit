using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkTunnel;

internal static class DiagnosticRedactor
{
    public static string ScrubJson(string json, IEnumerable<string> secrets)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        void Walk(System.Text.Json.Nodes.JsonNode n)
        {
            if (n is System.Text.Json.Nodes.JsonObject obj)
                foreach (string key in obj.Select(k => k.Key).ToArray())
                {
                    if (obj[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s)) obj[key] = Scrub(s, secrets);
                    else if (obj[key] != null) Walk(obj[key]!);
                }
            else if (n is System.Text.Json.Nodes.JsonArray a)
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s)) a[i] = Scrub(s, secrets);
                    else if (a[i] != null) Walk(a[i]!);
                }
        }
        Walk(node); return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
    public static string Scrub(string value, IEnumerable<string>? secrets = null)
    {
        foreach (string secret in secrets ?? [])
            if (!string.IsNullOrWhiteSpace(secret)) value = value.Replace(secret, "[redacted]", StringComparison.OrdinalIgnoreCase);
        value = Regex.Replace(value, @"-----BEGIN [^-]*PRIVATE KEY-----.*?(?:-----END [^-]*PRIVATE KEY-----|$)", "[private key removed]", RegexOptions.Singleline);
        value = Regex.Replace(value, @"(?i)\b(?:vless|vmess|trojan|ss|https?)://\S+", "[link removed]");
        value = Regex.Replace(value, @"(?i)\b[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\b", "[identity removed]");
        value = Regex.Replace(value, "(?i)([\\\"']?(?:password|passwd|token|uuid|privateKey|publicKey|shortId|authorization|secret|api[_-]?key)[\\\"']?\\s*[:=]\\s*)(?:[\\\"][^\\\"]*[\\\"]|[^\\s,;]+)", "$1[redacted]");
        value = Regex.Replace(value, @"(?i)\bBearer\s+\S+", "Bearer [redacted]");
        value = Regex.Replace(value, @"(?i)[a-z]:\\Users\\[^\\\s]+", @"C:\Users\[user]");
        value = Regex.Replace(value, @"(?<!\w)(?:[0-9a-fA-F]{0,4}:){2,}[0-9a-fA-F:.%]*(?!\w)", m =>
            System.Net.IPAddress.TryParse(m.Value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[IP removed]" : m.Value);
        value = Regex.Replace(value, @"(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?!\d)", "[IP removed]");
        value = Regex.Replace(value, @"\b[A-Za-z0-9_+/=-]{32,}\b", "[long identifier removed]");
        return value.Length > 2000 ? value[..2000] + "…" : value;
    }
}

internal sealed record HistoryEntry(DateTimeOffset At, string Kind, string Message);

internal sealed class HistoryStore
{
    private readonly string? _path;
    private readonly object _gate = new();
    private readonly List<HistoryEntry> _entries = new();
    public string? Error { get; private set; }
    public HistoryStore(string? path)
    {
        _path = path;
        try
        {
            if (!File.Exists(path)) return;
            // Bounds both memory use and the cost of recovering a damaged log.
            if (new FileInfo(path).Length > 4_000_000) { Error = "History file is too large to load."; return; }
            foreach (string line in File.ReadLines(path))
                try { var e = JsonSerializer.Deserialize<HistoryEntry>(line); if (e != null) _entries.Add(e); } catch (JsonException) { }
            if (_entries.Count > 2000) _entries.RemoveRange(0, _entries.Count - 2000);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Error = "History could not be read."; }
    }
    public HistoryEntry[] Entries { get { lock (_gate) return _entries.ToArray(); } }
    public void Add(string kind, string message)
    {
        lock (_gate)
        {
            var entry = new HistoryEntry(DateTimeOffset.Now, kind, DiagnosticRedactor.Scrub(message));
            _entries.Add(entry); if (_entries.Count > 2000) _entries.RemoveAt(0);
            if (_path == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (File.Exists(_path) && new FileInfo(_path).Length > 2_000_000)
                {
                    string temp = _path + ".new";
                    File.WriteAllLines(temp, _entries.Select(e => JsonSerializer.Serialize(e)), AppPaths.Utf8NoBom);
                    File.Move(temp, _path, true);
                }
                else File.AppendAllText(_path, JsonSerializer.Serialize(entry) + "\n", AppPaths.Utf8NoBom);
                Error = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Error = "History could not be saved; this session is still visible."; }
        }
    }
}

internal static class ProblemExplanation
{
    public static string Explain(TunnelState state, TunnelHealth? h)
    {
        if (state == TunnelState.Disconnected) return "Disconnected by this app. Other VPNs may still be active.";
        if (state == TunnelState.Connecting) return "Starting the engines and tunnel adapter. Waiting for measurements.";
        if (state == TunnelState.Faulted) return "The tunnel could not stay running. Check the latest event or save diagnostics; the cause is not yet established.";
        return ConnectionDiagnosis.For(h).ToString();
    }
}

internal static class DiagnosticsExporter
{
    public static void Export(string destination, TunnelState state, TunnelHealth? health, HistoryEntry[] history, QualityResult? quality)
    {
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { WriteArchive(temp, state, health, history, quality); File.Move(temp, destination, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void WriteArchive(string destination, TunnelState state, TunnelHealth? health, HistoryEntry[] history, QualityResult? quality)
    {
        var profile = Profile.Load();
        string[] secrets = profile.Secrets;
        using var file = new FileStream(destination, FileMode.CreateNew);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        void Write(string name, string contents)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), AppPaths.Utf8NoBom);
            writer.Write(contents);
        }
        Write("README.txt", "Local diagnostic export, including current network facts and up to five recent incident reports. No files are uploaded. Profile credentials, private keys and raw configs are excluded. Recent warning/error lines are scrubbed, but review this ZIP before sharing. Incident snapshots have their own timestamps and may be from a previous host session. Probe failures are not a measurement of Discord packet loss.\n");
        Write("summary.json", DiagnosticRedactor.ScrubJson(JsonSerializer.Serialize(new { Created = DateTimeOffset.Now,
            Version = typeof(DiagnosticsExporter).Assembly.GetName().Version?.ToString(3), Windows = Environment.OSVersion.VersionString,
            State = state.ToString(), Explanation = ProblemExplanation.Explain(state, health), Health = health, Quality = quality,
            Settings = SettingsSnapshot.Read(includeEndpoint: false, runtime: state == TunnelState.Connected) }, new JsonSerializerOptions { WriteIndented = true }), secrets));
        Write("history.jsonl", string.Join("\n", history.TakeLast(300).Select(e => JsonSerializer.Serialize(e with { Message = DiagnosticRedactor.Scrub(e.Message, secrets) }))));
        Write("network.json", DiagnosticRedactor.ScrubJson(NetworkEvidence.Capture(), secrets));
        foreach (var report in IncidentRecorder.ReadRecent(AppPaths.IncidentsDir))
        {
            try
            {
                string safe = report.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? DiagnosticRedactor.ScrubJson(report.Contents, secrets)
                    : string.Join("\n", report.Contents.Split('\n').Select(line => DiagnosticRedactor.Scrub(line, secrets)));
                Write(report.Name, safe);
            }
            catch (JsonException) { Write(report.Name + ".unavailable.txt", "This incident file could not be read as JSON."); }
        }
        foreach (var path in new[] { AppPaths.XrayLog, AppPaths.SingBoxLog })
            Write(Path.GetFileName(path) + ".redacted.txt", DiagnosticLog.Read(path, secrets));
    }
}
