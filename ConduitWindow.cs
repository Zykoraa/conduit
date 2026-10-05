using System.Runtime.InteropServices;
using static WorkTunnel.DashboardTheme;

namespace WorkTunnel;

/// <summary>Client-painted chrome with native resize, drag, system menu and Snap hit targets.</summary>
internal class ConduitWindow : Form
{
    private readonly CaptionStrip _caption;
    private int _pressed;
    private bool _layingOut;
    private int CaptionHeight => Math.Max((int)(40 * DeviceDpi / 96f), Font.Height + (int)(14 * DeviceDpi / 96f));
    private bool Resizable => FormBorderStyle is FormBorderStyle.Sizable or FormBorderStyle.SizableToolWindow;
    internal Rectangle MaximizeTarget => _caption.MaximizeBounds;
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseTracking { public int Size, Flags; public IntPtr Window; public int HoverTime; }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref MouseTracking tracking);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    public ConduitWindow()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        _caption = new CaptionStrip(this); Controls.Add(_caption);
        Activated += (_, _) => _caption.Invalidate(); Deactivate += (_, _) => _caption.Invalidate();
        TextChanged += (_, _) => _caption.Invalidate();
        // Keep the standard WS_CAPTION/WS_THICKFRAME styles for native window management.
        // WM_NCCALCSIZE below removes their visual non-client area, not those capabilities.
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1, border = Border.R | Border.G << 8 | Border.B << 16, corners = 2;
        _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
        _ = DwmSetWindowAttribute(Handle, 33, ref corners, sizeof(int));
        var margins = new Margins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        _ = DwmExtendFrameIntoClientArea(Handle, ref margins);
        PerformLayout();
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        if (!_layingOut && _caption != null)
        {
            _layingOut = true;
            try
            {
                var inset = new Padding(1, CaptionHeight, 1, 1);
                if (Padding != inset) Padding = inset;
                _caption.Bounds = new(1, 1, Math.Max(1, ClientSize.Width - 2), CaptionHeight - 1);
                _caption.BringToFront(); _caption.RefreshButtons();
            }
            finally { _layingOut = false; }
        }
        base.OnLayout(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); using var outline = new Pen(Border);
        e.Graphics.DrawRectangle(outline, 0, 0, Math.Max(0, ClientSize.Width - 1), Math.Max(0, ClientSize.Height - 1));
    }
    internal int HitTest(Point point)
    {
        int grip = Math.Max(5, (int)(6 * DeviceDpi / 96f));
        if (Resizable && WindowState != FormWindowState.Maximized)
        {
            bool left = point.X < grip, right = point.X >= ClientSize.Width - grip;
            bool top = point.Y < grip, bottom = point.Y >= ClientSize.Height - grip;
            if (top && left) return 13; if (top && right) return 14;
            if (bottom && left) return 16; if (bottom && right) return 17;
            if (left) return 10; if (right) return 11; if (top) return 12; if (bottom) return 15;
        }
        if (point.Y >= CaptionHeight) return 1; // HTCLIENT
        int button = _caption.HitButton(point);
        return button != 0 ? button : 2; // HTCAPTION supports drag, double-click and right-click menu.
    }
    internal void CaptionAction(int hit)
    {
        if (hit == 8 && MinimizeBox) SendMessageW(Handle, 0x112, (IntPtr)0xF020, IntPtr.Zero);
        else if (hit == 9 && MaximizeBox) SendMessageW(Handle, 0x112, (IntPtr)(WindowState == FormWindowState.Maximized ? 0xF120 : 0xF030), IntPtr.Zero);
        else if (hit == 20 && ControlBox) Close(); // Preserves the dashboard's close-to-tray handler.
    }
    protected override void WndProc(ref Message m)
    {
        // DefWindowProc would otherwise print the standard caption over our custom one.
        if (m.Msg == 0x317) m.LParam = (IntPtr)(m.LParam.ToInt64() & ~2L); // WM_PRINT, clear PRF_NONCLIENT
        if (m.Msg == 0x83) // WM_NCCALCSIZE: entire window becomes our dark client area.
        {
            if (m.LParam != IntPtr.Zero && IsZoomed(Handle))
            {
                var bounds = Marshal.PtrToStructure<NativeRect>(m.LParam);
                var work = Screen.FromRectangle(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)).WorkingArea;
                bounds.Left = work.Left; bounds.Top = work.Top; bounds.Right = work.Right; bounds.Bottom = work.Bottom;
                Marshal.StructureToPtr(bounds, m.LParam, false);
            }
            m.Result = IntPtr.Zero; return;
        }
        if (m.Msg == 0x84 && _caption != null) // WM_NCHITTEST, signed coordinates allow monitors left of the primary display.
        {
            long packed = m.LParam.ToInt64(); var point = PointToClient(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
            m.Result = (IntPtr)HitTest(point); return;
        }
        if (m.Msg == 0xA0) // Forward to Windows for Snap layout hover and request a reliable hover reset.
        {
            _caption?.Hover((int)m.WParam);
            var tracking = new MouseTracking { Size = Marshal.SizeOf<MouseTracking>(), Flags = 0x12, Window = Handle };
            _ = TrackMouseEvent(ref tracking); // TME_LEAVE | TME_NONCLIENT
        }
        if (m.Msg == 0x2A2) { _pressed = 0; _caption?.Hover(0); } // WM_NCMOUSELEAVE
        if (m.Msg == 0xA1 && (int)m.WParam is 8 or 9 or 20) { _pressed = (int)m.WParam; m.Result = IntPtr.Zero; return; }
        if (m.Msg == 0xA2 && (int)m.WParam is 8 or 9 or 20)
        {
            int pressed = _pressed; _pressed = 0;
            if (pressed == (int)m.WParam) CaptionAction(pressed);
            m.Result = IntPtr.Zero; return;
        }
        if (m.Msg is 0x202 or 0x215 or 0x1F) _pressed = 0; // Cancel a release outside the original button.
        base.WndProc(ref m);
    }

    private sealed class CaptionStrip : Control
    {
        private readonly ConduitWindow _owner;
        private readonly CaptionButton _minimize, _maximize, _close;
        public Rectangle MaximizeBounds => _maximize.Bounds with { X = _maximize.Left + Left, Y = _maximize.Top + Top };
        public CaptionStrip(ConduitWindow owner)
        {
            _owner = owner; BackColor = Background; TabStop = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            _minimize = new(owner, 8, "Minimize"); _maximize = new(owner, 9, "Maximize"); _close = new(owner, 20, "Close to tray");
            Controls.AddRange([_minimize, _maximize, _close]);
        }
        public void RefreshButtons()
        {
            _minimize.Visible = _owner.ControlBox && _owner.MinimizeBox; _maximize.Visible = _owner.ControlBox && _owner.MaximizeBox; _close.Visible = _owner.ControlBox;
            _maximize.AccessibleName = _owner.WindowState == FormWindowState.Maximized ? "Restore window" : "Maximize";
            PerformLayout(); Invalidate(true);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); if (_close == null) return;
            int width = (int)(48 * DeviceDpi / 96f), right = Width;
            foreach (var button in new[] { _close, _maximize, _minimize })
                if (button.Visible) { right -= width; button.Bounds = new(right, 0, width, Height); }
        }
        public int HitButton(Point point)
        {
            point.Offset(-Left, -Top);
            if (_close.Visible && _close.Bounds.Contains(point)) return 20;
            if (_maximize.Visible && _maximize.Bounds.Contains(point)) return 9;
            if (_minimize.Visible && _minimize.Bounds.Contains(point)) return 8;
            return 0;
        }
        public void Hover(int hit) { _minimize.SetHover(hit == 8); _maximize.SetHover(hit == 9); _close.SetHover(hit == 20); }
        protected override void WndProc(ref Message m) { if (m.Msg == 0x84) { m.Result = (IntPtr)(-1); return; } base.WndProc(ref m); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int right = Controls.Cast<Control>().Where(c => c.Visible).Select(c => c.Left).DefaultIfEmpty(Width).Min();
            TextRenderer.DrawText(e.Graphics, _owner.Text, Font, new Rectangle((int)(18 * DeviceDpi / 96f), 0, Math.Max(1, right - (int)(32 * DeviceDpi / 96f)), Height), Form.ActiveForm == _owner ? Muted : AnimationSettings.Blend(Muted, Background, .25f), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }
    private sealed class CaptionButton : Button
    {
        private readonly ConduitWindow _owner;
        private readonly int _hit;
        private bool _hover;
        public CaptionButton(ConduitWindow owner, int hit, string name)
        {
            _owner = owner; _hit = hit; AccessibleName = name; AccessibleRole = AccessibleRole.PushButton; TabStop = true; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Click += (_, _) => owner.CaptionAction(hit);
        }
        public void SetHover(bool hover) { if (_hover != hover) { _hover = hover; Invalidate(); } }
        protected override void WndProc(ref Message m) { if (m.Msg == 0x84) { m.Result = (IntPtr)(-1); return; } base.WndProc(ref m); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(_hover ? _hit == 20 ? Color.FromArgb(164, 54, 70) : Surface : Background);
            float dpi = DeviceDpi / 96f, size = 10 * dpi, x = (Width - size) / 2, y = (Height - size) / 2;
            using var pen = new Pen(_hover ? DashboardTheme.Text : Muted, Math.Max(1, dpi));
            if (_hit == 8) e.Graphics.DrawLine(pen, x, y + size / 2, x + size, y + size / 2);
            else if (_hit == 20) { e.Graphics.DrawLine(pen, x, y, x + size, y + size); e.Graphics.DrawLine(pen, x + size, y, x, y + size); }
            else if (_owner.WindowState == FormWindowState.Maximized)
            { e.Graphics.DrawRectangle(pen, x + 2 * dpi, y, size - 2 * dpi, size - 2 * dpi); using var fill = new SolidBrush(_hover ? Surface : Background); e.Graphics.FillRectangle(fill, x, y + 2 * dpi, size - 2 * dpi, size - 2 * dpi); e.Graphics.DrawRectangle(pen, x, y + 2 * dpi, size - 2 * dpi, size - 2 * dpi); }
            else e.Graphics.DrawRectangle(pen, x, y, size, size);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -6, -6), Muted, Background);
        }
    }
}
