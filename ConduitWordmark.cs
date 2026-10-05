using System.Drawing.Drawing2D;

namespace WorkTunnel;

/// <summary>Decorative branding only; it never represents tunnel health or traffic.</summary>
internal sealed class ConduitWordmark : Control, IAnimatedControl
{
    public ConduitWordmark()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Dock = DockStyle.Fill; Margin = Padding.Empty; BackColor = Color.Transparent;
        Font = new Font("Segoe UI", 23, FontStyle.Bold);
        Text = "Conduit"; AccessibleName = "Conduit";
        AccessibleDescription = "Connected on your terms. Decorative logo.";
        AccessibleRole = AccessibleRole.StaticText; TabStop = false;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += PreferencesChanged;
    }

    private void PreferencesChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke((Action)Invalidate); } catch (InvalidOperationException) { }
    }

    public void RefreshMotion() => Invalidate();

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 2 || Height < 2) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float size = Math.Min(Height - 4, Font.GetHeight(g) * .9f);
        if (size < 4) return;
        float top = (Height - size) / 2, left = 2;
        Color blue = SystemInformation.HighContrast ? SystemColors.WindowText : DashboardTheme.Blue;
        Color pink = SystemInformation.HighContrast ? SystemColors.WindowText : DashboardTheme.Pink;
        // Static branding avoids continuous redraws when the dashboard is idle.
        for (int ring = 0; ring < 3; ring++)
        {
            float inset = ring * size * .14f, diameter = size - inset * 2;
            var bounds = new RectangleF(left + inset, top + inset, diameter, diameter);
            using var gradient = new LinearGradientBrush(bounds, blue, pink, 35f);
            using var track = new Pen(gradient, Math.Max(1.5f, size * .055f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(track, bounds, 42, 276);
        }

        using var letters = new GraphicsPath();
        letters.AddString("Conduit", Font.FontFamily, (int)Font.Style, Font.SizeInPoints * g.DpiY / 72,
            new PointF(0, 0), StringFormat.GenericTypographic);
        var textBounds = letters.GetBounds();
        float textLeft = size + 12 * size / 34;
        float fit = Math.Min(1, Math.Min((Width - textLeft - 2) / textBounds.Width, (Height - 2) / textBounds.Height));
        if (fit <= 0) return;
        using var position = new Matrix(fit, 0, 0, fit, textLeft - textBounds.Left * fit, (Height - textBounds.Height * fit) / 2 - textBounds.Top * fit);
        letters.Transform(position);
        var word = letters.GetBounds();
        using var ink = new LinearGradientBrush(word, blue, pink, 15f);
        g.FillPath(ink, letters);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= PreferencesChanged;
        base.OnHandleDestroyed(e);
    }
}
