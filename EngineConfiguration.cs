using System.Diagnostics;

namespace WorkTunnel;

internal static class EngineConfiguration
{
    public static async Task ValidateProfileAsync(Profile profile, CancellationToken ct)
    {
        string folder = Path.Combine(AppPaths.DataDir, "validation", Guid.NewGuid().ToString("N"));
        SecretStore.RestrictDirectory(folder);
        string xray = Path.Combine(folder, "xray.json"), tun = Path.Combine(folder, "tun.json");
        try
        {
            File.WriteAllText(xray, TunnelController.BuildXrayConfig(profile, File.ReadAllText(AppPaths.XrayTemplate)), AppPaths.Utf8NoBom);
            File.WriteAllText(tun, TunnelController.BuildTunConfig(profile, File.ReadAllText(AppPaths.SingBoxConfig)), AppPaths.Utf8NoBom);
            await ValidateFilesAsync(xray, tun, ct);
        }
        finally { File.Delete(xray); File.Delete(tun); Directory.Delete(folder); }
    }
    public static async Task ValidateFilesAsync(string xray, string tun, CancellationToken ct)
    {
        foreach (var (exe, args) in new[] { (AppPaths.XrayExe, new[] { "run", "-test", "-c", xray }), (AppPaths.SingBoxExe, new[] { "check", "-c", tun }) })
        {
            using var process = new Process { StartInfo = new(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (string arg in args) process.StartInfo.ArgumentList.Add(arg);
            process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { try { process.Kill(true); } catch { } throw; }
            await output; await errors; // Engine error text can contain credentials. Keep it out of UI/history.
            if (process.ExitCode != 0) throw new InvalidDataException("A bundled engine rejected these server settings. Your saved profile has not been replaced. Check the transport in v2rayN.");
        }
    }
}
