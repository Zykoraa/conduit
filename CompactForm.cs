using static WorkTunnel.DashboardTheme;

namespace WorkTunnel;

internal sealed class CompactForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    public CompactForm(ITunnelController controller, Func<string> traffic, Action showDashboard, bool persist = true)
    {
        Text = "Conduit · Compact"; ClientSize = new(385, 195); FormBorderStyle = FormBorderStyle.FixedToolWindow;
        TopMost = true; BackColor = Background; ForeColor = DashboardTheme.Text; Font = new("Segoe UI", 10); StyleCaption(this);
        var grid = Grid(100); grid.Padding = new(15); grid.RowCount = 5;
        foreach (int height in new[] { 35, 28, 28, 34, 32 }) grid.RowStyles.Add(new(SizeType.Absolute, height));
        var state = Label("Waiting", 16, Healthy, true); var latency = Label("—", 10, Muted); var rate = Label("—", 10, Muted);
        grid.Controls.Add(state, 0, 0); grid.Controls.Add(latency, 0, 1); grid.Controls.Add(rate, 0, 2);
        var restore = new DashboardButton { Text = "Open dashboard", Dock = DockStyle.Fill, BackColor = AccentPink, ForeColor = Background };
        restore.Click += (_, _) => showDashboard(); grid.Controls.Add(restore, 0, 3);
        var top = new CheckBox { Text = "Always on top", Checked = true, AutoSize = true, ForeColor = DashboardTheme.Text }; top.CheckedChanged += (_, _) => TopMost = top.Checked; grid.Controls.Add(top, 0, 4);
        Controls.Add(grid);
        void RefreshReadings()
        {
            var h = controller.Health; bool fresh = controller.State == TunnelState.Connected && HealthFreshness.IsCurrent(h, DateTimeOffset.UtcNow);
            state.Text = controller.State == TunnelState.Connected ? controller.Verified ? "Connection verified" : "Needs verification" : controller.State.ToString();
            state.ForeColor = controller.Verified ? Healthy : Muted;
            latency.Text = fresh && h!.Internet.Milliseconds.HasValue ? $"HTTPS check: {h.Internet.Milliseconds} ms" : "No recent HTTPS measurement";
            rate.Text = traffic();
        }
        _timer.Tick += (_, _) => RefreshReadings(); Shown += (_, _) => { RefreshReadings(); _timer.Start(); };
        if (persist) { UiPreferences.Apply(this); WindowPreferences.Attach(this, "compact"); }
    }
    protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }
}
