using Microsoft.Win32;
namespace WorkTunnel;

/// <summary>Opt-in, unelevated dashboard startup. Connection still requires a user-started host.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool IsEnabled() { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue("WorkTunnel") is string; }
    public static bool Enable() { using var key = Registry.CurrentUser.CreateSubKey(RunKey); key.SetValue("WorkTunnel", "\"" + Environment.ProcessPath + "\""); return true; }
    public static bool Disable() { using var key = Registry.CurrentUser.OpenSubKey(RunKey, true); key?.DeleteValue("WorkTunnel", false); return true; }
}
