using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkTunnel;

/// <summary>Bounded connection fields, never executable paths or arbitrary engine JSON.</summary>
internal sealed class ImportedProxy
{
    public string Protocol { get; set; } = "vless";
    public string Transport { get; set; } = "tcp";
    public string Security { get; set; } = "none";
    public string Credential { get; set; } = "";
    public string Method { get; set; } = "";
    public string Flow { get; set; } = "";
    public string Fingerprint { get; set; } = "chrome";
    public string Path { get; set; } = "/";
    public string Host { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string Authority { get; set; } = "";
    public bool MultiMode { get; set; }
    public string[] Alpn { get; set; } = [];
    public string ResolvedAddress { get; set; } = "";

    public bool Valid(Profile p) => Protocol is "vless" or "vmess" or "trojan" or "shadowsocks" &&
        Transport is "tcp" or "ws" or "grpc" && Security is "none" or "tls" or "reality" &&
        Bounded(Credential, 512) && Credential.Length > 0 && Bounded(Path, 1024) && Bounded(Host, 253) &&
        Bounded(ServiceName, 256) && Bounded(Authority, 253) && Bounded(Fingerprint, 32) &&
        Alpn is { Length: <= 8 } && Alpn.All(a => Bounded(a, 64) && a.Length > 0) &&
        (Protocol is not ("vless" or "vmess") || Guid.TryParse(Credential, out _)) &&
        (Protocol != "vmess" || Method is "auto" or "aes-128-gcm" or "chacha20-poly1305" or "none" or "zero") &&
        (Protocol != "shadowsocks" || Method is "aes-128-gcm" or "aes-256-gcm" or "chacha20-poly1305" or "chacha20-ietf-poly1305" or "2022-blake3-aes-128-gcm" or "2022-blake3-aes-256-gcm" or "2022-blake3-chacha20-poly1305") &&
        (Flow == "" || Protocol == "vless" && Transport == "tcp" && Flow == "xtls-rprx-vision" && Security is "tls" or "reality") &&
        (Security == "none" || Uri.CheckHostName(p.Sni) is UriHostNameType.Dns or UriHostNameType.IPv4) &&
        (Security != "reality" || DeviceLink.ValidKey(p.PublicKey) && (p.ShortId == "" || DeviceLink.ValidShortId(p.ShortId))) &&
        (ResolvedAddress == "" || IsV4(ResolvedAddress));
    internal static bool Bounded(string? value, int max) => value != null && value.Length <= max && !value.Any(char.IsControl);
    internal static bool IsV4(string? value) => IPAddress.TryParse(value, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    public JsonObject Outbound(Profile p)
    {
        if (!Valid(p)) throw new InvalidDataException("The imported connection contains unsupported or invalid settings.");
        var server = new JsonObject { ["address"] = p.Server, ["port"] = p.Port };
        JsonObject settings;
        if (Protocol is "vless" or "vmess")
        {
            var user = new JsonObject { ["id"] = Credential };
            if (Protocol == "vless") { user["encryption"] = "none"; if (Flow != "") user["flow"] = Flow; }
            else { user["security"] = Method; user["alterId"] = 0; }
            server["users"] = new JsonArray(user); settings = new() { ["vnext"] = new JsonArray(server) };
        }
        else
        {
            server["password"] = Credential;
            if (Protocol == "shadowsocks") server["method"] = Method;
            settings = new() { ["servers"] = new JsonArray(server) };
        }
        var stream = new JsonObject { ["network"] = Transport, ["security"] = Security };
        if (Security == "tls")
        {
            var tls = new JsonObject { ["serverName"] = p.Sni, ["fingerprint"] = Fingerprint, ["allowInsecure"] = false };
            if (Alpn.Length > 0) tls["alpn"] = new JsonArray(Alpn.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
            stream["tlsSettings"] = tls;
        }
        if (Security == "reality") stream["realitySettings"] = new JsonObject
            { ["serverName"] = p.Sni, ["fingerprint"] = Fingerprint, ["publicKey"] = p.PublicKey, ["shortId"] = p.ShortId, ["spiderX"] = Path };
        if (Transport == "ws")
        {
            var ws = new JsonObject { ["path"] = Path };
            if (Host != "") ws["headers"] = new JsonObject { ["Host"] = Host };
            stream["wsSettings"] = ws;
        }
        if (Transport == "grpc") stream["grpcSettings"] = new JsonObject { ["serviceName"] = ServiceName, ["authority"] = Authority, ["multiMode"] = MultiMode };
        return new JsonObject { ["tag"] = "proxy", ["protocol"] = Protocol, ["settings"] = settings, ["streamSettings"] = stream };
    }
}

internal static class V2rayImport
{
    public static IReadOnlyList<string> FindCandidates()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { "v2rayN", "v2rayN-With-Core", "v2rayN-windows-64" })
            foreach (string folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") })
                roots.Add(Path.Combine(folder, name));
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("v2rayN"))
        {
            using (process) try { if (process.MainModule?.FileName is { } exe) roots.Add(System.IO.Path.GetDirectoryName(exe)!); } catch { }
        }
        return roots.Select(r => System.IO.Path.Combine(r, "binConfigs", "config.json")).Where(File.Exists).Order().ToArray();
    }
    public static Profile Read(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("The config is too large (maximum 1 MiB).");
        return Parse(File.ReadAllText(path));
    }
    private static string Text(JsonNode? n, string name, string fallback = "") => n?[name]?.GetValue<string>() ?? fallback;
    private static void Fields(JsonObject? obj, params string[] allowed)
    {
        if (obj == null) return;
        foreach (var field in obj)
            if (field.Value != null && !(field.Value is JsonValue value && value.TryGetValue<string>(out var text) && text == "") && !allowed.Contains(field.Key))
                throw new InvalidDataException($"This server uses an unsupported setting: {field.Key}. The current profile was kept.");
    }
    private static JsonObject Single(JsonNode? node, string what)
    {
        if (node is not JsonArray { Count: 1 } values || values[0] is not JsonObject result)
            throw new InvalidDataException($"Choose one active server in v2rayN. Conduit imports one {what} at a time.");
        return result;
    }
    public static Profile Parse(string json)
    {
        if (json.Length > 1024 * 1024) throw new InvalidDataException("The config is too large.");
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 32 })?.AsObject()
            ?? throw new InvalidDataException("Choose v2rayN's binConfigs/config.json (Xray config).");
        var candidates = (root["outbounds"] as JsonArray)?.OfType<JsonObject>().Where(o => Text(o, "protocol") is "vless" or "vmess" or "trojan" or "shadowsocks").ToArray() ?? [];
        var chosen = candidates.FirstOrDefault(o => Text(o, "tag") == "proxy") ?? (candidates.Length == 1 ? candidates[0] : null)
            ?? throw new InvalidDataException("No single supported Xray server found. Select a VLESS, VMess, Trojan or Shadowsocks server in v2rayN and connect once to generate its config.");
        Fields(chosen, "tag", "protocol", "settings", "streamSettings", "mux");
        var proxy = new ImportedProxy { Protocol = Text(chosen, "protocol") };
        var settings = chosen["settings"]?.AsObject();
        Fields(settings, proxy.Protocol is "vless" or "vmess" ? "vnext" : "servers");
        var server = Single(settings?[proxy.Protocol is "vless" or "vmess" ? "vnext" : "servers"], "server");
        Fields(server, "address", "port", "users", "password", "method", "level", "email", "ota");
        if (server["ota"]?.GetValue<bool>() == true) throw new InvalidDataException("Legacy Shadowsocks OTA is unsupported.");
        var p = new Profile { User = "Imported from v2rayN", Server = Text(server, "address"), Port = server["port"]?.GetValue<int>() ?? 0, Imported = proxy, Sni = "" };
        if (proxy.Protocol is "vless" or "vmess")
        {
            var user = Single(server["users"], "user");
            Fields(user, "id", "encryption", "flow", "security", "alterId", "level", "email");
            if (Text(user, "encryption", "none") != "none" || (user["alterId"]?.GetValue<int>() ?? 0) != 0)
                throw new InvalidDataException("This server uses unsupported VLESS encryption or legacy VMess alterId.");
            proxy.Credential = Text(user, "id"); proxy.Flow = Text(user, "flow"); proxy.Method = Text(user, "security", "auto");
            p.Uuid = proxy.Credential;
        }
        else { proxy.Credential = Text(server, "password"); proxy.Method = Text(server, "method"); }
        var stream = chosen["streamSettings"]?.AsObject();
        Fields(stream, "network", "security", "tlsSettings", "realitySettings", "tcpSettings", "rawSettings", "wsSettings", "grpcSettings");
        proxy.Transport = Text(stream, "network", "tcp"); if (proxy.Transport == "raw") proxy.Transport = "tcp";
        proxy.Security = Text(stream, "security", "none");
        var tls = stream?[proxy.Security == "reality" ? "realitySettings" : "tlsSettings"]?.AsObject();
        Fields(tls, "serverName", "fingerprint", "allowInsecure", "alpn", "publicKey", "password", "shortId", "spiderX", "show");
        if (tls?["allowInsecure"]?.GetValue<bool>() == true) throw new InvalidDataException("This server disables certificate verification. Fix that in v2rayN before importing.");
        p.Sni = Text(tls, "serverName", p.Server); proxy.Fingerprint = Text(tls, "fingerprint", "chrome");
        p.PublicKey = Text(tls, "publicKey", Text(tls, "password")); p.ShortId = Text(tls, "shortId");
        if (tls?["alpn"] is JsonArray alpn) proxy.Alpn = alpn.Select(a => a!.GetValue<string>()).ToArray();
        if (proxy.Security == "reality") proxy.Path = Text(tls, "spiderX", "/");
        var tcp = (stream?["tcpSettings"] ?? stream?["rawSettings"])?.AsObject();
        Fields(tcp, "header");
        if (tcp?["header"] is JsonObject header) { Fields(header, "type"); if (Text(header, "type", "none") != "none") throw new InvalidDataException("TCP HTTP disguise is unsupported."); }
        var ws = stream?["wsSettings"]?.AsObject(); Fields(ws, "path", "headers", "host");
        if (ws != null)
        {
            proxy.Path = Text(ws, "path", "/"); var headers = ws["headers"]?.AsObject(); Fields(headers, "Host", "host");
            proxy.Host = Text(headers, "Host", Text(headers, "host", Text(ws, "host")));
        }
        var grpc = stream?["grpcSettings"]?.AsObject(); Fields(grpc, "serviceName", "authority", "multiMode");
        if (grpc != null) { proxy.ServiceName = Text(grpc, "serviceName"); proxy.Authority = Text(grpc, "authority"); proxy.MultiMode = grpc["multiMode"]?.GetValue<bool>() ?? false; }
        if (!p.IsValid) throw new InvalidDataException("Unsupported server settings. Supported: IPv4/hostname endpoints; VLESS, VMess, Trojan and Shadowsocks; TCP, WebSocket and gRPC; TLS or REALITY. IPv6-only endpoints and other transports are not yet supported.");
        return p;
    }
    public static async Task<Profile> ResolveAsync(Profile profile, CancellationToken ct)
    {
        if (ImportedProxy.IsV4(profile.Server)) return profile;
        var addresses = await Dns.GetHostAddressesAsync(profile.Server, ct);
        string address = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString()
            ?? throw new InvalidDataException("This server has no IPv4 address. IPv6-only servers are not supported yet.");
        if (profile.Imported != null) profile.Imported.ResolvedAddress = address;
        return profile;
    }
}
