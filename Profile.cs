using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkTunnel;

/// <summary>
/// Per-Windows-user identity and active cover. Installers contain no identity;
/// importing a link stores it using current-user DPAPI.
/// </summary>
internal sealed class Profile
{
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("server")] public string Server { get; set; } = "";
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("sni")] public string Sni { get; set; } = "www.apple.com";
    [JsonPropertyName("port")] public int Port { get; set; } = 443;
    [JsonPropertyName("publicKey")] public string PublicKey { get; set; } = "";
    [JsonPropertyName("shortId")] public string ShortId { get; set; } = "";
    [JsonPropertyName("imported")] public ImportedProxy? Imported { get; set; }
    [JsonPropertyName("expectedExitIp")] public string ExpectedExitIp { get; set; } = "";

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static Profile Load()
    {
        try
        {
            if (File.Exists(AppPaths.ProtectedProfilePath))
            {
                byte[] plaintext = SecretStore.Unprotect(File.ReadAllBytes(AppPaths.ProtectedProfilePath));
                try { return JsonSerializer.Deserialize<Profile>(plaintext, Opts) ?? new Profile(); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }
            }
            return new Profile();
        }
        catch
        {
            return new Profile();
        }
    }

    public void Save()
    {
        if (!IsValid) throw new InvalidDataException("Invalid device profile.");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(this, Opts);
        try { SecretStore.Save(AppPaths.ProtectedProfilePath, json); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(json); }
    }

    [JsonIgnore]
    public bool IsValid => ImportedProxy.Bounded(User, 256) && ImportedProxy.Bounded(Server, 253) &&
        ImportedProxy.Bounded(Sni, 253) && ImportedProxy.Bounded(PublicKey, 64) && ImportedProxy.Bounded(ShortId, 16) &&
        (ExpectedExitIp == "" || ImportedProxy.IsV4(ExpectedExitIp)) && Port is > 0 and <= 65535 &&
        (Imported != null ? (ImportedProxy.IsV4(Server) || Uri.CheckHostName(Server) == UriHostNameType.Dns) && Imported.Valid(this) :
        Guid.TryParse(Uuid, out _) && Uri.CheckHostName(Sni) == UriHostNameType.Dns &&
        System.Net.IPAddress.TryParse(Server, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
        Port is > 0 and <= 65535 && (PublicKey.Length == 0 || DeviceLink.ValidKey(PublicKey)) &&
        (ShortId.Length == 0 || DeviceLink.ValidShortId(ShortId)));

    [JsonIgnore]
    public string[] Secrets => [Uuid, PublicKey, ShortId, User, Server, Sni, ExpectedExitIp, Imported?.Credential ?? "", Imported?.Host ?? "", Imported?.Path is { Length: > 1 } path ? path : "", Imported?.ServiceName ?? ""];
}
