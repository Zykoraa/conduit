using System.Text.Json;

namespace WorkTunnel;

internal enum VisualLoad { Balanced, LowPower }

internal static class VisualPerformance
{
    internal static string PathName => Path.Combine(AppPaths.DataDir, "visual-performance.json");
    internal static VisualLoad Read(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 1024) return VisualLoad.Balanced;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.GetProperty("mode").GetString() == "lowPower" ? VisualLoad.LowPower : VisualLoad.Balanced;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException) { return VisualLoad.Balanced; }
    }
    internal static void Write(string path, VisualLoad mode)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(new { mode = mode == VisualLoad.LowPower ? "lowPower" : "balanced" }), AppPaths.Utf8NoBom);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static int SampleInterval(bool visible, bool minimized, bool active, bool compact, bool lowPower) =>
        (!visible || minimized) && !compact ? 0 : !active && !compact ? 5000 : lowPower ? 2000 : 1000;
}
