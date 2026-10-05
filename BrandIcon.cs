using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WorkTunnel;

internal static class BrandIcon
{
    public static Icon Load()
    {
        using var stream = typeof(BrandIcon).Assembly.GetManifestResourceStream("Conduit.ico")
            ?? throw new InvalidOperationException("The Conduit icon is missing from this build.");
        using var icon = new Icon(stream, 32, 32);
        return (Icon)icon.Clone();
    }

    public static Icon WithStatus(Color status)
    {
        using var icon = Load();
        using var bitmap = icon.ToBitmap();
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var border = new SolidBrush(DashboardTheme.Background);
            using var fill = new SolidBrush(status);
            g.FillEllipse(border, 21, 21, 11, 11);
            g.FillEllipse(fill, 23, 23, 7, 7);
        }
        IntPtr handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
