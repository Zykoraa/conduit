using System.Text.Json;

namespace WorkTunnel;

internal sealed record ConnectionOptions(bool AutoReconnect = true, bool NetworkLock = false);

internal static class Preferences
{
    private static readonly object Gate = new();
    private static string PathName => Path.Combine(AppPaths.DataDir, "preferences.json");
    internal static ConnectionOptions Read(string path)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            return new(!root.TryGetProperty("autoReconnect", out var auto) || auto.GetBoolean(),
                root.TryGetProperty("networkLock", out var networkLock) && networkLock.GetBoolean());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return new(); }
    }
    internal static void Write(string path, ConnectionOptions options)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(new { autoReconnect = options.AutoReconnect, networkLock = options.NetworkLock }), AppPaths.Utf8NoBom);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static bool AutoReconnect() { lock (Gate) return Read(PathName).AutoReconnect; }
    public static bool NetworkLock() { lock (Gate) return Read(PathName).NetworkLock; }
    public static void Save(bool enabled) { lock (Gate) Write(PathName, Read(PathName) with { AutoReconnect = enabled }); }
    public static void SaveNetworkLock(bool enabled) { lock (Gate) Write(PathName, Read(PathName) with { NetworkLock = enabled }); }
}
