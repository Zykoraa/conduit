using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.AccessControl;

namespace WorkTunnel;

internal static class InstallSecurity
{
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WorkTunnel-" + UpdateInstaller.Flavor);
    public static void ProtectMachineDirectory(string path)
    {
        for (var d = new DirectoryInfo(path); d != null; d = d.Parent)
            if (d.Exists && d.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Protected machine paths cannot contain links.");
        var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (Directory.Exists(path))
        {
            var owner = new DirectoryInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (!admin.Equals(owner) && !system.Equals(owner)) throw new IOException("An untrusted owner controls the machine data directory. Setup refuses to adopt it: " + path);
        }
        var directory = Directory.CreateDirectory(path);
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(admin);
        foreach (var sid in new[] { admin, system, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, sid.Equals(admin) || sid.Equals(system) ? FileSystemRights.FullControl : FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }
    public static void RequireInstalled()
    {
        if (!Path.GetFullPath(AppPaths.BaseDir).TrimEnd('\\').Equals(InstallRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Install this version with Conduit Setup first. The privileged host only runs from its protected installation folder.");
        for (var d = new DirectoryInfo(InstallRoot); d != null; d = d.Parent)
            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Installation path cannot contain links.");
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    public static string ProcessPath(uint id)
    {
        IntPtr handle = OpenProcess(0x1000, false, id);
        if (handle == IntPtr.Zero) throw new IOException("Cannot verify the tunnel host identity.");
        try { var path = new System.Text.StringBuilder(32768); int size = path.Capacity; if (!QueryFullProcessImageName(handle, 0, path, ref size)) throw new IOException("Cannot verify executable path."); return path.ToString(); }
        finally { CloseHandle(handle); }
    }
}
