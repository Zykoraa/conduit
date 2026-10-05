using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkTunnel;

internal sealed record ReleaseManifest(string Version, string Flavor);
internal sealed record UpdatePlan(string Source, string Target, string Version, string[] Files, Dictionary<string, string> Hashes, int ParentId, long ParentStarted, bool Reconnect, bool Rollback = false, string[]? Remove = null, string? SignedBundle = null);
internal sealed record RollbackInfo(string Source, string Target, string Version, string[] Files, Dictionary<string, string> Hashes, string[] Remove);

internal static class UpdateInstaller
{
#if OWNER_BUILD
    public const string Flavor = "Owner";
#else
    public const string Flavor = "Client";
#endif
    public static string Root => Path.Combine(AppPaths.DataDir, "updates");
    public static string ProtectedRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkTunnel", "updates-" + Flavor);
    public static UpdatePlan PrepareSigned(string bundle, string target, bool reconnect, string? storageRoot = null)
    {
        string storage = storageRoot ?? Root;
        string unpack = Path.Combine(storage, "signed-" + Guid.NewGuid().ToString("N"));
        var verified = ReleaseSignature.Unpack(bundle, unpack, ReleaseTrust.PublicKey);
        var plan = Prepare(verified.Package, verified.Release.PackageSha256, target, reconnect, storage);
        if (plan.Version != verified.Release.Version || verified.Release.Flavor != Flavor) throw new InvalidDataException("Signed metadata does not match the package.");
        return plan with { SignedBundle = Path.GetFullPath(bundle) };
    }
    public static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    internal static string SafeRelative(string name)
    {
        name = name.Replace('\\', '/');
        if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ')))
            throw new InvalidDataException("Unsafe package path.");
        return name;
    }
    internal static string Under(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, SafeRelative(relative)));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escaped its directory.");
        return path;
    }
    private static void NoLinks(string path)
    {
        for (var item = new DirectoryInfo(Path.GetDirectoryName(path)!); item != null; item = item.Parent)
            if (item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Update paths must not use junctions or symlinks.");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Update file is a link.");
    }
    public static UpdatePlan Prepare(string archive, string expectedHash, string target, bool reconnect, string? storageRoot = null)
    {
        using var archiveStream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Regex.IsMatch(expectedHash, "^[a-fA-F0-9]{64}$") || !Convert.ToHexString(SHA256.HashData(archiveStream)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The ZIP does not match the SHA-256 from the trusted GitHub release. Nothing was installed.");
        string storage = storageRoot ?? Root;
        Directory.CreateDirectory(storage);
        string stage = Path.Combine(storage, "stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        archiveStream.Position = 0;
        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 1000 || zip.Entries.Sum(e => e.Length) > 600_000_000) throw new InvalidDataException("Package is unexpectedly large.");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            string relative = SafeRelative(entry.FullName);
            if (!files.Add(relative)) throw new InvalidDataException("Duplicate package path.");
            if (relative.EndsWith(".key", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Private key file in update.");
            string output = Under(stage, relative); NoLinks(output); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var input = entry.Open(); using var file = new FileStream(output, FileMode.CreateNew); input.CopyTo(file);
        }
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(File.ReadAllText(Under(stage, "release-manifest.json"))) ?? throw new InvalidDataException("Missing release manifest.");
        if (manifest.Flavor != Flavor) throw new InvalidDataException($"This is the {Flavor} app. Choose its matching package.");
        if (!Version.TryParse(manifest.Version, out var next) || next <= typeof(UpdateInstaller).Assembly.GetName().Version)
            throw new InvalidDataException("Choose a newer release. Use Roll back for a previous installed version.");
        foreach (string required in new[] { "WorkTunnel.exe", "cores/xray/xray.exe", "cores/sing_box/sing-box.exe", "configs/xray.template.json", "configs/singbox-tun.json" })
            if (!files.Contains(required)) throw new InvalidDataException("Incomplete app-and-engine package.");
        // Never replace the local identity, even with an Owner package's profile.
        files.RemoveWhere(f => f.Equals("configs/profile.json", StringComparison.OrdinalIgnoreCase));
        var hashes = files.ToDictionary(f => f, f => Hash(Under(stage, f)), StringComparer.OrdinalIgnoreCase);
        using var parent = Process.GetCurrentProcess();
        return new(stage, Path.GetFullPath(target), manifest.Version, files.ToArray(), hashes, parent.Id, parent.StartTime.ToUniversalTime().Ticks, reconnect);
    }
    public static UpdatePlan PrepareRollback(string target, bool reconnect, string? storageRoot = null)
    {
        var saved = JsonSerializer.Deserialize<RollbackInfo>(File.ReadAllText(Path.Combine(storageRoot ?? ProtectedRoot, "rollback.json"))) ?? throw new InvalidDataException("No previous version is available.");
        if (!Path.GetFullPath(saved.Target).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Backup belongs to another installation.");
        using var parent = Process.GetCurrentProcess();
        return new(saved.Source, saved.Target, saved.Version, saved.Files, saved.Hashes, parent.Id, parent.StartTime.ToUniversalTime().Ticks, reconnect, true, saved.Remove);
    }
    public static async Task ValidateCoresAsync(UpdatePlan plan, Profile? profile = null)
    {
        if (!Version.TryParse(FileVersionInfo.GetVersionInfo(Under(plan.Source, "WorkTunnel.exe")).FileVersion, out _))
            throw new InvalidDataException("The app executable is damaged or missing version metadata.");
        var p = profile ?? (InstallSecurity.IsAdministrator ? new Profile() : Profile.Load());
        if (!p.IsValid) p = new Profile { User = "Validation", Uuid = "11111111-1111-4111-8111-111111111111", Server = "203.0.113.1", Sni = "www.apple.com" };
        string validationRoot = InstallSecurity.IsAdministrator ? ProtectedRoot : Root;
        Directory.CreateDirectory(validationRoot);
        if (InstallSecurity.IsAdministrator) SecretStore.RestrictDirectory(validationRoot, System.Security.Principal.WindowsIdentity.GetCurrent().User!);
        string xrayConfig = Path.Combine(validationRoot, "validate-" + Guid.NewGuid().ToString("N") + ".json");
        string tunConfig = xrayConfig + ".tun.json";
        try
        {
            File.WriteAllText(xrayConfig, TunnelController.BuildXrayConfig(p, File.ReadAllText(Under(plan.Source, "configs/xray.template.json"))), AppPaths.Utf8NoBom);
            File.WriteAllText(tunConfig, TunnelController.BuildTunConfig(p, File.ReadAllText(Under(plan.Source, "configs/singbox-tun.json"))), AppPaths.Utf8NoBom);
            foreach (var test in new[] { ("cores/xray/xray.exe", new[] { "run", "-test", "-c", xrayConfig }), ("cores/sing_box/sing-box.exe", new[] { "check", "-c", tunConfig }) })
            {
                using var process = new Process { StartInfo = new(Under(plan.Source, test.Item1)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
                foreach (string argument in test.Item2) process.StartInfo.ArgumentList.Add(argument);
                process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                using var deadline = new CancellationTokenSource(15000);
                try { await process.WaitForExitAsync(deadline.Token); } catch { try { process.Kill(true); } catch { } throw; }
                await output; await errors;
                if (process.ExitCode != 0) throw new InvalidDataException("A bundled engine rejected the connection configuration. Nothing was installed.");
            }
        }
        finally { if (File.Exists(xrayConfig)) File.Delete(xrayConfig); if (File.Exists(tunConfig)) File.Delete(tunConfig); }
    }
    public static async Task LaunchAsync(UpdatePlan plan)
    {
        Directory.CreateDirectory(Root);
        string id = Guid.NewGuid().ToString("N"), job = Path.Combine(Root, "job-" + id + ".json");
        string eventName = "Local\\WorkTunnel_Update_" + id;
        using var ready = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        File.WriteAllText(job, JsonSerializer.Serialize(plan), AppPaths.Utf8NoBom);
        var start = new ProcessStartInfo(Path.Combine(AppPaths.BaseDir, "WorkTunnel.exe")) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--prepare-update"); start.ArgumentList.Add(job); start.ArgumentList.Add(eventName); Process.Start(start)?.Dispose();
        if (!await Task.Run(() => ready.WaitOne(60000))) throw new IOException("The installer did not become ready. The running app has not been stopped.");
    }
    public static async Task PrepareWorkerAsync(string job, string readyEvent)
    {
        try
        {
            InstallSecurity.RequireInstalled();
            if (!InstallSecurity.IsAdministrator) throw new UnauthorizedAccessException("The installer requires elevation.");
            var requested = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(job)) ?? throw new InvalidDataException("Missing request.");
            using var parent = Process.GetProcessById(requested.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != requested.ParentStarted || !InstallSecurity.ProcessPath((uint)parent.Id).Equals(Path.Combine(InstallSecurity.InstallRoot, "WorkTunnel.exe"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid parent process.");
            SecretStore.RestrictDirectory(ProtectedRoot, System.Security.Principal.WindowsIdentity.GetCurrent().User!);
            UpdatePlan plan = requested.Rollback ? PrepareRollback(InstallSecurity.InstallRoot, requested.Reconnect)
                : PrepareSigned(requested.SignedBundle ?? throw new InvalidDataException("A signed bundle is required."), InstallSecurity.InstallRoot, requested.Reconnect, ProtectedRoot);
            await ValidateCoresAsync(plan);
            plan = plan with { ParentId = requested.ParentId, ParentStarted = requested.ParentStarted };
            string id = Guid.NewGuid().ToString("N"), helper = Path.Combine(ProtectedRoot, "installer-" + id + ".exe"), protectedJob = Path.Combine(ProtectedRoot, "job-" + id + ".json");
            File.Copy(Path.Combine(AppPaths.BaseDir, "WorkTunnel.exe"), helper);
            File.WriteAllText(protectedJob, JsonSerializer.Serialize(plan), AppPaths.Utf8NoBom);
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(protectedJob); start.ArgumentList.Add(readyEvent); Process.Start(start)?.Dispose();
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Update not started"); }
    }
    public static async Task RunWorkerAsync(string job, string readyEvent)
    {
        try
        {
            if (!InstallSecurity.IsAdministrator || !Path.GetFullPath(job).StartsWith(ProtectedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Invalid updater context.");
            var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(job)) ?? throw new InvalidDataException("Missing update job.");
            using var parent = Process.GetProcessById(plan.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStarted || !string.Equals(parent.MainModule?.FileName, Path.Combine(plan.Target, "WorkTunnel.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The running application does not match the update target.");
            using var relaunch = new UserLaunch(parent);
            using (var signal = EventWaitHandle.OpenExisting(readyEvent)) signal.Set();
            using var timeout = new CancellationTokenSource(45000); await parent.WaitForExitAsync(timeout.Token);
            ApplyFiles(plan, ProtectedRoot);
            relaunch.Start(Path.Combine(plan.Target, "WorkTunnel.exe"), plan.Reconnect);
        }
        catch (Exception error)
        {
            MessageBox.Show("The update could not complete. Open Conduit again.\n\n" + error.Message, "Conduit update", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    internal static void ApplyFiles(UpdatePlan plan, string? storageRoot = null)
    {
        string storage = storageRoot ?? Root;
        string backup = Path.Combine(storage, "backup-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(backup);
        var affected = plan.Files.Concat(plan.Remove ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var previous = new List<string>(); var absent = new List<string>();
        foreach (string relative in affected)
        {
            if (relative.Equals("configs/profile.json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Identity replacement is forbidden.");
            string destination = Under(plan.Target, relative); NoLinks(destination);
            if (plan.Files.Contains(relative))
            {
                string source = Under(plan.Source, relative); NoLinks(source);
                if (!plan.Hashes.TryGetValue(relative, out var hash) || !Hash(source).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Staged file changed; update refused.");
            }
            if (File.Exists(destination)) { string copy = Under(backup, relative); Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(destination, copy); previous.Add(relative); }
            else absent.Add(relative);
        }
        try
        {
            foreach (string relative in plan.Files) { string destination = Under(plan.Target, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(Under(plan.Source, relative), destination, true); }
            foreach (string relative in plan.Remove ?? []) { string destination = Under(plan.Target, relative); if (File.Exists(destination)) File.Delete(destination); }
            var saved = new RollbackInfo(backup, plan.Target, "previous installed version", previous.ToArray(), previous.ToDictionary(f => f, f => Hash(Under(backup, f))), absent.ToArray());
            if (previous.Count > 0)
            {
                string temporary = Path.Combine(storage, "rollback.new.json"); File.WriteAllText(temporary, JsonSerializer.Serialize(saved), AppPaths.Utf8NoBom); File.Move(temporary, Path.Combine(storage, "rollback.json"), true);
            }
        }
        catch
        {
            foreach (string relative in previous) File.Copy(Under(backup, relative), Under(plan.Target, relative), true);
            foreach (string relative in absent) { string file = Under(plan.Target, relative); if (File.Exists(file)) File.Delete(file); }
            throw;
        }
    }
}
