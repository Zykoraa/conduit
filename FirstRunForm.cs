using System.Text.Json;
using static WorkTunnel.DashboardTheme;

namespace WorkTunnel;

internal sealed class FirstRunForm : Form
{
    public FirstRunForm()
    {
        Text = "Set up this device · Conduit"; ClientSize = new(820, 470); MinimumSize = new(780, 440);
        StartPosition = FormStartPosition.CenterParent; BackColor = Background; ForeColor = DashboardTheme.Text; Font = new("Segoe UI", 11); StyleCaption(this);
        var layout = Grid(100); layout.Padding = new(24); layout.RowCount = 4;
        foreach (int height in new[] { 140, 120, 65, 60 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        var intro = Label("1. Finish any Wi-Fi sign-in page first.\n2. Stop other tunnel apps such as v2rayN.\n3. Import the personal link issued for THIS device.\n4. Connect, then test your actual voice/video app.", 11);
        var input = new TextBox { Dock = DockStyle.Fill, Multiline = true, BackColor = Surface, ForeColor = DashboardTheme.Text, PlaceholderText = "Paste your vless:// connection link", AccessibleName = "Personal connection link" };
        var status = Label("Saved credentials are encrypted for this Windows user. New installers contain no device identity.", 10, Muted);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var save = new DashboardButton { Text = "&Import link", AutoSize = true, BackColor = AccentPink, ForeColor = Background, Padding = new(12), AccessibleName = "Import personal connection link" };
        var legacy = new DashboardButton { Text = "Import &previous profile", AutoSize = true, BackColor = BlueTint, ForeColor = DashboardTheme.Text, Padding = new(12) };
        save.Click += (_, _) => { try { DeviceLink.Parse(input.Text).Save(); DialogResult = DialogResult.OK; } catch (Exception e) { status.Text = e.Message; } };
        legacy.Click += (_, _) =>
        {
            using var file = new OpenFileDialog { Filter = "Previous WorkTunnel / Conduit profile|profile.json", Title = "Select configs/profile.json from the previous installation" };
            if (file.ShowDialog(this) != DialogResult.OK) return;
            try { ImportLegacy(File.ReadAllText(file.FileName)).Save(); DialogResult = DialogResult.OK; }
            catch (Exception e) { status.Text = e.Message; }
        };
        var v2ray = new DashboardButton { Text = "Import from &v2rayN", AutoSize = true, BackColor = BlueTint, ForeColor = DashboardTheme.Text, Padding = new(12) };
        v2ray.Click += (_, _) => { using var import = new V2rayImportForm(); if (import.ShowDialog(this) == DialogResult.OK) DialogResult = DialogResult.OK; };
        actions.Controls.Add(save); actions.Controls.Add(v2ray); actions.Controls.Add(legacy);
        layout.Controls.Add(intro, 0, 0); layout.Controls.Add(input, 0, 1); layout.Controls.Add(status, 0, 2); layout.Controls.Add(actions, 0, 3); Controls.Add(layout);
        AcceptButton = save;
        UiPreferences.Apply(this);
    }
    internal static Profile ImportLegacy(string json)
    {
        if (json.Length > 8192) throw new InvalidDataException("Profile is too large.");
        var profile = JsonSerializer.Deserialize<Profile>(json) ?? throw new InvalidDataException("Invalid profile.");
        if (!profile.IsValid) throw new InvalidDataException("This file does not contain a valid connection.");
        if (profile.PublicKey.Length == 0 || profile.ShortId.Length == 0)
            throw new InvalidDataException("This older profile is incomplete. Import a full connection link containing the server's public key and short ID.");
        _ = DeviceLink.Create(profile); return profile;
    }
}
