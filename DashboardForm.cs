using System.Diagnostics;
using static WorkTunnel.DashboardTheme;

namespace WorkTunnel;

internal sealed partial class DashboardForm : ConduitWindow
{
    private readonly ITunnelController _controller;
    private Button _connect = null!;
    private Button _check = null!;
    private readonly AnimatedStatusLabel _headline = new() { Text = "Disconnected", ForeColor = Muted };
    private readonly Label _status = Label("Connect when you're ready. Stop any other tunnel first.", 10, Muted);
    private readonly Label _profile = Label("PERSONAL NETWORK / OVERVIEW", 9, Muted);
    private readonly Label _session = Label("No active session", 9, Muted);
    private readonly Label _checked = Label("Connect to run all four health checks.", 9, Muted);
    private readonly Label _lockHint = Label("Network lock off", 11, Muted, true);
    private readonly Label _routes = Label("Ordinary app routes · not checked", 10, Muted);
    private readonly Label _privacyStatus = Label("Privacy & protection · waiting for current status", 10, Muted);
    private readonly ListView _destinations = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.None, BackColor = Surface, ForeColor = DashboardTheme.Text };
    private readonly CancellationTokenSource _lifetime = new();
    private DateTimeOffset _nextConnections;
    private readonly ToolTip _details = new();
    private readonly DashboardMetric _exit = new("EXIT IP");
    private readonly DashboardMetric _latency = new("INTERNET CHECK");
    private readonly DashboardMetric _speed = new("TUNNEL TRAFFIC   ↓ RX / ↑ TX", 12);
    private readonly DashboardMetric _network = new("LOCAL NETWORK ADAPTER");
    private readonly DashboardMetric _adapter = new("TUNNEL ADAPTER");
    private readonly DashboardMetric _cores = new("RUNNING CORES");
    private readonly DashboardMetric _dns = new("DNS CHECK");
    private readonly DashboardMetric _udp = new("UDP / VOICE PATH");
    private readonly DashboardChart _latencyChart = new(false, 120);
    private readonly DashboardChart _trafficChart = new(true, 300);
    private readonly ListView _events = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.None, MultiSelect = false,
        BackColor = Surface, ForeColor = DashboardTheme.Text, HideSelection = false };
    private readonly TextBox _usage = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true,
        ScrollBars = ScrollBars.Vertical, BackColor = Surface, ForeColor = DashboardTheme.Text, BorderStyle = BorderStyle.None,
        Font = new("Consolas", 9) };
    private readonly CheckBox _auto = new() { Text = "Automatic recovery", AutoSize = true, ForeColor = DashboardTheme.Text, Cursor = Cursors.Hand };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly TrafficMeter _meter = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _connectedTime = new();
    private TunnelState _previous = TunnelState.Disconnected;
    private DateTimeOffset? _connectedAt;
    private DateTimeOffset? _lastHealth;
    private string _lastEvent = "";
    private string _lastHealthSummary = "";
    private bool _allowClose, _sampling;
    private bool _monitoring, _profileValid;
    private readonly bool _onlineUpdates;
    private string _monitorServer = "";
    private int _monitorPort = 443;
    private readonly HistoryStore _history;
    private QualityResult? _quality;
    private CompactForm? _compact;
    private string _trafficText = "No traffic sample";
    public event Action? ExitRequested;

    public DashboardForm(ITunnelController controller, bool persistHistory = true)
    {
        _controller = controller; _onlineUpdates = persistHistory;
        _history = new HistoryStore(persistHistory ? Path.Combine(AppPaths.DataDir, "history.jsonl") : null);
        StyleCaption(this);
        Text = "Conduit · Connection dashboard";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(1320, 900); MinimumSize = new(980, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background; ForeColor = DashboardTheme.Text; Font = new("Segoe UI", 10);
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.Enter) { _connect?.PerformClick(); e.Handled = true; } };
        BuildWorkspace();

        _controller.StatusChanged += Changed; _controller.HealthChanged += HealthChanged;
        _timer.Tick += async (_, _) => await SampleAsync();
        Shown += async (_, _) =>
        {
            var workArea = Screen.FromControl(this).WorkingArea;
            MinimumSize = new(Math.Min(MinimumSize.Width, workArea.Width - 24), Math.Min(MinimumSize.Height, workArea.Height - 24));
            Size = new(Math.Min(Width, workArea.Width - 24), Math.Min(Height, workArea.Height - 24));
            RefreshProfile(); ApplyHealth(_controller.Health); ApplyState(); AddEvent("Dashboard", "Monitoring started. No connection settings changed."); _monitoring = true; RefreshSamplingSchedule(); await SampleAsync();
            if (persistHistory && !Profile.Load().IsValid) ImportProfile();
        };
        VisibleChanged += (_, _) => { _meter.ResetBaseline(); if (Visible) RefreshProfile(); RefreshSamplingSchedule(); };
        Activated += (_, _) => RefreshSamplingSchedule(); Deactivate += (_, _) => RefreshSamplingSchedule(); Resize += (_, _) => RefreshSamplingSchedule();
        FormClosing += (_, e) => { if (!_allowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        if (persistHistory) { UiPreferences.Apply(this); WindowPreferences.Attach(this, "dashboard"); }
    }

    private static DashboardSurface ChartPanel(string title, string subtitle, DashboardChart chart)
    {
        var panel = new DashboardSurface(); var grid = Grid(100); grid.RowCount = 2;
        grid.RowStyles.Add(new(SizeType.Absolute, 24)); grid.RowStyles.Add(new(SizeType.Percent, 100));
        grid.Controls.Add(Label(title + "   /   " + subtitle, 8, Muted, true), 0, 0); grid.Controls.Add(chart, 0, 1); panel.Controls.Add(grid); return panel;
    }
    private Button ActionButton(string text, Func<Task> action, bool disableWhileBusy = true)
    {
        var button = new DashboardButton { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Height = 34, FlatStyle = FlatStyle.Flat,
            BackColor = Surface, ForeColor = DashboardTheme.Text, Padding = new(10, 3, 10, 3),
            Margin = new(6, 0, 0, 0), Cursor = Cursors.Hand, Font = new("Segoe UI", 9), AccessibleName = text };
        button.FlatAppearance.BorderColor = Border; button.FlatAppearance.MouseOverBackColor = BlueTint;
        button.Click += async (_, _) =>
        {
            if (disableWhileBusy) button.Enabled = false;
            try { await action(); }
            catch (Exception error) { if (!IsDisposed) { _status.Text = error.Message; AddEvent("Error", error.Message, Red); } }
            finally { if (!IsDisposed) { if (disableWhileBusy) button.Enabled = true; ApplyState(); } }
        };
        return button;
    }
    private void RefreshProfile()
    {
        var profile = Profile.Load();
        _profileValid = profile.IsValid; _monitorServer = profile.Imported?.ResolvedAddress is { Length: > 0 } resolved ? resolved : profile.Server; _monitorPort = profile.Port;
        _profile.Text = profile.IsValid ? $"{profile.User}  /  {profile.Server}:{profile.Port}  /  {profile.Sni}" : "NEW DEVICE  /  IMPORT LINK OR v2rayN SERVER";
    }
    private void RefreshSamplingSchedule()
    {
        if (!_monitoring || IsDisposed) return;
        int interval = VisualPerformance.SampleInterval(Visible, WindowState == FormWindowState.Minimized,
            Form.ActiveForm == this, _compact?.Visible == true && _compact.WindowState != FormWindowState.Minimized, AnimationSettings.LowPower(this));
        if (interval == 0) { _timer.Stop(); _meter.ResetBaseline(); return; }
        _timer.Interval = interval; _timer.Start();
    }
    private void Changed(object? sender, StatusEventArgs args) => Post(() =>
    {
        _status.Text = args.Message;
        AddEvent(args.State.ToString(), args.Message, args.State == TunnelState.Faulted ? Red : null);
        ApplyState(); RefreshProfile();
    });
    private void HealthChanged(TunnelHealth? health) => Post(() => ApplyHealth(_controller.Health));
    private void ApplyState()
    {
        var observation = (_controller as BrokerClient)?.LockObservation;
        bool locked = observation?.BlockingVerified == true;
        _lockHint.Text = locked ? "NETWORK LOCK ACTIVE" : observation is { Known: true, PolicyPresent: true } ? "NETWORK LOCK NEEDS ATTENTION"
            : observation is { Known: false } && _controller.KillSwitch ? "NETWORK LOCK UNCONFIRMED" : _controller.KillSwitch ? "NETWORK LOCK SELECTED" : "NETWORK LOCK OFF";
        _lockHint.ForeColor = locked ? Healthy : _controller.KillSwitch ? Blue : Amber;
        _details.SetToolTip(_lockHint, observation?.Detail ?? "Enable the remembered network lock in Settings to block fallback internet. Its installed rules must be confirmed by the tunnel host.");
        var state = _controller.State;
        bool verified = _controller.Verified && HealthFreshness.IsCurrent(_controller.Health, DateTimeOffset.UtcNow);
        _privacyStatus.Text = $"PRIVACY / PROTECTION\nConnection: {(verified ? "recent checks passed" : "unverified")}  ·  Fallback blocking: {(locked ? "IPv4 / IPv6 rules confirmed" : _controller.KillSwitch ? "awaiting confirmation" : "off")}\n" +
            "Destination locations: no external lookups  ·  Connection list: local, current sample only";
        UpdateWorkspaceStatus(state, verified);
        _headline.SetStatus(state switch { TunnelState.Connected => verified ? "Connection verified" : "Connected · needs verification", TunnelState.Connecting => "Connecting…", TunnelState.Faulted => "Connection needs attention", _ => "Disconnected" },
            verified ? Healthy : state == TunnelState.Faulted ? Red : state == TunnelState.Disconnected ? Muted : Amber);
        _connect.Text = _controller.ConnectionRequested ? (state == TunnelState.Connecting ? "Cancel connection" : "Disconnect") : _profileValid ? "Connect" : "Import link";
        _connect.BackColor = _controller.ConnectionRequested ? AccentBlue : AccentPink;
        _connect.ForeColor = Background;
        _check.Enabled = state == TunnelState.Connected;
        _auto.Checked = _controller.AutoHeal;
        if (state != _previous)
        {
            if (state == TunnelState.Connected) { _connectedTime.Restart(); _connectedAt = DateTimeOffset.Now; }
            else { _connectedTime.Reset(); _connectedAt = null; }
            _previous = state;
            if (state == TunnelState.Disconnected) _status.Text = "This app is disconnected. Other VPNs may still be active.";
        }
        if (state != TunnelState.Connected) { _exit.UpdateValue("—", "No verified tunnel exit", Muted); _latency.UpdateValue("—", "Connect to measure request time", Muted); }
    }
    private void ApplyHealth(TunnelHealth? health)
    {
        bool current = HealthFreshness.IsCurrent(health, DateTimeOffset.UtcNow) && _controller.State == TunnelState.Connected;
        _exit.UpdateValue(current ? health!.ExitIp ?? "Unavailable" : "—", current ? health!.Internet.Detail : "No verified tunnel exit", current && health!.Internet.Ok ? Healthy : Muted);
        _latency.UpdateValue(current && health!.Internet.Milliseconds.HasValue ? $"{health.Internet.Milliseconds} ms" : "—", "HTTPS request time · not ICMP ping");
        _dns.UpdateValue(!current ? "Not checked" : health!.Dns.Ok ? "Responding" : "Needs attention", current ? health!.Dns.Detail : "Connect to test DNS through the tunnel", !current ? Muted : health!.Dns.Ok ? Healthy : Amber);
        _udp.UpdateValue(!current ? "Not checked" : health!.Udp.Ok ? "Reachable" : "Unverified", current ? health!.Udp.Detail : "Connect to check UDP reachability", !current ? Muted : health!.Udp.Ok ? Healthy : Amber);
        _routes.Text = !current ? "ORDINARY APP ROUTES · Not checked" : health!.Routes?.Ok == true ? "ORDINARY APP ROUTES · IPv4 / IPv6 checked" : "ORDINARY APP ROUTES · Needs attention";
        _routes.ForeColor = current && health!.Routes?.Ok == true ? Healthy : Amber;
        _details.SetToolTip(_routes, health?.Routes == null ? "Route checks have not completed." : $"{health.Routes.Ipv4.Detail}\n{health.Routes.Ipv6.Detail}\n{health.Routes.OrdinaryInternet.Detail}");
        _details.SetToolTip(_status, ConnectionDiagnosis.For(health).ToString());
        if (health != null && health.CheckedAt != _lastHealth)
        {
            _latencyChart.Add(health.Internet.Ok ? health.Internet.Milliseconds : null);
            _lastHealth = health.CheckedAt;
            string summary = $"Adapter {(health.Adapter.Ok ? "OK" : "failed")} · Internet {(health.Internet.Ok ? "OK" : "failed")} · DNS {(health.Dns.Ok ? "OK" : "failed")} · UDP {(health.Udp.Ok ? "OK" : "unverified")} · Routes {(health.Routes?.Ok == true ? "OK" : "unverified")}";
            if (summary != _lastHealthSummary) { AddEvent("Health", summary, health.Healthy ? Healthy : Amber); _lastHealthSummary = summary; }
        }
        else if (health == null && _lastHealth.HasValue) { _latencyChart.Add(null); _lastHealthSummary = ""; _lastHealth = null; }
        UpdateCheckAge(); ApplyState();
    }
    private void UpdateCheckAge()
    {
        var health = _controller.Health;
        _checked.Text = health == null ? "Connect to verify tunnel health and ordinary IPv4 / IPv6 routes." :
            !HealthFreshness.IsCurrent(health, DateTimeOffset.UtcNow) ? "Previous checks have expired. Current protection is unverified; choose Check now." :
            $"Checked {Math.Max(0, (int)(DateTimeOffset.Now - health.CheckedAt).TotalSeconds)}s ago  ·  {(health.Healthy ? "7 of 7 checks passed" : "Some checks need attention")}  ·  UDP reachability does not guarantee call quality.";
    }
    private async Task SampleAsync()
    {
        if (_sampling || IsDisposed || ((!Visible || WindowState == FormWindowState.Minimized) && _compact?.Visible != true)) return;
        RefreshSamplingSchedule();
        _sampling = true;
        try
        {
            var snapshot = await Task.Run(() => DashboardTelemetry.Read(_monitorServer, _monitorPort));
            if (IsDisposed || ((!Visible || WindowState == FormWindowState.Minimized) && _compact?.Visible != true)) return;
            _network.UpdateValue(snapshot.Network, snapshot.Address);
            bool active = _controller.State == TunnelState.Connected;
            _adapter.UpdateValue(snapshot.Id == null ? "Not available" : snapshot.Tunnel,
                snapshot.Id == null ? "Connect to create the full-device adapter" : !active ? "Present · not managed by this session" : ConfiguredTun());
            var ids = _controller.CoreProcessIds;
            _cores.UpdateValue($"Xray {ids.Xray?.ToString() ?? "—"} / sing-box {ids.SingBox?.ToString() ?? "—"}", "Process IDs · this app's engines");
            var traffic = _meter.Sample(_controller.State == TunnelState.Connected ? snapshot : snapshot with { Id = null }, _clock.Elapsed.TotalSeconds);
            _trafficChart.Add(traffic?.down, traffic?.up);
            _trafficText = traffic.HasValue ? $"↓ {TrafficMeter.Rate(traffic.Value.down)}  ↑ {TrafficMeter.Rate(traffic.Value.up)}" : "No traffic sample";
            _speed.UpdateValue(traffic.HasValue ? $"↓ {TrafficMeter.Rate(traffic.Value.down)}  ↑ {TrafficMeter.Rate(traffic.Value.up)}" : "—",
                $"While visible: ↓ {TrafficMeter.Bytes(_meter.Received)}  ↑ {TrafficMeter.Bytes(_meter.Sent)}");
            _session.Text = _connectedAt.HasValue ? $"{_connectedTime.Elapsed.ToString(@"hh\:mm\:ss")} · since {_connectedAt:HH:mm:ss}" : "No active session";
            ApplyHealth(_controller.Health);
            if (Visible && Form.ActiveForm == this && _navigation.SelectedPage == "Connections" && DateTimeOffset.UtcNow >= _nextConnections)
            {
                _nextConnections = DateTimeOffset.UtcNow.AddSeconds(AnimationSettings.LowPower(this) ? 5 : 2);
                var live = await _controller.ReadConnectionsAsync();
                if (IsDisposed) return;
                if (_controller.State != TunnelState.Connected) live = ConnectionSnapshot.Empty();
                _connectionNote.Text = live.Problem ?? $"{live.Destinations.Length} destinations in the current sample · aggregated by IP and protocol · no location lookups";
                _updatingDestinations = true;
                _destinations.BeginUpdate(); _destinations.Items.Clear();
                foreach (var destination in live.Destinations)
                {
                    string name = destination.Name.Length > 0 ? destination.Name : destination.Address;
                    var item = new ListViewItem(name) { ToolTipText = destination.Address, Tag = destination.Address };
                    item.SubItems.Add(destination.Network.ToUpperInvariant() + (destination.Active ? " · Active" : " · Open"));
                    item.SubItems.Add(TrafficMeter.Bytes(destination.Uploaded + destination.Downloaded)); _destinations.Items.Add(item);
                    if (destination.Address == _inspector.SelectedAddress) item.Selected = true;
                }
                _destinations.ShowItemToolTips = true; _destinations.EndUpdate(); _updatingDestinations = false;
                _inspector.UpdateData(live);
            }
        }
        catch (Exception error) { if (!IsDisposed) { _meter.ResetBaseline(); _trafficChart.Add(null); _speed.UpdateValue("Unavailable", "Adapter counters could not be read", Amber); AddEvent("Monitor", error.Message, Amber); } }
        finally { _sampling = false; _updatingDestinations = false; }
    }
    private static string ConfiguredTun()
    {
        try
        {
            using var config = System.Text.Json.JsonDocument.Parse(File.ReadAllText(AppPaths.SingBoxConfig));
            var tun = config.RootElement.GetProperty("inbounds").EnumerateArray().First(i => i.GetProperty("type").GetString() == "tun");
            return $"Configured MTU {tun.GetProperty("mtu").GetInt32()} · {tun.GetProperty("stack").GetString()}";
        }
        catch { return "Adapter is up · configuration unavailable"; }
    }
    private void AddEvent(string kind, string message, Color? color = null)
    {
        string key = kind + message; if (key == _lastEvent) return; _lastEvent = key;
        _history.Add(kind, message);
        ShowEvent(_history.Entries.Last(), color);
    }
    private void ShowEvent(HistoryEntry entry, Color? color = null)
    {
        var item = new ListViewItem(entry.At.LocalDateTime.ToString("MM-dd HH:mm:ss")); item.SubItems.Add(entry.Kind); item.SubItems.Add(entry.Message);
        item.ForeColor = color ?? Muted; item.ToolTipText = entry.Message;
        _events.ShowItemToolTips = true; _events.Items.Insert(0, item);
        if (_events.Items.Count > 150) _events.Items.RemoveAt(_events.Items.Count - 1);
    }
    private void Post(Action action) { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); } catch (InvalidOperationException) { } }

    private void ImportProfile()
    {
#if OWNER_BUILD
        if (File.Exists(Path.Combine(AppPaths.DataDir, "pending-device-operation.json")))
        { _status.Text = "Refresh the pending change in Your devices before switching profiles."; return; }
#endif
        if (_controller.ConnectionRequested) { _status.Text = "Disconnect before importing a different device link."; return; }
        using var wizard = new FirstRunForm();
        if (wizard.ShowDialog(this) == DialogResult.OK) { RefreshProfile(); _status.Text = "Device profile encrypted and saved. Ready to connect."; ApplyState(); }
    }
    public void Shutdown() { _allowClose = true; Close(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _lifetime.Cancel(); _details.Dispose(); _compact?.Dispose(); _timer.Stop(); _timer.Dispose(); _controller.StatusChanged -= Changed; _controller.HealthChanged -= HealthChanged; _inspectorMotion.Dispose(); }
        base.Dispose(disposing);
    }
}
