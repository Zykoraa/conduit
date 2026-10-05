using System.Net;

namespace WorkTunnel;

internal static class DeviceLink
{
    public static string Create(Profile profile)
    {
        if (profile.Imported != null) throw new FormatException("This profile was imported from v2rayN. Export its original link from v2rayN; Conduit will not produce a link that loses transport settings.");
        if (!profile.IsValid || string.IsNullOrWhiteSpace(profile.PublicKey) || string.IsNullOrWhiteSpace(profile.ShortId))
            throw new FormatException("The connection profile is incomplete.");
        return $"vless://{profile.Uuid}@{profile.Server}:{profile.Port}?encryption=none&flow=xtls-rprx-vision" +
            $"&security=reality&sni={Uri.EscapeDataString(profile.Sni)}&fp=chrome&pbk={Uri.EscapeDataString(profile.PublicKey)}" +
            $"&sid={Uri.EscapeDataString(profile.ShortId)}&type=tcp#{Uri.EscapeDataString(profile.User)}";
    }
    public static Profile Parse(string text)
    {
        var uri = new Uri(text.Trim());
        if (uri.Scheme != "vless" || !Guid.TryParse(uri.UserInfo, out _) ||
            !IPAddress.TryParse(uri.Host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new FormatException("Use a VLESS link with a device UUID and an IPv4 server address.");
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x.Length == 2 ? Uri.UnescapeDataString(x[1]) : "");
        string Required(string name) => query.TryGetValue(name, out var value) && value.Length > 0
            ? value : throw new FormatException($"Link is missing {name}.");
        if (Required("security") != "reality" || Required("flow") != "xtls-rprx-vision" ||
            Required("type") is not ("tcp" or "raw")) throw new FormatException("This app supports VLESS / REALITY / Vision over TCP.");
        var profile = new Profile { User = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')),
            Server = ip.ToString(), Port = uri.Port > 0 ? uri.Port : 443, Uuid = uri.UserInfo,
            Sni = Required("sni"), PublicKey = Required("pbk"), ShortId = Required("sid") };
        if (!profile.IsValid || !ValidKey(profile.PublicKey) || !ValidShortId(profile.ShortId))
            throw new FormatException("Link contains an invalid server name, public key or short ID.");
        return profile;
    }
    internal static bool ValidKey(string key) => key.Length == 43 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    internal static bool ValidShortId(string id) => id.Length is > 0 and <= 16 && id.Length % 2 == 0 && id.All(Uri.IsHexDigit);
}
