using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WorkTunnel;

internal static class DashboardTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    public static void StyleCaption(Form form)
    {
        var icon = BrandIcon.Load(); form.Icon = icon;
        form.Disposed += (_, _) => icon.Dispose();
        form.HandleCreated += (_, _) =>
        {
        int dark = 1, caption = Background.R | (Background.G << 8) | (Background.B << 16), text = Text.R | (Text.G << 8) | (Text.B << 16);
        _ = DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(form.Handle, 36, ref text, sizeof(int));
        };
    }
    public static readonly Color Background = Color.FromArgb(12, 15, 21);
    public static readonly Color Surface = Color.FromArgb(23, 28, 37);
    public static readonly Color Border = Color.FromArgb(48, 58, 73);
    public static readonly Color Text = Color.FromArgb(235, 239, 246);
    public static readonly Color Muted = Color.FromArgb(155, 167, 185);
    public static readonly Color Blue = Color.FromArgb(144, 167, 255);
    public static readonly Color Pink = Color.FromArgb(111, 220, 228); // Secondary data trace: cyan.
    public static readonly Color AccentBlue = Color.FromArgb(144, 167, 255);
    public static readonly Color AccentPink = Color.FromArgb(144, 167, 255);
    public static readonly Color BlueTint = Color.FromArgb(20, 29, 46);
    public static readonly Color PinkTint = Color.FromArgb(28, 26, 43);
    public static readonly Color Healthy = Color.FromArgb(111, 220, 228);
    public static readonly Color Amber = Color.FromArgb(216, 183, 135);
    public static readonly Color Red = Color.FromArgb(222, 148, 164);

    public static Label Label(string text, float size = 10, Color? color = null, bool bold = false) => new()
    {
        Text = text, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = color ?? Text, BackColor = Color.Transparent,
        Font = new Font("Segoe UI Variable Text", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = Padding.Empty
    };

    public static TableLayoutPanel Grid(params float[] columns)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty, ColumnCount = columns.Length };
        foreach (float width in columns) grid.ColumnStyles.Add(new(SizeType.Percent, width));
        if (columns.Length > 1) { grid.RowCount = 1; grid.RowStyles.Add(new(SizeType.Percent, 100)); }
        return grid;
    }
}

internal class DashboardSurface : Panel
{
    public DashboardSurface()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = DashboardTheme.Surface; Dock = DockStyle.Fill;
        Padding = new(16); Margin = new(0, 0, 12, 12);
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? DashboardTheme.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float radius = 12 * DeviceDpi / 96f;
        var bounds = new RectangleF(.5f, .5f, Math.Max(1, Width - 3), Math.Max(1, Height - 5));
        using var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, radius, radius, 180, 90);
        path.AddArc(bounds.Right - radius, bounds.Top, radius, radius, 270, 90);
        path.AddArc(bounds.Right - radius, bounds.Bottom - radius, radius, radius, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - radius, radius, radius, 90, 90);
        path.CloseFigure();
        var shadowState = e.Graphics.Save();
        e.Graphics.TranslateTransform(0, 3 * DeviceDpi / 96f);
        using (var shadow = new Pen(Color.FromArgb(65, Color.Black), 4)) e.Graphics.DrawPath(shadow, path);
        e.Graphics.Restore(shadowState);
        using var brush = new LinearGradientBrush(bounds, AnimationSettings.Blend(BackColor, Color.White, .025f), BackColor, 90);
        using var pen = new Pen(DashboardTheme.Border);
        e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path);
        using var highlight = new Pen(Color.FromArgb(22, DashboardTheme.Text));
        e.Graphics.DrawLine(highlight, radius, 1, Width - radius, 1);
    }
}

internal sealed class DashboardMetric : DashboardSurface
{
    private readonly System.Windows.Forms.Timer _highlight = new() { Interval = 25 };
    private float _flash;
    private readonly TableLayoutPanel _grid;
    private readonly Label _title;
    private readonly Label _value = DashboardTheme.Label("—", 14, bold: true);
    private readonly Label _detail = DashboardTheme.Label("Waiting for data", 9, DashboardTheme.Muted);
    private readonly ToolTip _tip = new();
    public DashboardMetric(string title, float valueSize = 14)
    {
        _value.Font = new("Bahnschrift", valueSize, FontStyle.Regular);
        Padding = new(18, 13, 18, 13);
        _title = DashboardTheme.Label(title, 8, DashboardTheme.Muted, true);
        _grid = DashboardTheme.Grid(100); _grid.RowCount = 3;
        foreach (var label in new[] { _title, _value, _detail })
        {
            _grid.RowStyles.Add(new(SizeType.Absolute));
            _grid.Controls.Add(label, 0, _grid.Controls.Count);
            label.FontChanged += (_, _) => { PerformLayout(); FindForm()?.PerformLayout(); };
        }
        Controls.Add(_grid);
        _highlight.Tick += (_, _) =>
        {
            _flash = AnimationSettings.Allowed(this) ? Math.Max(0, _flash - .055f) : 0;
            Invalidate(); if (_flash == 0) _highlight.Stop();
        };
    }
    private static int LineHeight(Label label) => TextRenderer.MeasureText("Ag", label.Font, Size.Empty, TextFormatFlags.SingleLine).Height + 2;
    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width,
        Padding.Vertical + LineHeight(_title) + LineHeight(_value) + LineHeight(_detail));
    protected override void OnLayout(LayoutEventArgs e)
    {
        if (_grid != null)
        {
            _grid.RowStyles[0].Height = LineHeight(_title);
            _grid.RowStyles[1].Height = LineHeight(_value);
            _grid.RowStyles[2].Height = LineHeight(_detail);
        }
        base.OnLayout(e);
    }
    public void UpdateValue(string value, string detail, Color? color = null)
    {
        if (_value.Text == value && _detail.Text == detail && _value.ForeColor == (color ?? DashboardTheme.Text)) return;
        if (_value.Text != value && AnimationSettings.Allowed(this)) { _flash = 1; _highlight.Start(); }
        _value.Text = value; _detail.Text = detail; _value.ForeColor = color ?? DashboardTheme.Text;
        _tip.SetToolTip(_value, value); _tip.SetToolTip(_detail, detail);
        AccessibleName = value + ". " + detail;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float dpi = DeviceDpi / 96f;
        using var accent = new Pen(Color.FromArgb((int)(55 + 170 * _flash), _value.ForeColor), 2 * dpi);
        e.Graphics.DrawLine(accent, 5 * dpi, 14 * dpi, 5 * dpi, Math.Max(14 * dpi, Height - 14 * dpi));
        if (_flash > 0)
        {
            using var glow = new Pen(Color.FromArgb((int)(85 * _flash), DashboardTheme.Blue), 2 * dpi);
            e.Graphics.DrawLine(glow, 15 * dpi, Height - 5 * dpi, (15 + (Width / dpi - 30) * (1 - _flash)) * dpi, Height - 5 * dpi);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) { _tip.Dispose(); _highlight.Dispose(); } base.Dispose(disposing); }
}

internal sealed class AnimatedStatusLabel : Label
{
    private readonly System.Windows.Forms.Timer _transition = new() { Interval = 25 };
    private Color _target, _start;
    private float _progress = 1;
    public AnimatedStatusLabel()
    {
        Dock = DockStyle.Fill; AutoEllipsis = true; TextAlign = ContentAlignment.MiddleLeft;
        BackColor = Color.Transparent; Margin = Padding.Empty;
        _transition.Tick += (_, _) =>
        {
            _progress = AnimationSettings.Allowed(this) ? Math.Min(1, _progress + .07f) : 1;
            ForeColor = AnimationSettings.Blend(_start, _target, 1 - MathF.Pow(1 - _progress, 3));
            if (_progress == 1) _transition.Stop();
        };
    }
    public void SetStatus(string text, Color color)
    {
        Text = text; // The current status stays readable throughout the color transition.
        if (_target == color) return;
        _target = color; _start = ForeColor;
        if (!AnimationSettings.Allowed(this)) { ForeColor = color; _transition.Stop(); return; }
        _progress = 0; _transition.Start();
    }
    protected override void Dispose(bool disposing) { if (disposing) _transition.Dispose(); base.Dispose(disposing); }
}

internal sealed class DashboardChart : Control
{
    private readonly ToolTip _tip = new() { InitialDelay = 100, ReshowDelay = 50 };
    private int _selected = -1;
    private readonly System.Windows.Forms.Timer _transition = new() { Interval = 25 };
    private float _progress = 1;
    private readonly List<(double? first, double? second)> _samples = new();
    private readonly int _capacity;
    private readonly bool _traffic;
    public DashboardChart(bool traffic, int capacity)
    {
        _traffic = traffic; _capacity = capacity;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Fill; BackColor = DashboardTheme.Surface; ForeColor = DashboardTheme.Muted;
        Font = new("Segoe UI", 8); AccessibleName = traffic ? "Tunnel download and upload history" : "Internet check latency history";
        TabStop = true;
        _transition.Tick += (_, _) =>
        {
            _progress = AnimationSettings.Allowed(this) ? Math.Min(1, _progress + .12f) : 1;
            Invalidate(); if (_progress >= 1) _transition.Stop();
        };
    }
    private string SampleText(int index)
    {
        var sample = _samples[index];
        string value = _traffic ? $"RX {(sample.first.HasValue ? TrafficMeter.Rate(sample.first.Value) : "unavailable")} · TX {(sample.second.HasValue ? TrafficMeter.Rate(sample.second.Value) : "unavailable")}" :
            sample.first.HasValue ? $"HTTPS {sample.first:0} ms" : "Measurement unavailable";
        return $"{_samples.Count - 1 - index} samples ago · {value}";
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = (int)Math.Round((e.X - 1f) / Math.Max(1, Width - 3) * (_capacity - 1)) - (_capacity - _samples.Count);
        index = index >= 0 && index < _samples.Count ? index : -1;
        if (_selected == index) return;
        _selected = index; _tip.SetToolTip(this, index < 0 ? "" : SampleText(index)); Invalidate();
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _selected = -1; Invalidate(); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_samples.Count == 0 || e.KeyCode is not (Keys.Left or Keys.Right)) return;
        _selected = Math.Clamp((_selected < 0 ? _samples.Count - 1 : _selected) + (e.KeyCode == Keys.Left ? -1 : 1), 0, _samples.Count - 1);
        AccessibleDescription = SampleText(_selected); _tip.Show(AccessibleDescription, this, Width / 3, Height / 2, 3000); e.Handled = true; Invalidate();
    }
    public void Add(double? first, double? second = null)
    {
        _samples.Add((first, second)); if (_samples.Count > _capacity) _samples.RemoveAt(0); Invalidate();
        _progress = AnimationSettings.Allowed(this) ? 0 : 1; if (_progress < 1) _transition.Start();
        AccessibleDescription = first.HasValue ? _traffic ? $"Latest receive {TrafficMeter.Rate(first.Value)}, transmit {TrafficMeter.Rate(second ?? 0)}" : $"Latest HTTPS check {first:0} milliseconds" : "Latest measurement unavailable; chart contains a gap.";
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float dpi = DeviceDpi / 96f;
        var area = new RectangleF(1, 25 * dpi, Math.Max(1, Width - 3), Math.Max(1, Height - 48 * dpi));
        var values = _samples.SelectMany(s => new[] { s.first, s.second }).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        double ceiling = values.Length == 0 ? 1 : Math.Max(_traffic ? 1024 : 10, values.Max() * 1.15);
        using var grid = new Pen(Color.FromArgb(100, DashboardTheme.Border)) { DashStyle = DashStyle.Dot };
        for (int i = 0; i < 4; i++) g.DrawLine(grid, area.Left, area.Top + area.Height * i / 3, area.Right, area.Top + area.Height * i / 3);
        string max = _traffic ? TrafficMeter.Rate(ceiling) : $"{ceiling:0} ms";
        TextRenderer.DrawText(g, values.Length == 0 ? "Waiting for samples" : "Scale  " + max, Font, new Rectangle(0, 0, Width, (int)(24 * dpi)), ForeColor, TextFormatFlags.Right);
        if (values.Length == 0)
            TextRenderer.DrawText(g, "Real measurements appear while connected", Font, Rectangle.Round(area), ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        else
        {
            DrawSeries(g, area, ceiling, false, DashboardTheme.Blue);
            if (_traffic) DrawSeries(g, area, ceiling, true, DashboardTheme.Pink);
        }
        if (_selected >= 0 && _selected < _samples.Count)
        {
            float x = area.Left + area.Width * (_capacity - _samples.Count + _selected) / Math.Max(1, _capacity - 1);
            using var cursor = new Pen(Color.FromArgb(160, DashboardTheme.Muted)) { DashStyle = DashStyle.Dash };
            g.DrawLine(cursor, x, area.Top, x, area.Bottom);
        }
        TextRenderer.DrawText(g, "Hover or use arrows to inspect · gaps = unavailable", Font, new Rectangle(0, Height - (int)(20 * dpi), Math.Max(1, Width - 38), (int)(20 * dpi)), ForeColor, TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, "Now", Font, new Rectangle(0, Height - (int)(20 * dpi), Width, (int)(20 * dpi)), ForeColor, TextFormatFlags.Right);
    }
    private void DrawSeries(Graphics g, RectangleF area, double max, bool second, Color color)
    {
        var saved = g.Save(); g.SetClip(area);
        using var pen = new Pen(color, 1.8f * DeviceDpi / 96f);
        using var halo = new Pen(Color.FromArgb(28, color), 5 * DeviceDpi / 96f);
        using var shading = new LinearGradientBrush(area, Color.FromArgb(second ? 22 : 42, color), Color.FromArgb(0, color), 90);
        var segment = new List<PointF>();
        void Flush()
        {
            if (segment.Count >= 2)
            {
                using var fill = new GraphicsPath(); fill.AddLines(segment.ToArray());
                fill.AddLine(segment[^1], new PointF(segment[^1].X, area.Bottom));
                fill.AddLine(new PointF(segment[^1].X, area.Bottom), new PointF(segment[0].X, area.Bottom)); fill.CloseFigure();
                g.FillPath(shading, fill); g.DrawLines(halo, segment.ToArray()); g.DrawLines(pen, segment.ToArray());
            }
            else if (segment.Count == 1) { using var dot = new SolidBrush(color); g.FillEllipse(dot, segment[0].X - 2, segment[0].Y - 2, 4, 4); }
            segment.Clear();
        }
        for (int i = 0; i < _samples.Count; i++)
        {
            double? value = second ? _samples[i].second : _samples[i].first;
            if (!value.HasValue) { Flush(); continue; }
            float x = area.Left + area.Width * (_capacity - _samples.Count + i + (1 - _progress)) / Math.Max(1, _capacity - 1);
            var point = new PointF(x, area.Bottom - (float)(value.Value / max) * area.Height);
            segment.Add(point);
        }
        Flush();
        g.Restore(saved);
    }
    protected override void Dispose(bool disposing) { if (disposing) { _transition.Dispose(); _tip.Dispose(); } base.Dispose(disposing); }
}

internal sealed class DashboardButton : Button
{
    private readonly System.Windows.Forms.Timer _transition = new() { Interval = 25 };
    private float _hover;
    private bool _over;
    private float _ripple = 1;
    private Point _origin;
    private bool _pressed;
    public DashboardButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        _transition.Tick += (_, _) =>
        {
            float target = _over ? 1 : 0;
            _hover = AnimationSettings.Allowed(this) ? Math.Clamp(_hover + (_over ? .2f : -.2f), 0, 1) : target;
            _ripple = AnimationSettings.Allowed(this) ? Math.Min(1, _ripple + .055f) : 1;
            Invalidate(); if (_hover == target && _ripple == 1) _transition.Stop();
        };
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _over = true; _transition.Start(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _over = false; _transition.Start(); }
    private void Ripple(Point point) { if (AnimationSettings.Allowed(this)) { _origin = point; _ripple = 0; _transition.Start(); } }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _pressed = true; Ripple(e.Location); Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; base.OnMouseUp(e); Invalidate(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { _pressed = false; base.OnMouseCaptureChanged(e); Invalidate(); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode is Keys.Space or Keys.Enter) { _pressed = true; Ripple(new(Width / 2, Height / 2)); Invalidate(); } }
    protected override void OnKeyUp(KeyEventArgs e) { _pressed = false; base.OnKeyUp(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { _pressed = false; base.OnLostFocus(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? DashboardTheme.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = new GraphicsPath();
        float radius = Math.Min(14 * DeviceDpi / 96f, Math.Min(Width - 2, Height - 2));
        if (radius < 1) return;
        shape.AddArc(.5f, .5f, radius, radius, 180, 90);
        shape.AddArc(Width - radius - 1, .5f, radius, radius, 270, 90);
        shape.AddArc(Width - radius - 1, Height - radius - 1, radius, radius, 0, 90);
        shape.AddArc(.5f, Height - radius - 1, radius, radius, 90, 90); shape.CloseFigure();
        int brighten = Enabled ? (int)(_hover * 18) : 0;
        using var fill = new SolidBrush(Color.FromArgb(Math.Min(255, BackColor.R + brighten), Math.Min(255, BackColor.G + brighten), Math.Min(255, BackColor.B + brighten)));
        e.Graphics.FillPath(fill, shape);
        var saved = e.Graphics.Save(); e.Graphics.SetClip(shape);
        if (_hover > 0)
        {
            using var light = new LinearGradientBrush(ClientRectangle, Color.FromArgb((int)(22 * _hover), DashboardTheme.Blue), Color.Transparent, 0f);
            e.Graphics.FillRectangle(light, ClientRectangle);
        }
        if (_ripple < 1)
        {
            float reach = Math.Max(Width, Height) * (1 - MathF.Pow(1 - _ripple, 3));
            using var ripple = new SolidBrush(Color.FromArgb((int)(35 * (1 - _ripple)), DashboardTheme.Text));
            e.Graphics.FillEllipse(ripple, _origin.X - reach, _origin.Y - reach, reach * 2, reach * 2);
        }
        e.Graphics.Restore(saved);
        using var pen = new Pen(Focused ? DashboardTheme.Blue : DashboardTheme.Border);
        e.Graphics.DrawPath(pen, shape);
        Color disabled = BackColor.GetBrightness() > .45 ? DashboardTheme.Background : DashboardTheme.Muted;
        var textBounds = ClientRectangle;
        if (_pressed && Enabled) textBounds.Offset(0, Math.Max(1, DeviceDpi / 96));
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, Enabled ? ForeColor : disabled,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), ForeColor, BackColor);
    }
    protected override void Dispose(bool disposing) { if (disposing) _transition.Dispose(); base.Dispose(disposing); }
}
