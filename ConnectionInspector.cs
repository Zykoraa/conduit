using static WorkTunnel.DashboardTheme;

namespace WorkTunnel;

internal sealed class ConnectionInspector : DashboardSurface
{
    private readonly Label _place = Label("Destination", 19, Healthy);
    private readonly TextBox _facts = new() { Dock = DockStyle.Top, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.None, BackColor = Surface, ForeColor = DashboardTheme.Text, Font = new("Segoe UI", 10) };
    private bool _fitting;
    private ConnectionSnapshot _data = ConnectionSnapshot.Empty();
    public string? SelectedAddress { get; private set; }
    public event Action? CloseRequested;
    public ConnectionInspector()
    {
        Margin = Padding.Empty; Padding = new(20); AutoScroll = true; AccessibleName = "Connection inspector";
        var grid = Grid(100); grid.RowCount = 4; grid.Dock = DockStyle.Top; grid.AutoSize = true; grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        for (int i = 0; i < 4; i++) grid.RowStyles.Add(new(SizeType.AutoSize));
        var close = new DashboardButton { Text = "Close inspector  ×", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ForeColor = Muted, BackColor = BlueTint, Padding = new(8), AccessibleName = "Close connection inspector" };
        close.Click += (_, _) => CloseRequested?.Invoke();
        grid.Controls.Add(close, 0, 0); _place.AutoSize = true; _place.Padding = new(0, 18, 0, 14); grid.Controls.Add(_place, 0, 1);
        _facts.AccessibleName = "Selected connection details"; grid.Controls.Add(_facts, 0, 2);
        var note = Label("These destinations come from the local tunnel engine. No location service is contacted. This sample is not a complete per-process inventory.", 9, Muted); note.AutoSize = true; note.Padding = new(0, 14, 0, 0); grid.Controls.Add(note, 0, 3);
        Controls.Add(grid);
        Layout += (_, _) => FitFacts(); _facts.TextChanged += (_, _) => FitFacts();
    }
    private void FitFacts()
    {
        if (_fitting) return; _fitting = true;
        try { _facts.Height = TextRenderer.MeasureText(_facts.Text, _facts.Font, new Size(Math.Max(100, _facts.ClientSize.Width - 8), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + _facts.Font.Height; }
        finally { _fitting = false; }
    }
    public void SelectAddress(string? address) { SelectedAddress = address; RefreshFacts(); }
    public void UpdateData(ConnectionSnapshot data) { _data = data; RefreshFacts(); }
    private void RefreshFacts()
    {
        if (SelectedAddress == null) return;
        var matches = _data.Destinations.Where(d => d.Address == SelectedAddress).ToArray();
        _place.Text = matches.FirstOrDefault()?.Name is { Length: > 0 } name ? name : SelectedAddress;
        _facts.Text = matches.Length == 0 ? SelectedAddress + "\r\n\r\nThis destination is no longer in the current sample." :
            $"ADDRESS\r\n{SelectedAddress}\r\n\r\n" +
            (matches.Any(d => d.Name.Length > 0) ? "HOST\r\n" + string.Join(" / ", matches.Select(d => d.Name).Where(n => n.Length > 0).Distinct()) + "\r\n\r\n" : "") +
            "PROTOCOL\r\n" + string.Join(" / ", matches.Select(d => d.Network.ToUpperInvariant()).Distinct()) + "\r\n\r\n" +
            "ACTIVITY\r\n" + (matches.Any(d => d.Active) ? "Traffic observed in the latest sample" : "Open · no recent traffic observed") + "\r\n\r\n" +
            $"RECEIVED\r\n{TrafficMeter.Bytes(matches.Sum(d => d.Downloaded))}\r\n\r\nSENT\r\n{TrafficMeter.Bytes(matches.Sum(d => d.Uploaded))}\r\n\r\n" +
            $"SAMPLED\r\n{_data.At.LocalDateTime:T}\r\n\r\nTotals cover the connections reported by the tunnel. They are not a device-wide lifetime total.";
        AccessibleDescription = _place.Text + ". " + _facts.Text;
    }
}
