using static WorkTunnel.DashboardTheme;

namespace WorkTunnel;

internal sealed class UpdateFeedPanel : UserControl
{
    private readonly ITunnelController _controller;
    private readonly Func<string, CancellationToken, Task> _review;
    private readonly bool _online;
    private readonly TextBox _address = new() { Dock = DockStyle.Fill, BackColor = Surface, ForeColor = DashboardTheme.Text, BorderStyle = BorderStyle.FixedSingle };
    private readonly CheckBox _automatic = new() { AutoSize = true, Text = "Check automatically through a verified tunnel", ForeColor = DashboardTheme.Text };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Muted, Padding = new(0, 6, 0, 8) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 12, Visible = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 120000 };
    private readonly Button _save, _check, _download, _cancel;
    private UpdateOptions _options;
    private VerifiedUpdate? _candidate;
    private CancellationTokenSource? _operation;
    private DateTimeOffset _nextAutomatic;
    private bool _busy;
    private static int _checking;
    internal bool Busy => _busy;

    internal UpdateFeedPanel(ITunnelController controller, Func<string, CancellationToken, Task> review, bool online)
    {
        _controller = controller; _review = review; _online = online;
        _options = UpdatePreferences.Read(UpdatePreferences.PathName);
        Dock = DockStyle.Top; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new(0, 0, 0, 12) };
        grid.ColumnStyles.Add(new(SizeType.Percent, 100)); Controls.Add(grid);
        void Row(Control c) { int row = grid.RowCount++; grid.RowStyles.Add(new(SizeType.AutoSize)); grid.Controls.Add(c, 0, row); }
        Row(new Label { Text = "Signed update feed (HTTPS)", AutoSize = true, ForeColor = Muted });
        _address.AccessibleName = "Signed update feed address"; _address.Text = _options.Feed; Row(_address);
        _automatic.Checked = _options.Automatic; Row(_automatic);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new(0, 6, 0, 0) };
        Button Add(string title, Action action)
        {
            var button = new DashboardButton { Text = title, AutoSize = true, BackColor = BlueTint, ForeColor = DashboardTheme.Text, Padding = new(8, 4, 8, 4) };
            button.Click += (_, _) => action(); buttons.Controls.Add(button); return button;
        }
        _save = Add("Save feed", Save);
        _check = Add("Check now", () => _ = CheckAsync(false));
        _download = Add("Download and review", () => _ = DownloadAsync()); _download.Enabled = false;
        _cancel = Add("Cancel", () => _operation?.Cancel()); _cancel.Enabled = false;
        Row(buttons); Row(_status); Row(_progress);
        _address.TextChanged += (_, _) => { _candidate = null; _download.Enabled = false; };
        _status.Text = _options.Feed.Length == 0 ? "Add the publisher's signed feed to enable discovery."
            : "Checks verify the release signature. Downloads and installation require your action.";
        _timer.Tick += async (_, _) =>
        {
            _options = UpdatePreferences.Read(UpdatePreferences.PathName);
            if (_online && !_busy && _controller.ConnectionRequested && _controller.Verified &&
                DateTimeOffset.UtcNow >= _nextAutomatic && UpdatePreferences.Due(_options, DateTimeOffset.UtcNow))
                await CheckAsync(true);
        };
        _timer.Enabled = _online && _options.Automatic;
        _nextAutomatic = DateTimeOffset.UtcNow.AddMinutes(2);
        if (!_online) { _save.Enabled = false; _check.Enabled = false; _automatic.Enabled = false; _address.ReadOnly = true; }
    }
    private bool SaveOptions()
    {
        string address = _address.Text.Trim();
        if (address.Length > 0) _ = UpdateFeedCodec.FeedAddress(address);
        if (_automatic.Checked && address.Length == 0) throw new InvalidDataException("Add a signed feed address before enabling automatic checks.");
        bool changed = address != _options.Feed;
        _options = _options with { Feed = address, Automatic = _automatic.Checked, LastChecked = changed ? null : _options.LastChecked };
        UpdatePreferences.Write(UpdatePreferences.PathName, _options);
        _timer.Enabled = _options.Automatic && _online;
        if (changed) { _candidate = null; _download.Enabled = false; }
        return address.Length > 0;
    }
    private void Save()
    {
        if (!_online || _busy) return;
        try
        {
            SaveOptions();
            _status.Text = _options.Automatic ? "Saved. Automatic checks run at most every 12 hours through a verified tunnel. Downloads remain manual."
                : "Saved. Automatic checks are off. Check now uses the tunnel when connected, or ordinary internet when disconnected.";
        }
        catch (Exception e) { _status.Text = e.Message; }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy; _address.Enabled = !busy; _automatic.Enabled = !busy && _online;
        _save.Enabled = _check.Enabled = !busy && _online; _cancel.Enabled = busy;
        _download.Enabled = !busy && _candidate != null;
    }
    private async Task CheckAsync(bool automatic)
    {
        if (_busy || !_online || Interlocked.CompareExchange(ref _checking, 1, 0) != 0) return;
        try
        {
            if (!automatic && !SaveOptions()) throw new InvalidDataException("Add the publisher's signed feed address first.");
            SetBusy(true); _candidate = null; _download.Enabled = false;
            _operation = new(); _status.Text = "Checking signed release metadata…";
            using var discovery = UpdateDiscovery.ForController(_controller);
            var result = await discovery.CheckAsync(_options, _operation.Token);
            if (IsDisposed) return;
            // Persist only a cryptographically verified version, never an unsigned network claim.
            _options = _options with { HighestSeen = result.Release.Version, LastChecked = DateTimeOffset.UtcNow };
            UpdatePreferences.Write(UpdatePreferences.PathName, _options);
            if (UpdateFeedCodec.VersionNumber(result.Release.Version) > UpdateFeedCodec.Normalize(typeof(UpdateFeedPanel).Assembly.GetName().Version!))
            {
                _candidate = result;
                _status.Text = $"Verified release {result.Release.Version} is available ({result.Release.BundleBytes / 1048576.0:0.#} MiB). Download and review to continue.";
            }
            else _status.Text = "No newer stable release in this verified feed. Checked " + DateTime.Now.ToString("g") + ".";
        }
        catch (OperationCanceledException) { if (!IsDisposed) _status.Text = "Update check cancelled or timed out."; }
        catch (Exception e) { if (!IsDisposed) _status.Text = "Update check failed: " + e.Message; }
        finally
        {
            _nextAutomatic = DateTimeOffset.UtcNow.AddHours(1);
            _operation?.Dispose(); _operation = null;
            Interlocked.Exchange(ref _checking, 0);
            if (!IsDisposed) SetBusy(false);
        }
    }
    private async Task DownloadAsync()
    {
        if (_busy || !_online || _candidate == null) return;
        string? bundle = null;
        try
        {
            SetBusy(true); _operation = new(); _progress.Value = 0; _progress.Visible = true;
            _status.Text = "Downloading the signed release…";
            using var discovery = UpdateDiscovery.ForController(_controller);
            bundle = await discovery.DownloadAsync(_candidate, UpdateInstaller.Root, percent =>
            {
                if (!IsDisposed) _progress.Value = percent;
            }, _operation.Token);
            if (IsDisposed) return;
            _status.Text = "Download hash and size verified. Checking the signed bundle and engines…";
            await _review(bundle, _operation.Token);
            if (!IsDisposed) _status.Text = "Download verified. Installation proceeds only after confirmation.";
        }
        catch (OperationCanceledException) { if (!IsDisposed) _status.Text = "Download cancelled or timed out; partial file removed."; }
        catch (Exception e) { if (!IsDisposed) _status.Text = "Update not installed: " + e.Message; }
        finally
        {
            // The protected installer stages the verified bundle before the dashboard exits.
            if (bundle != null) try { File.Delete(bundle); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _operation?.Dispose(); _operation = null;
            if (!IsDisposed) { _progress.Visible = false; SetBusy(false); }
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Stop(); _timer.Dispose(); _operation?.Cancel(); }
        base.Dispose(disposing);
    }
}
