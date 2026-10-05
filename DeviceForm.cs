#if OWNER_BUILD
using QRCoder;

namespace WorkTunnel;

internal sealed class DeviceForm : Form
{
    private readonly DeviceManager _manager;
    private DeviceList? _data;
    private readonly ListBox _devices = new() { Dock = DockStyle.Fill, DisplayMember = "DisplayName", IntegralHeight = false };
    private readonly TextBox _name = new() { Width = 190, PlaceholderText = "e.g. My phone", MaxLength = 60 };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = false };
    private readonly PictureBox _qr = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private readonly TextBox _link = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly FlowLayoutPanel _actions = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Button _revoke;
    private bool _busy;

    public DeviceForm(ITunnelController controller)
    {
        _manager = new(controller);
        DashboardTheme.StyleCaption(this);
        Text = "Your devices · Conduit"; Size = new(850, 680); MinimumSize = new(760, 620);
        StartPosition = FormStartPosition.CenterParent; Font = new("Segoe UI", 10);
        BackColor = DashboardTheme.Background; ForeColor = DashboardTheme.Text;
        _devices.BackColor = DashboardTheme.Surface; _devices.ForeColor = DashboardTheme.Text; _devices.BorderStyle = BorderStyle.FixedSingle;
        _name.BackColor = DashboardTheme.Surface; _name.ForeColor = DashboardTheme.Text;
        _link.BackColor = DashboardTheme.PinkTint; _link.ForeColor = DashboardTheme.Text;
        _status.ForeColor = DashboardTheme.Muted;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), ColumnCount = 2, RowCount = 5 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 42)); layout.ColumnStyles.Add(new(SizeType.Percent, 58));
        foreach (var height in new[] { 65f, 45f, 0f, 100f, 45f }) layout.RowStyles.Add(new(height == 0 ? SizeType.Percent : SizeType.Absolute, height == 0 ? 100 : height));
        var intro = new Label { Dock = DockStyle.Fill, Text = "Each device gets its own connection. Select a device to show its link and QR code.\nAdding or revoking briefly reconnects devices on this VM. Existing clients are protected." };
        layout.Controls.Add(intro, 0, 0); layout.SetColumnSpan(intro, 2);
        var refresh = MakeButton("Refresh", async () => await RefreshAsync());
        var add = MakeButton("Add device", async () =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text)) { _status.Text = "Enter a device name first."; return; }
            var id = Guid.NewGuid().ToString();
            await RunAsync(async () => { await _manager.ChangeAsync("add", id, _name.Text.Trim(), Progress); await LoadAsync(id); _name.Clear(); });
        });
        _revoke = MakeButton("Revoke selected", async () =>
        {
            if (_devices.SelectedItem is not TunnelDevice device || !device.Managed) return;
            if (MessageBox.Show(this, $"Revoke {device.Name}? Its saved link will stop working.", "Revoke device", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            await RunAsync(async () => { await _manager.ChangeAsync("revoke", device.Id, "", Progress); await LoadAsync(); });
        });
        _actions.Controls.AddRange(new Control[] { _name, add, refresh, _revoke });
        layout.Controls.Add(_actions, 0, 1); layout.SetColumnSpan(_actions, 2);
        layout.Controls.Add(_devices, 0, 2); layout.Controls.Add(_qr, 1, 2);
        var share = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        share.RowStyles.Add(new(SizeType.Percent, 100)); share.RowStyles.Add(new(SizeType.Absolute, 36));
        share.Controls.Add(_link, 0, 0);
        var shareButtons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        shareButtons.Controls.Add(MakeButton("Copy link", () => { if (_link.Text.Length > 0) Clipboard.SetText(_link.Text); return Task.CompletedTask; }));
        shareButtons.Controls.Add(MakeButton("Save QR", () =>
        {
            if (_qr.Image == null) return Task.CompletedTask;
            using var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "device-connection.png" };
            if (dialog.ShowDialog(this) == DialogResult.OK) _qr.Image.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
            return Task.CompletedTask;
        }));
        share.Controls.Add(shareButtons, 0, 1); layout.Controls.Add(share, 0, 3); layout.SetColumnSpan(share, 2);
        layout.Controls.Add(_status, 0, 4); layout.SetColumnSpan(_status, 2);
        Controls.Add(layout);
        _devices.SelectedIndexChanged += (_, _) => ShowSelected();
        Shown += async (_, _) => await RefreshAsync();
    }
    private static Button MakeButton(string text, Func<Task> click)
    {
        var button = new DashboardButton { Text = text, AutoSize = true, Height = 32, FlatStyle = FlatStyle.Flat,
            BackColor = DashboardTheme.BlueTint, ForeColor = DashboardTheme.Text, Cursor = Cursors.Hand };
        button.Click += async (_, _) => { try { await click(); } catch (Exception error) { MessageBox.Show(error.Message, "Conduit"); } };
        return button;
    }
    private void Progress(string message) { if (!IsDisposed) _status.Text = message; }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; _actions.Enabled = false;
        try { await action(); }
        catch (Exception error) { Progress(error.Message); }
        finally { _busy = false; if (!IsDisposed) { _actions.Enabled = true; ShowSelected(); } }
    }
    private Task RefreshAsync() => RunAsync(async () => { await _manager.ResumeAsync(Progress); await LoadAsync(); });
    private async Task LoadAsync(string? selected = null)
    {
        _data = await _manager.ListAsync();
        if (IsDisposed) return;
        _devices.DataSource = _data.Devices;
        _devices.SelectedIndex = -1;
        if (selected != null) _devices.SelectedItem = _data.Devices.FirstOrDefault(d => d.Id == selected);
        Progress("Import the selected link in Conduit or a compatible VLESS/REALITY mobile app. Keep it private.");
    }
    private void ShowSelected()
    {
        if (IsDisposed) return;
        _qr.Image?.Dispose(); _qr.Image = null; _link.Clear(); _revoke.Enabled = false;
        if (_data == null || _devices.SelectedItem is not TunnelDevice device) return;
        _revoke.Enabled = device.Managed && device.Id != Profile.Load().Uuid && !_busy;
        try
        {
            _link.Text = DeviceLink.Create(DeviceManager.ConnectionFor(device, _data));
            using var generator = new QRCodeGenerator();
            using var code = generator.CreateQrCode(_link.Text, QRCodeGenerator.ECCLevel.M);
            using var png = new PngByteQRCode(code);
            using var stream = new MemoryStream(png.GetGraphic(6));
            using var image = Image.FromStream(stream);
            _qr.Image = new Bitmap(image);
        }
        catch (Exception error) { Progress(error.Message); }
    }
    protected override void Dispose(bool disposing) { if (disposing) _qr.Image?.Dispose(); base.Dispose(disposing); }
}
#endif
