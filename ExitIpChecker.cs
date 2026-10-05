using System.Net;

namespace WorkTunnel;

/// <summary>
/// Verifies the tunnel by fetching our public IP *through the xray SOCKS proxy*
/// (127.0.0.1:10808). A green light means traffic genuinely exits the VPS, not
/// just that the process is running.
/// </summary>
internal sealed class ExitIpChecker
{
    private static readonly Uri ProxyUri = new("socks5://127.0.0.1:10808");

    // A couple of independent echo endpoints in case one is down.
    private static readonly string[] Echos =
    {
        "https://api.ipify.org",
        "https://ifconfig.me/ip",
        "https://icanhazip.com"
    };

    public async Task<string?> GetExitIpAsync(CancellationToken ct = default)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy(ProxyUri),
            UseProxy = true
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Conduit/2.0.2");

        foreach (var url in Echos)
        {
            try
            {
                var body = (await http.GetStringAsync(url, ct)).Trim();
                if (IPAddress.TryParse(body, out var ip))
                    return ip.ToString();
            }
            catch when (!ct.IsCancellationRequested)
            {
                // try the next echo
            }
        }
        return null;
    }
}
