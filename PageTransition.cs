using System.Diagnostics;
using System.Drawing.Imaging;

namespace WorkTunnel;

/// <summary>Short-lived in-memory transition. Live controls remain the source of truth.</summary>
internal sealed class PageTransition : Control, IAnimatedControl
{
    private readonly Bitmap _before, _after;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Size _capturedSize;
    private PageTransition(Bitmap before, Bitmap after)
    {
        _before = before; _after = after; _capturedSize = after.Size;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Dock = DockStyle.Fill; TabStop = false; BackColor = DashboardTheme.Background;
        _timer.Tick += (_, _) => { if (_elapsed.ElapsedMilliseconds >= 240 || !AnimationSettings.Allowed(this) || Size != _capturedSize) Dispose(); else Invalidate(); };
    }
    public static Bitmap? Snapshot(Control body)
    {
        if (!AnimationSettings.Allowed(body) || body.Width < 1 || body.Height < 1) return null;
        Bitmap? image = null;
        try { image = new Bitmap(body.Width, body.Height); body.DrawToBitmap(image, body.ClientRectangle); return image; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { image?.Dispose(); return null; }
    }
    public static void Show(Control body, Bitmap? before)
    {
        if (before == null) return;
        var after = Snapshot(body);
        if (after == null) { before.Dispose(); return; }
        var transition = new PageTransition(before, after);
        body.Controls.Add(transition); transition.BringToFront(); transition._timer.Start();
    }
    public void RefreshMotion() { if (!AnimationSettings.Allowed(this)) Dispose(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        float t = Math.Clamp(_elapsed.ElapsedMilliseconds / 240f, 0, 1);
        float eased = 1 - MathF.Pow(1 - t, 3);
        void Draw(Bitmap bitmap, float alpha, int offset)
        {
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
            e.Graphics.DrawImage(bitmap, new Rectangle(offset, 0, Width, Height), 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
        }
        Draw(_before, 1 - eased, -(int)(8 * eased * DeviceDpi / 96));
        Draw(_after, eased, (int)(8 * (1 - eased) * DeviceDpi / 96));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _before.Dispose(); _after.Dispose(); }
        base.Dispose(disposing);
    }
}
