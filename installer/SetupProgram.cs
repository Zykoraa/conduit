using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using WorkTunnel;

internal static class SetupProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--release-smoke-test") return ReleaseSmokeChecks.Run(args[1], installer: true);
        Application.EnableVisualStyles(); Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        try
        {
            if (args.Length == 1 && args[0] is "--install" or "--repair" or "--uninstall")
            {
                if (!InstallSecurity.IsAdministrator) throw new UnauthorizedAccessException("Setup requires administrator approval.");
                if (args[0] == "--uninstall") Uninstall(); else Install();
                return 0;
            }
            using var form = new Form { Text = "Conduit Setup", ClientSize = new(650, 320), StartPosition = FormStartPosition.CenterScreen,
                Font = new("Segoe UI", 11), BackColor = DashboardTheme.Background, ForeColor = DashboardTheme.Text, Padding = new(24) };
            DashboardTheme.StyleCaption(form);
            var instructions = new Label { Dock = DockStyle.Fill, Text = "Conduit\nConnected on your terms.\n\nThe dashboard runs normally. Only the tunnel host asks for administrator approval. Each device imports its own connection link; this installer contains no enrolled identity.\n\nClose Conduit and stop its tunnel before installing, repairing or removing it. Your encrypted user profile is kept during these operations." };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50 };
            foreach (string action in new[] { "Install", "Repair", "Uninstall" })
            {
                var button = new DashboardButton { Text = action, AutoSize = true, Padding = new(12, 8, 12, 8), BackColor = DashboardTheme.BlueTint, ForeColor = DashboardTheme.Text };
                button.Click += async (_, _) =>
                {
                    foreach (Control c in buttons.Controls) c.Enabled = false;
                    try
                    {
                        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                        start.ArgumentList.Add("--" + action.ToLowerInvariant());
                        using var process = Process.Start(start) ?? throw new IOException("Setup did not start."); await process.WaitForExitAsync();
                        if (process.ExitCode != 0) throw new IOException("Setup did not complete. The earlier error explains why.");
                        instructions.Text = action == "Uninstall" ? "Conduit removed. Your encrypted user profile and diagnostic history were kept." : "Conduit is ready. Open it from the Start menu and import this device's link.\n\nFor an older installation, choose Import previous profile in the first-run screen. The old folder is left untouched.";
                        if (action != "Uninstall") Process.Start(new ProcessStartInfo(Path.Combine(InstallSecurity.InstallRoot, "WorkTunnel.exe")) { UseShellExecute = true });
                    }
                    catch (Exception e) { MessageBox.Show(form, e.Message, "Setup"); }
                    finally { foreach (Control c in buttons.Controls) c.Enabled = true; }
                };
                buttons.Controls.Add(button);
            }
            form.Controls.Add(instructions); form.Controls.Add(buttons); Application.Run(form); return 0;
        }
        catch (Exception e)
        {
            // Keep diagnostic detail in the protected installer directory, including for quiet installs.
            // A unique file avoids overwriting a user-controlled file or following an existing link.
            if (InstallSecurity.IsAdministrator)
            {
                try
                {
                    SecureInstallDirectory(Path.GetDirectoryName(UpdateInstaller.ProtectedRoot)!);
                    SecureInstallDirectory(UpdateInstaller.ProtectedRoot);
                    using var log = new StreamWriter(new FileStream(Path.Combine(UpdateInstaller.ProtectedRoot, "setup-error-" + Guid.NewGuid().ToString("N") + ".txt"), FileMode.CreateNew));
                    log.Write(DiagnosticRedactor.Scrub(e.ToString()));
                }
                catch { /* Reporting must not hide the original install error. */ }
            }
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") Console.Error.WriteLine(e);
            else MessageBox.Show(e.Message, "Conduit Setup");
            return 1;
        }
    }
    private static void EnsureStopped()
    {
        foreach (var process in Process.GetProcessesByName("WorkTunnel"))
            using (process)
                throw new IOException("Conduit (or an older WorkTunnel app) is running. Choose Disconnect and exit in every installation, then retry Setup.");
    }
    private static void SecureInstallDirectory(string path)
    {
        InstallSecurity.ProtectMachineDirectory(path);
    }
    private static void Install()
    {
        EnsureStopped();
        SecureInstallDirectory(Path.GetDirectoryName(UpdateInstaller.ProtectedRoot)!);
        SecureInstallDirectory(UpdateInstaller.ProtectedRoot);
        SecureInstallDirectory(InstallSecurity.InstallRoot);
        string stage = Path.Combine(UpdateInstaller.ProtectedRoot, "setup-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        string bundle = Path.Combine(stage, "release.wtupdate");
        using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("WorkTunnel.payload") ?? throw new IOException("Setup payload is missing."))
        using (var output = File.Create(bundle)) input.CopyTo(output);
        var verified = ReleaseSignature.Unpack(bundle, Path.Combine(stage, "verified"), ReleaseTrust.PublicKey);
        if (verified.Release.Flavor != UpdateInstaller.Flavor) throw new InvalidDataException("Wrong package flavor.");
        if (!Version.TryParse(verified.Release.Version, out var version)) throw new InvalidDataException("Invalid package version.");
        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        string existing = Path.Combine(InstallSecurity.InstallRoot, "WorkTunnel.exe");
        if (File.Exists(existing) && Version.TryParse(FileVersionInfo.GetVersionInfo(existing).FileVersion, out var installed) && version < installed)
            throw new InvalidDataException("This installer is older than the installed app. Download the latest Setup to repair it; use the app's verified rollback to restore an earlier version.");
        string extracted = Path.Combine(stage, "files"); Directory.CreateDirectory(extracted);
        using (var archive = ZipFile.OpenRead(verified.Package))
        {
            if (archive.Entries.Count > 1000 || archive.Entries.Sum(e => e.Length) > 600_000_000) throw new InvalidDataException("Package is too large.");
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                string relative = UpdateInstaller.SafeRelative(entry.FullName);
                if (relative.Equals("configs/profile.json", StringComparison.OrdinalIgnoreCase)) continue;
                string target = UpdateInstaller.Under(extracted, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open(); using var output = new FileStream(target, FileMode.CreateNew); input.CopyTo(output);
            }
        }
        var files = Directory.GetFiles(extracted, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(extracted, p)).ToArray();
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(File.ReadAllText(Path.Combine(extracted, "release-manifest.json")));
        if (manifest?.Version != verified.Release.Version || manifest.Flavor != verified.Release.Flavor) throw new InvalidDataException("Payload metadata mismatch.");
        if (!files.Contains("WorkTunnel.exe") || !files.Contains("cores\\xray\\xray.exe") || !files.Contains("cores\\sing_box\\sing-box.exe")) throw new InvalidDataException("Incomplete package.");
        var plan = new UpdatePlan(extracted, InstallSecurity.InstallRoot, verified.Release.Version, files, files.ToDictionary(f => f, f => UpdateInstaller.Hash(Path.Combine(extracted, f))), 0, 0, false,
            Remove: ["WorkTunnel.pdb", "WorkTunnel.Setup.pdb"]);
        UpdateInstaller.ValidateCoresAsync(plan).GetAwaiter().GetResult();
        UpdateInstaller.ApplyFiles(plan, UpdateInstaller.ProtectedRoot);
        // Retire the former elevated-login shortcut; startup is now explicitly opt-in in the dashboard.
        using (var task = Process.Start(new ProcessStartInfo("schtasks.exe") { Arguments = "/Delete /TN WorkTunnel-AutoStart /F", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
            task?.WaitForExit(5000);
        // Keep a repair/uninstall entrypoint; it is not the running setup image.
        string repairExe = Path.Combine(InstallSecurity.InstallRoot, "WorkTunnel.Setup.exe");
        if (!repairExe.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) File.Copy(Environment.ProcessPath!, repairExe, true);
        using (var uninstall = Registry.LocalMachine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkTunnel-" + UpdateInstaller.Flavor))
        {
            uninstall.SetValue("DisplayName", "Conduit"); uninstall.SetValue("DisplayVersion", verified.Release.Version);
            uninstall.SetValue("DisplayIcon", Path.Combine(InstallSecurity.InstallRoot, "WorkTunnel.exe") + ",0");
            uninstall.SetValue("Publisher", "Zykoraa"); uninstall.SetValue("InstallLocation", InstallSecurity.InstallRoot);
            // Open the setup UI so the user can choose repair or uninstall and approve elevation.
            uninstall.SetValue("UninstallString", "\"" + Path.Combine(InstallSecurity.InstallRoot, "WorkTunnel.Setup.exe") + "\"");
        }
        UpdateShortcut(remove: false);
    }
    private static void Uninstall()
    {
        EnsureStopped(); NetworkLock.Remove();
        string root = Path.GetFullPath(InstallSecurity.InstallRoot);
        if (!root.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\WorkTunnel-", StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected install directory.");
        if (Directory.Exists(root))
        {
            IEnumerable<string> OwnedFiles(string folder)
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(folder))
                {
                    var flags = File.GetAttributes(path);
                    if (flags.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Uninstall found a linked path.");
                    if (flags.HasFlag(FileAttributes.Directory)) { foreach (var child in OwnedFiles(path)) yield return child; }
                    else yield return path;
                }
            }
            var files = OwnedFiles(root).ToArray();
            foreach (string path in files)
                if (Path.GetFileName(path).Equals("WorkTunnel.Setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string retired = Path.Combine(UpdateInstaller.ProtectedRoot, "retired-setup-" + Guid.NewGuid().ToString("N") + ".exe");
                    File.Move(path, retired);
                    if (!MoveFileEx(retired, null, 4)) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                }
                else File.Delete(path);
            foreach (string directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length)) Directory.Delete(directory);
            Directory.Delete(root);
        }
        Registry.LocalMachine.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkTunnel-" + UpdateInstaller.Flavor, false);
        UpdateShortcut(remove: true);
    }

    private static void UpdateShortcut(bool remove)
    {
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        string ownExe = Path.Combine(InstallSecurity.InstallRoot, "WorkTunnel.exe");
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")!; dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            bool PointsTo(string file, string target)
            {
                if (!File.Exists(file)) return false;
                dynamic link = shell.CreateShortcut(file);
                try { return string.Equals((string)link.TargetPath, target, StringComparison.OrdinalIgnoreCase); }
                finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
            }
            // Remove only our earlier branded shortcuts, never an unrelated shortcut.
            foreach (string prefix in new[] { "Conduit ", "WorkTunnel " })
            {
                string old = Path.Combine(programs, prefix + UpdateInstaller.Flavor + ".lnk");
                if (PointsTo(old, ownExe)) File.Delete(old);
            }
            string menu = Path.Combine(programs, "Conduit.lnk");
            string target = ownExe;
            if (remove)
            {
                if (!PointsTo(menu, ownExe)) return;
                string other = UpdateInstaller.Flavor == "Owner" ? "Client" : "Owner";
                target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WorkTunnel-" + other, "WorkTunnel.exe");
                if (!File.Exists(target)) { File.Delete(menu); return; }
            }
            dynamic shortcut = shell.CreateShortcut(menu);
            try
            {
                shortcut.TargetPath = target; shortcut.WorkingDirectory = Path.GetDirectoryName(target);
                shortcut.IconLocation = target + ",0"; shortcut.Description = "Connected on your terms."; shortcut.Save();
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); }
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string source, string? destination, uint flags);
}
