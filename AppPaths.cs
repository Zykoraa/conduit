using System.Text;

namespace WorkTunnel;

/// <summary>
/// Resolves every path the app uses. Bundled assets (cores, configs, tools) sit
/// next to the .exe; runtime state (materialized configs, logs) lives under
/// protected ProgramData for the host; user preferences and encrypted identity
/// live in LocalAppData. The installed app directory stays read-only to users.
/// </summary>
internal static class AppPaths
{
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // --- shipped alongside the exe ---
    public static string BaseDir => AppContext.BaseDirectory;
    public static string CoresDir => Path.Combine(BaseDir, "cores");
    public static string XrayExe => Path.Combine(CoresDir, "xray", "xray.exe");
    public static string SingBoxExe => Path.Combine(CoresDir, "sing_box", "sing-box.exe");
    public static string ConfigsDir => Path.Combine(BaseDir, "configs");
    public static string XrayTemplate => Path.Combine(ConfigsDir, "xray.template.json");
    public static string SingBoxConfig => Path.Combine(ConfigsDir, "singbox-tun.json");
    public static string ProfilePath => Path.Combine(ConfigsDir, "profile.json");
    public static string ProtectedProfilePath => Path.Combine(DataDir, "secrets", "profile.dpapi");
    public static string UserSid => System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
    internal static string RuntimeSid { get; set; } = UserSid;
    public static string RuntimeDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkTunnel", "runtime", RuntimeSid);
    public static string DiagScript => Path.Combine(BaseDir, "tools", "tether-diag.ps1");

    // --- per-user runtime state ---
    public static string DataDir
    {
        get
        {
            var d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WorkTunnel");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string LogDir
    {
        get { return Path.Combine(RuntimeDir, "logs"); }
    }

    public static string XrayRunConfig => Path.Combine(RuntimeDir, "xray.run.json");
    public static string SingBoxRunConfig => Path.Combine(RuntimeDir, "singbox.run.json");
    public static string XrayLog => Path.Combine(LogDir, "xray.log");
    public static string SingBoxLog => Path.Combine(LogDir, "sing-box.log");
    public static string IncidentsDir => Path.Combine(RuntimeDir, "incidents");
}
