using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace WorkTunnel;

internal sealed class UserLaunch : IDisposable
{
    private readonly SafeFileHandle _token;
    public UserLaunch(Process parent)
    {
        var access = TOKEN_ACCESS_MASK.TOKEN_QUERY | TOKEN_ACCESS_MASK.TOKEN_DUPLICATE | TOKEN_ACCESS_MASK.TOKEN_ASSIGN_PRIMARY;
        if (!PInvoke.OpenProcessToken(parent.SafeHandle, access, out var original)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (original)
            if (!PInvoke.DuplicateTokenEx(original, access, null, SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation, TOKEN_TYPE.TokenPrimary, out _token)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public unsafe void Start(string path, bool connect)
    {
        Span<char> command = ("\"" + path + "\"" + (connect ? " --connect" : "") + "\0").ToCharArray();
        var info = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW) };
        if (!PInvoke.CreateProcessWithToken(_token, CREATE_PROCESS_LOGON_FLAGS.LOGON_WITH_PROFILE, path, ref command, 0, null, Path.GetDirectoryName(path), in info, out var process)) throw new Win32Exception(Marshal.GetLastWin32Error());
        PInvoke.CloseHandle(process.hProcess); PInvoke.CloseHandle(process.hThread);
    }
    public void Dispose() => _token.Dispose();
}
