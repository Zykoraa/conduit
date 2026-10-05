using System.Diagnostics;
using System.Text.Json;

namespace WorkTunnel;

internal static class SettingsSnapshot
{
    public static Dictionary<string, string> Read(bool includeEndpoint = true, bool runtime = false)
    {
        var p = Profile.Load();
        var result = new Dictionary<string, string>
        {
            ["Application"] = "Conduit " + typeof(SettingsSnapshot).Assembly.GetName().Version?.ToString(3),
            ["Configuration"] = runtime ? "Generated runtime files" : "Saved profile and templates",
            ["Server"] = includeEndpoint ? $"{p.Server}:{p.Port}" : "[redacted]",
            ["Cover / SNI"] = includeEndpoint ? p.Sni : "[redacted]",
            ["Protocol"] = "VLESS / REALITY / XTLS Vision",
            ["Credentials"] = "Hidden; connection links are never shown here"
        };
        try
        {
            using var tun = JsonDocument.Parse(File.ReadAllText(runtime ? AppPaths.SingBoxRunConfig : AppPaths.SingBoxConfig));
            var inbound = tun.RootElement.GetProperty("inbounds").EnumerateArray().First(i => i.GetProperty("type").GetString() == "tun");
            result["MTU"] = inbound.GetProperty("mtu").ToString(); result["TUN stack"] = inbound.GetProperty("stack").ToString();
            result["DNS"] = string.Join("; ", tun.RootElement.GetProperty("dns").GetProperty("servers").EnumerateArray().Select(d =>
                $"{d.GetProperty("type")}://{d.GetProperty("server")}{(d.TryGetProperty("path", out var path) ? path.ToString() : "")} via {(d.TryGetProperty("detour", out var detour) ? detour.ToString() : "default route")}"));
            using var xray = JsonDocument.Parse(File.ReadAllText(runtime ? AppPaths.XrayRunConfig : AppPaths.XrayTemplate));
            var mux = xray.RootElement.GetProperty("outbounds")[0].GetProperty("mux");
            result["TCP multiplexing"] = mux.GetProperty("concurrency").GetInt32() < 0 ? "Disabled" : "Enabled";
            result["XUDP concurrency"] = mux.GetProperty("xudpConcurrency").ToString();
            result["QUIC / UDP 443"] = mux.GetProperty("xudpProxyUDP443").ToString();
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { result["Configuration read"] = "Some values are unavailable; save diagnostics for details."; }
        return result;
    }
    public static async Task<string> EngineVersionAsync(string path)
    {
        try
        {
            using var process = new Process { StartInfo = new(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            process.StartInfo.ArgumentList.Add("version"); process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(4000);
            try { await process.WaitForExitAsync(timeout.Token); } catch { try { process.Kill(true); } catch { } throw; }
            await stderr;
            return (await stdout).Split('\n')[0].Trim();
        }
        catch { return "Version unavailable"; }
    }
}
