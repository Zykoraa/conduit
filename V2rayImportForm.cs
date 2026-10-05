using static WorkTunnel.DashboardTheme;

namespace WorkTunnel;

internal sealed class V2rayImportForm : Form
{
    private readonly CancellationTokenSource _stop = new();
    public V2rayImportForm()
    {
        Text = "Import from v2rayN · Conduit"; Size = new(760, 740); MinimumSize = new(660, 560);
        BackColor = Background; ForeColor = DashboardTheme.Text; Font = new("Segoe UI", 10); StartPosition = FormStartPosition.CenterParent; StyleCaption(this);
        var grid = Grid(100); grid.Padding = new(24); grid.RowCount = 8;
        foreach (int h in new[] { 78, 32, 45, 42, 38, 130, 90, 52 }) grid.RowStyles.Add(new(SizeType.Absolute, h));
        Controls.Add(grid);
        grid.Controls.Add(Label("Use the server you already have in v2rayN.\nSelect that server and connect once in v2rayN to generate its Xray config, then close v2rayN before connecting with Conduit.", 11), 0, 0);
        grid.Controls.Add(Label("v2rayN Xray configuration", 10, Muted), 0, 1);
        var paths = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AccessibleName = "v2rayN config path" };
        foreach (var candidate in V2rayImport.FindCandidates()) paths.Items.Add(candidate);
        if (paths.Items.Count > 0) paths.SelectedIndex = 0;
        var select = Grid(78, 22); select.Controls.Add(paths, 0, 0);
        var browse = new Button { Text = "Browse…", Dock = DockStyle.Fill };
        browse.Click += (_, _) => { using var file = new OpenFileDialog { Filter = "Xray JSON config|*.json", Title = "Choose v2rayN/binConfigs/config.json" }; if (file.ShowDialog(this) == DialogResult.OK) paths.Text = file.FileName; };
        select.Controls.Add(browse, 1, 0); grid.Controls.Add(select, 0, 2);
        grid.Controls.Add(Label("Expected public exit IPv4 (optional; use if your server exits through another IP)", 10, Muted), 0, 3);
        var expected = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Blank: use the server address", AccessibleName = "Expected exit IPv4" }; grid.Controls.Add(expected, 0, 4);
        var summary = Label("Only the selected server is imported. Conduit supplies its own full-device routing, DNS, network lock and bundled engines. v2rayN subscriptions, routing rules, certificates and plugins are not imported.\n\nSupported: VLESS / VMess / Trojan / Shadowsocks, TCP / WebSocket / gRPC, TLS / REALITY. Unsupported settings are rejected.", 10, Muted);
        grid.Controls.Add(summary, 0, 5);
        var status = Label("Your existing Conduit profile stays in place until validation succeeds and you choose Save.", 10, Blue); grid.Controls.Add(status, 0, 6);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var inspect = new Button { Text = "Read and validate", AutoSize = true, Height = 36 };
        var save = new Button { Text = "Save imported server", AutoSize = true, Height = 36, Enabled = false };
        Profile? candidateProfile = null;
        void InvalidateCandidate() { candidateProfile = null; save.Enabled = false; }
        paths.TextChanged += (_, _) => InvalidateCandidate(); expected.TextChanged += (_, _) => InvalidateCandidate();
        inspect.Click += async (_, _) =>
        {
            inspect.Enabled = false; browse.Enabled = false; paths.Enabled = false; expected.Enabled = false; InvalidateCandidate();
            try
            {
                status.Text = "Reading server settings and checking the bundled engines…";
                string path = paths.Text; string exit = expected.Text.Trim();
                var imported = await Task.Run(() => V2rayImport.Read(path), _stop.Token);
                imported.ExpectedExitIp = exit;
                if (!imported.IsValid) throw new InvalidDataException("Enter a valid expected exit IPv4 address, or leave it blank.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                await V2rayImport.ResolveAsync(imported, deadline.Token);
                await EngineConfiguration.ValidateProfileAsync(imported, deadline.Token);
                if (IsDisposed) return;
                candidateProfile = imported; save.Enabled = true;
                status.Text = $"Validated: {imported.Imported!.Protocol.ToUpperInvariant()} / {imported.Imported.Transport} / {imported.Imported.Security}\n{imported.Server}:{imported.Port}\nSave stores credentials encrypted for your Windows account.";
            }
            catch (OperationCanceledException) { if (!IsDisposed) status.Text = "Validation canceled or timed out. Your profile was kept."; }
            catch (Exception e) { if (!IsDisposed) status.Text = e is InvalidDataException ? e.Message : "Could not read or validate that config. Choose v2rayN's binConfigs/config.json. Your profile was kept."; }
            finally { if (!IsDisposed) { inspect.Enabled = true; browse.Enabled = true; paths.Enabled = true; expected.Enabled = true; } }
        };
        save.Click += (_, _) => { try { if (candidateProfile == null) return; candidateProfile.Save(); DialogResult = DialogResult.OK; } catch (Exception) { status.Text = "Could not save the encrypted profile. Check access to your Windows data folder."; } };
        actions.Controls.Add(inspect); actions.Controls.Add(save); grid.Controls.Add(actions, 0, 7);
        AutoScroll = true; grid.Dock = DockStyle.Top; grid.Height = 655;
        FormClosing += (_, _) => _stop.Cancel(); UiPreferences.Apply(this);
    }
    protected override void Dispose(bool disposing) { if (disposing) _stop.Cancel(); base.Dispose(disposing); }
}
