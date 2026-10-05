using System.Diagnostics;
using static WorkTunnel.DashboardTheme;

namespace WorkTunnel;

internal sealed class ToolsForm : Form
{
    private readonly ITunnelController _controller;
    private readonly HistoryStore _history;
    private readonly Action<QualityResult> _qualityChanged;
    private readonly Func<UpdatePlan, Task> _install;
    private QualityResult? _quality;
    private CancellationTokenSource? _probe;
    private bool _installing;
    private readonly ToolPages _tabs;
    private CheckBox _networkLock = null!;
    private Action? _refreshConnection;
    private UpdateFeedPanel _feed = null!;
    public ToolsForm(ITunnelController controller, HistoryStore history, QualityResult? quality, Action<QualityResult> qualityChanged, Func<UpdatePlan, Task> install, string? embeddedGroup = null, bool onlineUpdates = true)
    {
        _controller = controller; _history = history; _quality = quality; _qualityChanged = qualityChanged; _install = install;
        Text = "Connection tools · Conduit"; Size = new(860, 640); MinimumSize = new(750, 530); StartPosition = FormStartPosition.CenterParent;
        BackColor = Background; ForeColor = DashboardTheme.Text; Font = new("Segoe UI", 10);
        if (embeddedGroup == null) StyleCaption(this); else { TopLevel = false; FormBorderStyle = FormBorderStyle.None; MinimumSize = Size.Empty; }
        var tabs = _tabs = new ToolPages { Dock = DockStyle.Fill }; Controls.Add(tabs);
        Panel Tab(string title) => tabs.AddPage(title);
        var diagnosis = Tab("Connection"); var report = TextArea(); diagnosis.Controls.Add(report);
        void Explain() => report.Text = ProblemExplanation.Explain(_controller.State, _controller.Health) + "\r\n\r\n" +
            "What the evidence means\r\n\r\nHTTPS timing includes DNS, connection setup and the response. It is not Discord ping.\r\n" +
            "UDP probe variation measures the Cloudflare STUN test path. Failed probes can reflect timeouts, filtering or a test-service problem. They are not proof of Discord packet loss.\r\n\r\n" +
            "To improve delay\r\n\r\nCompare Wi-Fi with Ethernet using the same probe test. Avoid saturating uploads while calling. A closer VM may help, but requires comparing routes; extra CPU does not reduce network distance. Keep the tested MTU and routing unless measurements justify a change.\r\n\r\n" +
            "The optional network lock blocks ordinary outbound internet during recovery. It affects this entire PC and persists after a host crash. Disconnect or Restore normal internet removes it. Without the lock, recovery can briefly restore ordinary routing.\r\n\r\n" +
            "After a network change, recovery keeps the engines running and allows 15 seconds of link stability before checking them. If needed, it restarts the adapter and allows 20 seconds for routes to settle.\r\n\r\n" +
            "Incident reports are recorded locally, including the last verified state, recent events, network facts and scrubbed engine warnings. Up to 20 are kept; Save diagnostics includes the latest five.";
        _refreshConnection = Explain; Explain(); var diagButtons = Buttons(); diagnosis.Controls.Add(diagButtons);
        diagButtons.Controls.Add(Button("Check connection", async () => { await _controller.CheckNowAsync(); Explain(); }));
        diagButtons.Controls.Add(Button("Save diagnostics", ExportAsync));
        diagButtons.Controls.Add(Button("Incident reports", () =>
        {
            if (Directory.Exists(AppPaths.IncidentsDir)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppPaths.IncidentsDir + "\"") { UseShellExecute = true });
            else MessageBox.Show(this, "No incident reports have been recorded yet. Recovery and connection failures create reports automatically. Save diagnostics includes the latest five reports.", "Incident reports");
            return Task.CompletedTask;
        }));
        diagButtons.Controls.Add(Button("Restore normal internet", async () => await _controller.DisconnectAsync()));
        diagButtons.Controls.Add(Button("Open logs", () => { Process.Start(new ProcessStartInfo(AppPaths.LogDir) { UseShellExecute = true }); return Task.CompletedTask; }));

        var historyTab = Tab("History"); var historyView = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BackColor = Surface, ForeColor = DashboardTheme.Text };
        historyView.Columns.Add("When", 155); historyView.Columns.Add("Event", 100); historyView.Columns.Add("Details", 490);
        void RefreshHistory()
        {
            historyView.Items.Clear();
            foreach (var entry in _history.Entries.Reverse()) { var item = new ListViewItem(entry.At.LocalDateTime.ToString("MM-dd HH:mm:ss")); item.SubItems.Add(entry.Kind); item.SubItems.Add(entry.Message); historyView.Items.Add(item); }
        }
        historyTab.Controls.Add(historyView); var historyButtons = Buttons(); historyTab.Controls.Add(historyButtons);
        historyButtons.Controls.Add(Button("Refresh history", () => { RefreshHistory(); return Task.CompletedTask; }));
        historyButtons.Controls.Add(Button("Save diagnostics", ExportAsync));
        historyButtons.Controls.Add(new Label { AutoSize = true, ForeColor = Muted, Text = "Recent events survive restarts · up to 2,000 shown", Padding = new(10) }); RefreshHistory();

        var settings = Tab("Settings"); var values = TextArea(); settings.Controls.Add(values);
        async Task ReadSettings()
        {
            values.Text = string.Join("\r\n\r\n", SettingsSnapshot.Read(runtime: _controller.State == TunnelState.Connected).Select(k => k.Key + ": " + k.Value));
            var versions = await Task.WhenAll(SettingsSnapshot.EngineVersionAsync(AppPaths.XrayExe), SettingsSnapshot.EngineVersionAsync(AppPaths.SingBoxExe));
            if (!IsDisposed) values.AppendText("\r\n\r\n" + string.Join("\r\n", versions) + "\r\n\r\nThese settings are read-only to preserve the tested routing. Import link changes the device profile.");
        }
        var settingsButtons = Buttons(); settings.Controls.Add(settingsButtons); settingsButtons.Controls.Add(Button("Refresh settings", ReadSettings));
        settingsButtons.Controls.Add(Button("Export this device's link", () =>
        {
            var profile = Profile.Load(); if (!profile.IsValid) throw new InvalidOperationException("Import a device connection first.");
            using var save = new SaveFileDialog { Filter = "Connection link (contains credentials)|*.txt", FileName = "Conduit-device-link.txt" };
            if (save.ShowDialog(this) == DialogResult.OK) File.WriteAllText(save.FileName, DeviceLink.Create(profile), AppPaths.Utf8NoBom);
            return Task.CompletedTask;
        }));
        var options = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(0, 0, 0, 12) };
        var networkLock = _networkLock = new CheckBox { AutoSize = true, Text = "Block internet if tunnel fails (whole PC)", Checked = _controller.KillSwitch, Enabled = !_controller.ConnectionRequested, ForeColor = DashboardTheme.Text };
        networkLock.CheckedChanged += (_, _) =>
        {
            try { _controller.KillSwitch = networkLock.Checked; }
            catch (Exception e) { MessageBox.Show(this, e.Message, "Network lock"); networkLock.Checked = _controller.KillSwitch; }
        };
        options.Controls.Add(networkLock);
        options.Controls.Add(new Label { AutoSize = true, Text = "Text size", Padding = new(12, 4, 0, 0) });
        var textSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80, AccessibleName = "Text size" };
        textSize.Items.AddRange(["100%", "125%", "150%"]); textSize.SelectedItem = UiPreferences.Percent + "%";
        textSize.SelectedIndexChanged += (_, _) => { UiPreferences.Percent = int.Parse(textSize.Text.TrimEnd('%')); foreach (Form form in Application.OpenForms) if (form.TopLevel) UiPreferences.Apply(form); };
        options.Controls.Add(textSize); settings.Controls.Add(options);
        values.Text = "Choose Refresh settings to read the active configuration and bundled engine versions.";
        tabs.SelectedIndexChanged += async (_, _) =>
        {
            if (tabs.SelectedTab == settings) { try { await ReadSettings(); } catch (Exception e) { if (!IsDisposed) values.Text = e.Message; } }
            if (tabs.SelectedTab == diagnosis) Explain();
            if (tabs.SelectedTab == historyTab) RefreshHistory();
        };

        var qualityTab = Tab("Latency / voice"); var measurement = TextArea(); qualityTab.Controls.Add(measurement);
        measurement.Text = quality == null ? "Measure 12 small UDP probes through the tunnel. No download or upload load test is run.\r\n\r\nA successful test takes about 8 seconds; timeouts can extend it to about 26 seconds. Disconnecting or closing this window cancels it." : FormatQuality(quality);
        var probeButtons = Buttons(); qualityTab.Controls.Add(probeButtons);
        probeButtons.Controls.Add(Button("Measure latency", async () =>
        {
            if (_controller.State != TunnelState.Connected) throw new InvalidOperationException("Connect the tunnel first.");
            if (_probe != null) return;
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(35)); _probe = cancel;
            try
            {
                var progress = new Progress<string>(text => { if (!IsDisposed) measurement.Text = text + "\r\n\r\nTesting Cloudflare STUN through your VM. Your active calls are not measured."; });
                string expectedExit = _controller.Health?.ExitIp ?? throw new InvalidOperationException("Verify the tunnel exit first.");
                var result = await QualityProbe.RunAsync(expectedExit, progress, cancel.Token);
                if (!IsDisposed) { _quality = result; measurement.Text = FormatQuality(result); _qualityChanged(result); }
            }
            catch (OperationCanceledException) { if (!IsDisposed) measurement.Text = "Measurement canceled. No partial result was labeled as complete."; }
            finally { _probe = null; }
        }));
        probeButtons.Controls.Add(Button("Cancel test", () => { CancelProbe(); return Task.CompletedTask; }));

        var updates = Tab("Updates"); var updateText = TextArea(); updates.Controls.Add(updateText);
        updateText.Text = $"Installed: {typeof(ToolsForm).Assembly.GetName().Version} ({UpdateInstaller.Flavor})\r\n\r\n" +
            "Use the publisher's signed feed above, or download a matching .wtupdate bundle and choose Install signed update. Both verify the embedded release key. No manually pasted hash is needed.\r\n\r\n" +
            "The elevated installer verifies the signature again and checks the bundled engines. Your encrypted profile is preserved. Installation briefly disconnects and restarts the app.\r\n\r\n" +
            "Roll back restores files from the last successful updater transaction; it does not undo server changes. Windows publisher signing is separate and is not enabled for these builds. Downloads and installation remain manual.";
        async Task ReviewBundle(string bundle, CancellationToken ct)
        {
            if (_installing) throw new InvalidOperationException("Another update is already in progress.");
            _installing = true;
            try
            {
                var plan = await Task.Run(() => UpdateInstaller.PrepareSigned(bundle, AppPaths.BaseDir, _controller.ConnectionRequested));
                ct.ThrowIfCancellationRequested();
                await UpdateInstaller.ValidateCoresAsync(plan);
                ct.ThrowIfCancellationRequested();
                if (IsDisposed) return;
                if (MessageBox.Show(this, $"Install version {plan.Version}? Conduit will briefly disconnect, restart, and reconnect if it was connected. Your device profile will be kept.", "Install verified package", MessageBoxButtons.OKCancel) == DialogResult.OK)
                    await _install(plan);
                else updateText.Text = "Update verified but not installed. Your running app is unchanged.";
            }
            finally { _installing = false; }
        }
        _feed = new UpdateFeedPanel(controller, ReviewBundle, onlineUpdates && embeddedGroup != "diagnostics"); updates.Controls.Add(_feed);
        var updateButtons = Buttons(); updates.Controls.Add(updateButtons);
        updateButtons.Controls.Add(Button("Open releases", () => { Process.Start(new ProcessStartInfo("https://github.com/Zykoraa/conduit/releases/latest") { UseShellExecute = true }); return Task.CompletedTask; }));
        updateButtons.Controls.Add(Button("Install signed update", async () =>
        {
            if (_installing || _feed.Busy) return;
            using var choose = new OpenFileDialog { Filter = "Signed Conduit bundle|*.wtupdate" }; if (choose.ShowDialog(this) != DialogResult.OK) return;
            await ReviewBundle(choose.FileName, CancellationToken.None);
        }));
        updateButtons.Controls.Add(Button("Roll back", async () =>
        {
            if (_installing || _feed.Busy) return; _installing = true;
            try
            {
                var plan = UpdateInstaller.PrepareRollback(AppPaths.BaseDir, _controller.ConnectionRequested);
                await UpdateInstaller.ValidateCoresAsync(plan);
                if (MessageBox.Show(this, "Restore the previous installation saved by this updater? The tunnel will briefly disconnect. Your profile will be kept.", "Roll back", MessageBoxButtons.OKCancel) == DialogResult.OK) await _install(plan);
            }
            finally { _installing = false; }
        }));
        _controller.StatusChanged += OnState;
        FormClosing += (_, e) => { if ((_installing || _feed.Busy) && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; } CancelProbe(); };
        if (embeddedGroup == null) UiPreferences.Apply(this);
        else tabs.Filter(embeddedGroup == "settings" ? ["Settings", "Updates"] : ["Connection", "History", "Latency / voice"]);
    }
    internal void RefreshPage() => _tabs.RefreshSelected();
    private void CancelProbe() { try { _probe?.Cancel(); } catch (ObjectDisposedException) { } }
    private void OnState(object? sender, StatusEventArgs state)
    {
        if (state.State != TunnelState.Connected) CancelProbe();
        if (IsHandleCreated && !IsDisposed) try { BeginInvoke((Action)(() => { if (IsDisposed) return; _networkLock.Enabled = !_controller.ConnectionRequested; _refreshConnection?.Invoke(); })); } catch (InvalidOperationException) { }
    }
    private async Task ExportAsync()
    {
        using var save = new SaveFileDialog { Filter = "Diagnostic ZIP|*.zip", FileName = "Conduit-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip" };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        var state = _controller.State; var health = _controller.Health; var entries = _history.Entries; var quality = _quality;
        await Task.Run(() => DiagnosticsExporter.Export(save.FileName, state, health, entries, quality));
        if (!IsDisposed) MessageBox.Show(this, "Saved locally. No data was uploaded. Review the ZIP before sharing it.", "Diagnostics saved");
    }
    private static string FormatQuality(QualityResult q) => $"Measured {q.At.LocalDateTime:g}\r\n\r\nMedian UDP RTT: {q.MedianMs?.ToString("0.0") ?? "unavailable"} ms\r\n95th percentile: {q.P95Ms?.ToString("0.0") ?? "unavailable"} ms\r\nRTT variation: {q.VariationMs?.ToString("0.0") ?? "unavailable"} ms\r\nFailed probes: {q.Sent - q.Replied}/{q.Sent} ({q.FailurePercent:0.#}%)\r\n\r\nVariation is the mean absolute difference between consecutive successful RTT samples. Failures break that sequence.\r\n\r\n{q.Note}\r\n\r\nCompare this test on Wi-Fi and Ethernet under similar load. A failed probe is not proof of packet loss on a Discord call.";
    private TextBox TextArea() => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, TabStop = false, ScrollBars = ScrollBars.Vertical, BackColor = Surface, ForeColor = DashboardTheme.Text, BorderStyle = BorderStyle.None, Font = Font };
    private static FlowLayoutPanel Buttons() => new() { Dock = DockStyle.Bottom, AutoSize = true, MinimumSize = new(0, 55), Padding = new(0, 10, 0, 0) };
    private Button Button(string text, Func<Task> action)
    {
        var button = new DashboardButton { Text = text, AutoSize = true, Height = 35, BackColor = BlueTint, ForeColor = DashboardTheme.Text, Padding = new(8, 4, 8, 4), Cursor = Cursors.Hand };
        button.Click += async (_, _) => { button.Enabled = false; try { await action(); } catch (Exception e) { if (!IsDisposed) MessageBox.Show(this, e.Message, "Conduit"); } finally { if (!IsDisposed) button.Enabled = true; } }; return button;
    }
    protected override void Dispose(bool disposing) { if (disposing) { CancelProbe(); _controller.StatusChanged -= OnState; } base.Dispose(disposing); }
}

internal sealed class ToolPages : UserControl
{
    private readonly Panel _body = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _nav = new() { Dock = DockStyle.Top, Height = 52, Padding = new(16, 8, 0, 0), WrapContents = false };
    private readonly List<(Panel Page, Button Button)> _pages = new();
    public Panel? SelectedTab { get; private set; }
    public event EventHandler? SelectedIndexChanged;
    public ToolPages() { BackColor = Background; Controls.Add(_body); Controls.Add(_nav); }
    internal void Filter(string[] names)
    {
        foreach (var entry in _pages) entry.Button.Visible = names.Contains(entry.Button.Text);
        Select(_pages.First(p => p.Button.Text == names[0]).Page);
    }
    internal void RefreshSelected() => SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    public Panel AddPage(string title)
    {
        var page = new Panel { Dock = DockStyle.Fill, Padding = new(18), BackColor = Background, ForeColor = DashboardTheme.Text, Visible = false };
        var button = new DashboardButton { Text = title, Width = 128, Height = 35, Margin = new(0, 0, 6, 0), FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Muted };
        button.FlatAppearance.BorderColor = Border;
        button.Click += (_, _) => Select(page);
        _pages.Add((page, button)); _body.Controls.Add(page); _nav.Controls.Add(button);
        if (_pages.Count == 1) Select(page);
        return page;
    }
    private void Select(Panel selected)
    {
        if (SelectedTab == selected) return;
        foreach (var transition in _body.Controls.OfType<PageTransition>().ToArray()) transition.Dispose();
        var before = SelectedTab == null ? null : PageTransition.Snapshot(_body);
        SelectedTab = selected;
        foreach (var (page, button) in _pages) { page.Visible = page == selected; button.BackColor = page == selected ? BlueTint : Surface; button.ForeColor = page == selected ? Blue : Muted; }
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        _body.PerformLayout(); PageTransition.Show(_body, before);
    }
}
