using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace WorkTunnel;

internal static class SecretStore
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static byte[] Protect(byte[] input) => Transform(input, true);
    public static byte[] Unprotect(byte[] input) => Transform(input, false);
    private static byte[] Transform(byte[] input, bool encrypt)
    {
        var blob = new Blob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(input, 0, blob.Data, input.Length);
            bool ok = encrypt ? CryptProtectData(ref blob, "WorkTunnel current-user secret", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Windows could not protect/unlock this user's saved data.");
            byte[] result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (int i = 0; i < blob.Length; i++) Marshal.WriteByte(blob.Data, i, 0);
            Marshal.FreeHGlobal(blob.Data);
            if (output.Data != IntPtr.Zero) { for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
    public static void RestrictDirectory(string path, SecurityIdentifier? readUser = null)
    {
        for (var ancestor = new DirectoryInfo(path); ancestor != null; ancestor = ancestor.Parent)
            if (ancestor.Exists && ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Protected data paths cannot contain links.");
        var directory = Directory.CreateDirectory(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Protected data directory cannot be a link.");
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        if (readUser != null) security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(readUser ?? WindowsIdentity.GetCurrent().User!, readUser == null ? FileSystemRights.FullControl : FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }
    public static void Save(string path, byte[] plaintext)
    {
        RestrictDirectory(Path.GetDirectoryName(path)!);
        byte[] encrypted = Protect(plaintext);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try { File.WriteAllBytes(temp, encrypted); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); CryptographicOperations.ZeroMemory(encrypted); }
    }
}
