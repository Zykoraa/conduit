using System.Text.Json;
namespace WorkTunnel;

internal static class WindowPreferences
{
    private sealed record Placement(int X, int Y, int Width, int Height);
    public static void Attach(Form form, string name)
    {
        string path = Path.Combine(AppPaths.DataDir, "window-" + name + ".json");
        try
        {
            var saved = JsonSerializer.Deserialize<Placement>(File.ReadAllText(path));
            if (saved != null)
            {
                Rectangle requested = new(saved.X, saved.Y, saved.Width, saved.Height);
                Rectangle area = Screen.FromRectangle(requested).WorkingArea;
                int width = Math.Min(area.Width, Math.Max(form.MinimumSize.Width, saved.Width));
                int height = Math.Min(area.Height, Math.Max(form.MinimumSize.Height, saved.Height));
                form.StartPosition = FormStartPosition.Manual;
                form.Bounds = new(Math.Clamp(saved.X, area.Left, area.Right - width), Math.Clamp(saved.Y, area.Top, area.Bottom - height), width, height);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        void Save()
        {
            Rectangle bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            try { File.WriteAllText(path, JsonSerializer.Serialize(new Placement(bounds.X, bounds.Y, bounds.Width, bounds.Height)), AppPaths.Utf8NoBom); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        form.ResizeEnd += (_, _) => Save(); form.FormClosing += (_, _) => Save();
    }
}
