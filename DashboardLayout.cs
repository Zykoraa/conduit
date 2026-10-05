using static WorkTunnel.DashboardTheme;

namespace WorkTunnel;

internal sealed partial class DashboardForm
{
    private readonly WorkspaceNavigation _navigation = new();
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill };
    private readonly Label _connectionNote = Label("Live destinations stay on this PC. Select a row to inspect its traffic.", 10, Muted);
    private readonly ConnectionInspector _inspector = new() { Visible = false };
    private readonly Dictionary<string, Panel> _pages = new();
    private readonly System.Windows.Forms.Timer _inspectorMotion = new() { Interval = 16 };
    private TableLayoutPanel _workspaceColumns = null!;
    private TableLayoutPanel _shell = null!;
    private ToolsForm _diagnosticTools = null!, _settingsTools = null!;
    private bool _updatingDestinations, _inspectorOpen;
    private float UiScale => Font.Size / 10f * DeviceDpi / 96f;
    private float InspectorWidth => Math.Min(340 * UiScale, _workspaceColumns.ClientSize.Width * .43f);

    private void BuildWorkspace()
    {
        _shell = Grid(100, 100); _shell.ColumnStyles[0] = new(SizeType.Absolute, _navigation.PreferredWidth);
        _shell.ColumnStyles[1] = new(SizeType.Percent, 100); Controls.Add(_shell); _shell.Controls.Add(_navigation, 0, 0);
        _navigation.WidthChanged += () => { _shell.ColumnStyles[0].Width = _navigation.PreferredWidth; };
        Layout += (_, _) => { if (Math.Abs(_shell.ColumnStyles[0].Width - _navigation.PreferredWidth) > .5f) _shell.ColumnStyles[0].Width = _navigation.PreferredWidth; };
        _navigation.PageSelected += Navigate;
        var workspace = Grid(100); workspace.Padding = new(20, 16, 16, 12); workspace.RowCount = 2;
        workspace.RowStyles.Add(new(SizeType.AutoSize)); workspace.RowStyles.Add(new(SizeType.Percent, 100)); _shell.Controls.Add(workspace, 1, 0);

        var status = new DashboardSurface { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = new(0, 0, 0, 14) };
        var statusGrid = Grid(100, 0); statusGrid.AutoSize = true; statusGrid.AutoSizeMode = AutoSizeMode.GrowAndShrink; statusGrid.Dock = DockStyle.Top;
        statusGrid.ColumnStyles[0] = new(SizeType.Percent, 100); statusGrid.ColumnStyles[1] = new(SizeType.Absolute, 164);
        statusGrid.RowCount = 3; for (int i = 0; i < 3; i++) statusGrid.RowStyles.Add(new(SizeType.AutoSize));
        _headline.Font = new("Segoe UI Variable Display", 23); _headline.AutoSize = true;
        _headline.Padding = new(0, 0, 10, 8); statusGrid.Controls.Add(_headline, 0, 0);
        _connect = ActionButton("Connect", async () =>
        {
            if (_controller.ConnectionRequested) await _controller.DisconnectAsync();
            else if (!Profile.Load().IsValid) ImportProfile(); else await _controller.ConnectAsync();
        }, false);
        _connect.Dock = DockStyle.Fill; _connect.Margin = new(8, 0, 0, 4); _connect.Font = new("Segoe UI", 12, FontStyle.Bold);
        statusGrid.Controls.Add(_connect, 1, 0);
        _status.AutoSize = true; _status.Padding = new(0, 2, 0, 8); statusGrid.Controls.Add(_status, 0, 1); statusGrid.SetColumnSpan(_status, 2);
        _lockHint.Font = new("Segoe UI", 9, FontStyle.Bold); _lockHint.AutoSize = true; statusGrid.Controls.Add(_lockHint, 0, 2);
        _session.TextAlign = ContentAlignment.MiddleRight; _session.AutoSize = true; statusGrid.Controls.Add(_session, 1, 2);
        status.Controls.Add(statusGrid); workspace.Controls.Add(status, 0, 0);
        _workspaceColumns = Grid(100, 0); _workspaceColumns.ColumnStyles[0] = new(SizeType.Percent, 100); _workspaceColumns.ColumnStyles[1] = new(SizeType.Absolute, 0);
        _workspaceColumns.Controls.Add(_pageHost, 0, 0); _workspaceColumns.Controls.Add(_inspector, 1, 0); workspace.Controls.Add(_workspaceColumns, 0, 1);

        Panel Page(string name)
        {
            var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Background, Visible = false };
            _pages[name] = page; _pageHost.Controls.Add(page); return page;
        }
        var overview = Page("Overview"); var home = Grid(100); home.Dock = DockStyle.Top; home.RowCount = 4;
        home.RowStyles.Add(new(SizeType.Absolute, 36)); home.RowStyles.Add(new(SizeType.Absolute, 110)); home.RowStyles.Add(new(SizeType.Percent, 100)); home.RowStyles.Add(new(SizeType.Absolute, 54));
        _profile.Padding = new(4, 0, 0, 0); home.Controls.Add(_profile, 0, 0);
        var hero = ChartPanel("TUNNEL TRAFFIC", "Measured blue RX / cyan TX", _trafficChart); home.Controls.Add(hero, 0, 2);
        var stats = Grid(33.33f, 33.33f, 33.34f);
        DashboardMetric[] metrics = [_exit, _latency, _speed]; foreach (var metric in metrics) stats.Controls.Add(metric);
        home.Controls.Add(stats, 0, 1);
        var homeActions = Flow();
        _check = ActionButton("Check now", async () => await _controller.CheckNowAsync()); homeActions.Controls.Add(_check);
        homeActions.Controls.Add(ActionButton("Reconnect", async () => await _controller.ReconnectAsync()));
        homeActions.Controls.Add(ActionButton("Live connections", () => { Navigate("Connections"); return Task.CompletedTask; }));
        homeActions.Controls.Add(ActionButton("Privacy settings", () => { Navigate("Settings"); return Task.CompletedTask; }));
        home.Controls.Add(homeActions, 0, 3); overview.Controls.Add(home);
        bool fitting = false;
        void FitHome()
        {
            if (fitting || IsDisposed) return; fitting = true;
            try
            {
                int columns = overview.ClientSize.Width < 650 * UiScale ? 1 : 3;
                if (stats.ColumnCount != columns || stats.RowCount != 3 / columns)
                {
                    stats.SuspendLayout(); stats.ColumnCount = columns; stats.RowCount = 3 / columns; stats.ColumnStyles.Clear(); stats.RowStyles.Clear();
                    for (int i = 0; i < columns; i++) stats.ColumnStyles.Add(new(SizeType.Percent, 100f / columns));
                    for (int i = 0; i < stats.RowCount; i++) stats.RowStyles.Add(new(SizeType.Percent, 100f / stats.RowCount));
                    for (int i = 0; i < metrics.Length; i++) stats.SetCellPosition(metrics[i], new(i % columns, i / columns)); stats.ResumeLayout();
                }
                int metricHeight = metrics.Max(m => m.GetPreferredSize(Size.Empty).Height + m.Margin.Vertical);
                home.RowStyles[0].Height = Math.Max(_profile.PreferredSize.Height, 30 * UiScale);
                home.RowStyles[1].Height = metricHeight * stats.RowCount;
                homeActions.PerformLayout();
                home.RowStyles[3].Height = Math.Max(52 * UiScale, Math.Max(homeActions.GetPreferredSize(new(Math.Max(1, overview.ClientSize.Width), 0)).Height,
                    homeActions.Controls.Cast<Control>().Select(c => c.Bottom + c.Margin.Bottom).DefaultIfEmpty().Max() + homeActions.Padding.Bottom + homeActions.Margin.Vertical));
                home.Height = Math.Max(overview.ClientSize.Height, (int)(home.RowStyles[0].Height + home.RowStyles[1].Height + home.RowStyles[3].Height + 220 * UiScale));
            }
            finally { fitting = false; }
        }
        overview.Layout += (_, _) => FitHome(); FontChanged += (_, _) => FitHome(); DpiChanged += (_, _) => FitHome();

        var connections = Page("Connections"); var connectionLayout = Grid(100); connectionLayout.Dock = DockStyle.Top; connectionLayout.Height = 670;
        connectionLayout.RowCount = 2; connectionLayout.RowStyles.Add(new(SizeType.Absolute, 52)); connectionLayout.RowStyles.Add(new(SizeType.Percent, 100));
        connectionLayout.Controls.Add(_connectionNote, 0, 0);
        var destinationPanel = new DashboardSurface { Padding = new(12), Margin = new(0, 0, 0, 12) }; destinationPanel.Controls.Add(_destinations);
        _destinations.Columns.Add("Destination", 260); _destinations.Columns.Add("Protocol / activity", 150); _destinations.Columns.Add("Transferred", 130);
        _destinations.AccessibleName = "Live destinations from Conduit's tunnel"; _destinations.MultiSelect = false; _destinations.HideSelection = false;
        _destinations.OwnerDraw = true;
        _destinations.DrawColumnHeader += (_, e) =>
        {
            using var fill = new SolidBrush(BlueTint); e.Graphics.FillRectangle(fill, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", _destinations.Font, Rectangle.Inflate(e.Bounds, -4, 0), Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        _destinations.DrawItem += (_, e) => e.DrawDefault = true; _destinations.DrawSubItem += (_, e) => e.DrawDefault = true;
        void InspectRow()
        {
            if (!_updatingDestinations && _destinations.SelectedItems.Count > 0 && _destinations.SelectedItems[0].Tag is string address) { _inspector.SelectAddress(address); SetInspector(true); }
        }
        _destinations.SelectedIndexChanged += (_, _) => InspectRow(); _destinations.ItemActivate += (_, _) => InspectRow();
        _destinations.Resize += (_, _) => _destinations.Columns[0].Width = Math.Max((int)(130 * UiScale), _destinations.ClientSize.Width - _destinations.Columns[1].Width - _destinations.Columns[2].Width - 8);
        connectionLayout.Controls.Add(destinationPanel, 0, 1); connections.Controls.Add(connectionLayout);
        connections.Resize += (_, _) => { connectionLayout.Height = Math.Max(connections.ClientSize.Height, (int)(620 * UiScale)); };

        var diagnostics = Page("Diagnostics"); var diagnosticLayout = Stack();
        var health = Grid(50, 50); health.Height = 110; health.Dock = DockStyle.Top; health.Controls.Add(_dns, 0, 0); health.Controls.Add(_udp, 1, 0);
        Add(diagnosticLayout, health); _routes.Height = 36; _routes.Dock = DockStyle.Top; Add(diagnosticLayout, _routes);
        _checked.Height = 46; _checked.Dock = DockStyle.Top; Add(diagnosticLayout, _checked);
        var latency = ChartPanel("LATENCY", "Measured HTTPS request duration", _latencyChart); latency.Dock = DockStyle.Top; latency.Height = 190; Add(diagnosticLayout, latency);
        var technical = Stack(); technical.Visible = false;
        var technicalButton = ActionButton("Show engine and adapter details  +", () => { technical.Visible = !technical.Visible; return Task.CompletedTask; });
        technical.VisibleChanged += (_, _) => technicalButton.Text = technical.Visible ? "Hide engine and adapter details  −" : "Show engine and adapter details  +";
        Add(diagnosticLayout, technicalButton);
        foreach (var metric in new[] { _network, _adapter, _cores }) { metric.Dock = DockStyle.Top; metric.AutoSize = true; Add(technical, metric); }
        _events.Columns.Add("Time", 110); _events.Columns.Add("Event", 95); _events.Columns.Add("Details", 460); _events.Height = 160; _events.Dock = DockStyle.Top;
        foreach (var entry in _history.Entries.TakeLast(150)) ShowEvent(entry); Add(technical, _events); Add(diagnosticLayout, technical);
        _diagnosticTools = EmbeddedTools("diagnostics"); _diagnosticTools.Height = 510; Add(diagnosticLayout, _diagnosticTools); diagnostics.Controls.Add(diagnosticLayout); _diagnosticTools.Show();
        diagnostics.Layout += (_, _) => health.Height = Math.Max(_dns.GetPreferredSize(Size.Empty).Height + _dns.Margin.Vertical, _udp.GetPreferredSize(Size.Empty).Height + _udp.Margin.Vertical);

        var settings = Page("Settings"); var settingLayout = Stack();
        var settingsActions = Flow(); settingsActions.Controls.Add(ActionButton("Import server", () => { ImportProfile(); return Task.CompletedTask; }));
#if OWNER_BUILD
        settingsActions.Controls.Add(ActionButton("Devices", () => { using var form = new DeviceForm(_controller); form.ShowDialog(this); return Task.CompletedTask; }));
        settingsActions.Controls.Add(ActionButton("VM usage", async () => MessageBox.Show(this, await UsageReader.ReadAsync(), "Shared server usage")));
#endif
        settingsActions.Controls.Add(ActionButton("Compact view", () => { if (_compact == null || _compact.IsDisposed) { _compact = new CompactForm(_controller, () => _trafficText, () => { Show(); Activate(); }); _compact.VisibleChanged += (_, _) => RefreshSamplingSchedule(); _compact.Resize += (_, _) => RefreshSamplingSchedule(); } _compact.Show(); _compact.Activate(); Hide(); return Task.CompletedTask; }));
        Add(settingLayout, settingsActions);
        _privacyStatus.AutoSize = true; _privacyStatus.Padding = new(8, 12, 8, 12); Add(settingLayout, _privacyStatus);
        var privacyActions = Flow();
        privacyActions.Controls.Add(ActionButton("Use privacy defaults", () =>
        {
            if (_controller.ConnectionRequested) { _status.Text = "Disconnect before applying privacy defaults."; return Task.CompletedTask; }
            _controller.KillSwitch = true; _controller.AutoHeal = true; Preferences.Save(true);
            _status.Text = "Privacy defaults selected. The next connection enables whole-PC fallback blocking. Disconnect restores normal internet.";
            return Task.CompletedTask;
        }));
        Add(settingLayout, privacyActions);
        var performance = Flow();
        performance.Controls.Add(Label("Visual load", 10, Muted));
        var load = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, AccessibleName = "Visual load", BackColor = Surface, ForeColor = DashboardTheme.Text };
        load.Items.AddRange(["Balanced", "Low power"]); load.SelectedIndex = AnimationSettings.Load(this) == VisualLoad.LowPower ? 1 : 0;
        load.DrawMode = DrawMode.OwnerDrawFixed;
        load.DrawItem += (_, e) =>
        {
            using var fill = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? BlueTint : Surface); e.Graphics.FillRectangle(fill, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Index < 0 ? load.Text : load.Items[e.Index]!.ToString(), load.Font, Rectangle.Inflate(e.Bounds, -4, 0), DashboardTheme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            e.DrawFocusRectangle();
        };
        load.SelectedIndexChanged += (_, _) =>
        {
            try { AnimationSettings.SetLoad(this, load.SelectedIndex == 1 ? VisualLoad.LowPower : VisualLoad.Balanced); RefreshSamplingSchedule(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _status.Text = "Visual preference could not be saved."; }
        };
        performance.Controls.Add(load); Add(settingLayout, performance);
        var loadHint = Label("Balanced samples visible traffic once per second. Low power samples every two seconds and stops decorative animation. On battery, Conduit uses Low power automatically. Background sampling slows to five seconds; hidden or minimized dashboard sampling pauses. The tunnel host continues connection protection.", 10, Muted);
        loadHint.AutoSize = true; loadHint.Padding = new(8, 6, 8, 12); Add(settingLayout, loadHint);
        var options = Flow(); _auto.Checked = _controller.AutoHeal;
        _auto.CheckedChanged += (_, _) => { if (_controller.AutoHeal != _auto.Checked) { _controller.AutoHeal = _auto.Checked; Preferences.Save(_auto.Checked); } };
        var motion = new CheckBox { Text = "Animations", AutoSize = true, Checked = true, ForeColor = Muted };
        motion.CheckedChanged += (_, _) => AnimationSettings.SetEnabled(this, motion.Checked);
        options.Controls.Add(_auto); options.Controls.Add(motion); Add(settingLayout, options);
        var privacy = Label("Live destinations come from your local tunnel engine and are not sent to a location service or saved as browsing history. Health checks contact exit-IP and STUN services; tunnel DNS uses the configured Cloudflare DoH resolver. Diagnostic reports stay local until you choose to share them.", 10, Muted); privacy.AutoSize = true; privacy.Padding = new(8, 12, 8, 16); Add(settingLayout, privacy);
        _settingsTools = EmbeddedTools("settings"); _settingsTools.Height = 600; Add(settingLayout, _settingsTools); settings.Controls.Add(settingLayout); _settingsTools.Show();

        _inspector.CloseRequested += () => { _inspector.SelectAddress(null); SetInspector(false); _destinations.Focus(); };
        _inspectorMotion.Tick += (_, _) => ResizeInspector(false);
        _workspaceColumns.Resize += (_, _) => ResizeInspector(true);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && _inspectorOpen) { _inspector.SelectAddress(null); SetInspector(false); _destinations.Focus(); e.Handled = true; } };
        Navigate("Overview"); FitHome();
    }

    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new(0, 5, 0, 8), Padding = new(0, 4, 0, 4) };
    private static TableLayoutPanel Stack() { var stack = Grid(100); stack.Dock = DockStyle.Top; stack.AutoSize = true; stack.AutoSizeMode = AutoSizeMode.GrowAndShrink; stack.RowCount = 0; return stack; }
    private static void Add(TableLayoutPanel stack, Control control) { stack.RowStyles.Add(new(SizeType.AutoSize)); stack.Controls.Add(control, 0, stack.RowCount++); }
    private ToolsForm EmbeddedTools(string group) => new(_controller, _history, _quality, result => { _quality = result; AddEvent("UDP probe", $"Median {result.MedianMs:0.#} ms; failed {result.Sent - result.Replied}/{result.Sent}"); }, async plan =>
    {
        await UpdateInstaller.LaunchAsync(plan); await _controller.DisconnectAsync(); if (_controller is BrokerClient broker) await broker.ShutdownAsync(); ExitRequested?.Invoke();
    }, group, onlineUpdates: _onlineUpdates) { Dock = DockStyle.Top, Margin = Padding.Empty };
    internal void Navigate(string name)
    {
        if (!_pages.TryGetValue(name, out var selected)) return;
        foreach (var transition in _pageHost.Controls.OfType<PageTransition>().ToArray()) transition.Dispose();
        var before = PageTransition.Snapshot(_pageHost);
        foreach (var pair in _pages) pair.Value.Visible = pair.Key == name;
        _navigation.SelectPage(name);
        SetInspector(name == "Connections" && _inspector.SelectedAddress != null);
        if (name == "Diagnostics") _diagnosticTools.RefreshPage(); if (name == "Settings") _settingsTools.RefreshPage();
        selected.BringToFront(); _pageHost.PerformLayout(); PageTransition.Show(_pageHost, before);
    }
    private void SetInspector(bool open) { _inspectorOpen = open; if (open) _inspector.Visible = true; if (AnimationSettings.Allowed(this)) _inspectorMotion.Start(); else ResizeInspector(true); }
    private void ResizeInspector(bool immediate)
    {
        if (_workspaceColumns == null) return;
        float target = _inspectorOpen ? InspectorWidth : 0, current = _workspaceColumns.ColumnStyles[1].Width;
        float next = immediate || !AnimationSettings.Allowed(this) ? target : current + (target - current) * .24f;
        if (Math.Abs(next - target) < 1) { next = target; _inspectorMotion.Stop(); if (!_inspectorOpen) _inspector.Visible = false; }
        _workspaceColumns.ColumnStyles[1].Width = next;
    }
    private void UpdateWorkspaceStatus(TunnelState state, bool verified)
    {
        _status.ForeColor = state == TunnelState.Faulted ? Red : state == TunnelState.Connected && !verified ? Amber : Muted;
        if (state != TunnelState.Connected) { _inspector.SelectAddress(null); SetInspector(false); }
    }
}
