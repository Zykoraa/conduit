namespace WorkTunnel;

/// <summary>
/// A REALITY cover site. Each is a *matched* profile: the server's dest +
/// serverNames and the client's sni all point at the same real public site, so
/// an active DPI probe sees a cert that fits the SNI. <see cref="Key"/> is the
/// server-side profile name used by set-cover.sh.
///
/// Only domains that PASS the live REALITY loopback test belong here
/// (www.microsoft.com and *.sharepoint.com FAIL it; www.apple.com and bamwx.com pass).
/// </summary>
internal sealed record CoverSite(string Key, string Name, string Sni, bool Vetted);

internal static class CoverProfiles
{
    public static readonly IReadOnlyList<CoverSite> All = new[]
    {
        new CoverSite("apple", "Apple",  "www.apple.com", Vetted: true),
        new CoverSite("bamwx", "BAMWX",  "bamwx.com",     Vetted: true),
    };

    public static CoverSite? BySni(string sni) =>
        All.FirstOrDefault(c => string.Equals(c.Sni, sni, StringComparison.OrdinalIgnoreCase));
}
