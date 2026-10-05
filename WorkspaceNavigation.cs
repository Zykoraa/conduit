using static WorkTunnel.DashboardTheme;

namespace WorkTunnel;

internal sealed class WorkspaceNavigation : Control, IAnimatedControl
{
    private readonly ConduitWordmark _brand = new();
    private readonly DashboardButton _collapse = new() { Text = "☰", AccessibleName = "Collapse navigation", TabIndex = 0 };
    private readonly List<(string Name, string Icon, DashboardButton Button)> _items = new();
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _motion = new() { Interval = 16 };
    private float _indicator, _target;
    public bool Collapsed { get; private set; }
    public string SelectedPage { get; private set; } = "Overview";
    public event Action<string>? PageSelected;
    public event Action? WidthChanged;
    private float ScaleFactor => Font.Size / 10f * DeviceDpi / 96f;
    public int PreferredWidth => (int)((Collapsed ? 66 : 190) * ScaleFactor);
    public WorkspaceNavigation()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Background; Dock = DockStyle.Fill; Font = new("Segoe UI", 10); TabStop = false;
        _brand.Dock = DockStyle.None; Controls.Add(_brand); Controls.Add(_collapse);
        _collapse.BackColor = Surface; _collapse.ForeColor = Muted;
        _collapse.Click += (_, _) => ToggleCollapsed();
        foreach (var (name, icon) in new[] { ("Overview", "◉"), ("Connections", "↗"), ("Diagnostics", "≋"), ("Settings", "⚙") })
        {
            var button = new DashboardButton { Text = icon + "   " + name, BackColor = Background, ForeColor = Muted, AccessibleName = name, TabIndex = _items.Count + 1 };
            button.Click += (_, _) => { SelectPage(name); PageSelected?.Invoke(name); };
            _tips.SetToolTip(button, name); _items.Add((name, icon, button)); Controls.Add(button);
        }
        _motion.Tick += (_, _) => { _indicator += (_target - _indicator) * .22f; if (!AnimationSettings.Allowed(this) || Math.Abs(_target - _indicator) < .5f) { _indicator = _target; _motion.Stop(); } Invalidate(); };
    }
    public void ToggleCollapsed()
    {
        Collapsed = !Collapsed; _collapse.AccessibleName = Collapsed ? "Expand navigation" : "Collapse navigation";
        foreach (var item in _items) item.Button.Text = Collapsed ? item.Icon : item.Icon + "   " + item.Name;
        PerformLayout(); WidthChanged?.Invoke();
    }
    public void SelectPage(string name)
    {
        SelectedPage = name;
        foreach (var item in _items)
        {
            bool selected = item.Name == name;
            item.Button.BackColor = selected ? BlueTint : Background; item.Button.ForeColor = selected ? Blue : Muted;
            if (selected) _target = item.Button.Top;
        }
        if (AnimationSettings.Allowed(this)) _motion.Start(); else { _indicator = _target; Invalidate(); }
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e); if (_items == null) return;
        float s = ScaleFactor; int inset = (int)(12 * s), height = (int)(48 * s);
        _brand.Visible = !Collapsed; _brand.Bounds = new(inset, (int)(22 * s), Math.Max(1, Width - 2 * inset), (int)(36 * s));
        _collapse.Bounds = new(inset, (int)(78 * s), Math.Max(1, Width - 2 * inset), (int)(34 * s));
        for (int i = 0; i < _items.Count; i++) _items[i].Button.Bounds = new(inset, (int)(140 * s) + i * (height + (int)(10 * s)), Math.Max(1, Width - 2 * inset), height);
        _target = _items.FirstOrDefault(i => i.Name == SelectedPage).Button?.Top ?? 0; _indicator = _target; Invalidate();
    }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); PerformLayout(); WidthChanged?.Invoke(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); using var edge = new Pen(Border); e.Graphics.DrawLine(edge, Width - 1, 0, Width - 1, Height);
        using var ink = new SolidBrush(Blue); e.Graphics.FillRectangle(ink, 2 * ScaleFactor, _indicator + 8 * ScaleFactor, 3 * ScaleFactor, 32 * ScaleFactor);
    }
    public void RefreshMotion() { if (!AnimationSettings.Allowed(this)) { _motion.Stop(); _indicator = _target; Invalidate(); } }
    protected override void Dispose(bool disposing) { if (disposing) { _motion.Dispose(); _tips.Dispose(); } base.Dispose(disposing); }
}
