using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace WorkTunnel;

/// <summary>Tray-only UI: one Connect/Disconnect control, live status, and the extras.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ITunnelController _ctrl = new BrokerClient();
    private readonly Form _pump; // hidden window used only to marshal onto the UI thread
    private readonly DashboardForm _dashboard;

    private readonly Icon _gray, _yellow, _green, _red;

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _connectItem;
    private readonly ToolStripMenuItem _disconnectItem;
    private readonly ToolStripMenuItem _killItem;
    private readonly ToolStripMenuItem _autoHealItem;
    private readonly ToolStripMenuItem _autoItem;
    private readonly ToolStripMenuItem _coverMenu;

    private readonly TrayNotificationPolicy _notifications = new();
#if OWNER_BUILD
    private readonly CoverSwitcher _cover;
    private volatile bool _coverBusy;
#endif

    public TrayContext(bool connectOnLaunch = false)
    {
        _ctrl.AutoHeal = Preferences.AutoReconnect();
        _pump = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
            Size = new Size(1, 1)
        };
        _ = _pump.Handle; // force handle creation on the UI thread

        _gray = MakeDot(Color.Gray);
        _yellow = MakeDot(Color.Goldenrod);
        _green = MakeDot(Color.SeaGreen);
        _red = MakeDot(Color.Firebrick);

        _statusItem = new ToolStripMenuItem("Disconnected") { Enabled = false };
        _connectItem = new ToolStripMenuItem("Connect", null, async (_, _) => await SafeConnect());
        _disconnectItem = new ToolStripMenuItem("Disconnect", null, async (_, _) => { try { await _ctrl.DisconnectAsync(); } catch (Exception e) { MessageBox.Show(e.Message, "Disconnect"); } })
        { Enabled = false };

        _killItem = new ToolStripMenuItem("Block internet if tunnel fails (whole PC)")
        { CheckOnClick = true, Checked = _ctrl.KillSwitch };
        _killItem.CheckedChanged += (_, _) =>
        {
            if (_ctrl.ConnectionRequested && _killItem.Checked != _ctrl.KillSwitch)
            { _killItem.Checked = _ctrl.KillSwitch; MessageBox.Show("Disconnect before changing this option.", "Network lock"); return; }
            try { _ctrl.KillSwitch = _killItem.Checked; }
            catch (Exception e) { MessageBox.Show(e.Message, "Network lock"); _killItem.Checked = _ctrl.KillSwitch; }
        };

        _autoHealItem = new ToolStripMenuItem("Auto-reconnect if tunnel drops")
        { CheckOnClick = true, Checked = _ctrl.AutoHeal };
        _autoHealItem.CheckedChanged += (_, _) => { _ctrl.AutoHeal = _autoHealItem.Checked; Preferences.Save(_autoHealItem.Checked); };

        _autoItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        try { _autoItem.Checked = AutoStart.IsEnabled(); } catch { }
        _autoItem.Click += (_, _) => ToggleAutoStart();

#if OWNER_BUILD
        _cover = new CoverSwitcher(_ctrl);
#endif
        _coverMenu = BuildCoverMenu();

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open dashboard", null, (_, _) => ShowDashboard()));
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_connectItem);
        menu.Items.Add(_disconnectItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_coverMenu);
        menu.Items.Add(_killItem);
        menu.Items.Add(_autoHealItem);
        menu.Items.Add(_autoItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Run diagnostics…", null, (_, _) => Diagnostics.Run()));
#if OWNER_BUILD
        menu.Items.Add(new ToolStripMenuItem("Data usage…", null, async (_, _) => await ShowUsage()));
#endif
        menu.Items.Add(new ToolStripMenuItem("Open logs folder", null,
            (_, _) => { try { Process.Start("explorer.exe", AppPaths.LogDir); } catch { } }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Close interface (keep tunnel running)", null, (_, _) => { Cleanup(); ExitThread(); }));
        menu.Items.Add(new ToolStripMenuItem("Disconnect and exit", null, (_, _) => ExitApp()));

        _icon = new NotifyIcon
        {
            Icon = _gray,
            Text = "Conduit — Disconnected",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ShowDashboard();

        _ctrl.StatusChanged += OnStatus;
        _ctrl.Notice += (msg, kind) => Post(() =>
            _icon.ShowBalloonTip(kind == NoticeKind.Info ? 3000 : 4000, "Conduit", msg, MapIcon(kind)));
#if OWNER_BUILD
        _cover.Progress += m => Post(() => _icon.ShowBalloonTip(2500, "Conduit — cover site", m, ToolTipIcon.Info));
#endif
        Application.ApplicationExit += (_, _) => Cleanup();
        _dashboard = new DashboardForm(_ctrl);
        _dashboard.ExitRequested += ExitApp;
        _dashboard.Show();
        if (connectOnLaunch) Post(() => _ = SafeConnect());
    }

    // ------------------------------------------------------------------ actions
    private void ShowDashboard() { _dashboard.Show(); _dashboard.WindowState = FormWindowState.Normal; _dashboard.Activate(); }
    public void RequestDashboard() => Post(ShowDashboard);

    private async Task Toggle()
    {
        if (_ctrl.State is TunnelState.Connected or TunnelState.Connecting)
            await _ctrl.DisconnectAsync();
        else
            await SafeConnect();
    }

    private async Task SafeConnect()
    {
        var p = Profile.Load();
        if (!p.IsValid)
        {
            MessageBox.Show("Open the dashboard and import this device's connection link first.",
                "Conduit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try { await _ctrl.ConnectAsync(); } catch (Exception e) { MessageBox.Show(e.Message, "Connect"); }
    }

    private void ToggleAutoStart()
    {
        try
        {
            bool ok = _autoItem.Checked ? AutoStart.Enable() : AutoStart.Disable();
            _autoItem.Checked = AutoStart.IsEnabled();
            if (!ok)
                MessageBox.Show("Could not change the auto-start task.", "Conduit",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch { }
    }

#if OWNER_BUILD
    private async Task ShowUsage()
    {
        var text = await UsageReader.ReadAsync();
        Post(() => new OutputForm("Tunnel data usage (shared VM)", text).Show());
    }
#endif

    // ------------------------------------------------------------------ cover site

    private ToolStripMenuItem BuildCoverMenu()
    {
        var root = new ToolStripMenuItem("Cover site");
        var active = Profile.Load().Sni;
        foreach (var c in CoverProfiles.All)
        {
            var item = new ToolStripMenuItem($"{c.Name}  ({c.Sni})")
            {
                Tag = c,
                Checked = string.Equals(c.Sni, active, StringComparison.OrdinalIgnoreCase),
            };
#if OWNER_BUILD
            item.Click += async (s, _) => { if (s is ToolStripMenuItem mi && mi.Tag is CoverSite cs) await SwitchCover(cs); };
            item.ToolTipText = "Switch the whole tunnel to this cover (server + client), with a 90s auto-revert safety.";
#else
            item.Enabled = false;
            item.ToolTipText = "Cover switching is available in the owner build.";
#endif
            root.DropDownItems.Add(item);
        }
        return root;
    }

    private void RefreshCoverChecks()
    {
        var active = Profile.Load().Sni;
        foreach (ToolStripItem it in _coverMenu.DropDownItems)
            if (it is ToolStripMenuItem mi && mi.Tag is CoverSite cs)
                mi.Checked = string.Equals(cs.Sni, active, StringComparison.OrdinalIgnoreCase);
    }

#if OWNER_BUILD
    private async Task SwitchCover(CoverSite site)
    {
        if (File.Exists(Path.Combine(AppPaths.DataDir, "pending-device-operation.json")))
        {
            MessageBox.Show("Finish the pending change in Your devices before switching the cover site.", "Conduit");
            return;
        }
        if (_coverBusy) return;
        if (string.Equals(Profile.Load().Sni, site.Sni, StringComparison.OrdinalIgnoreCase))
        {
            _icon.ShowBalloonTip(2000, "Conduit", $"Already on {site.Name}.", ToolTipIcon.Info);
            return;
        }
        var confirm = MessageBox.Show(
            $"Switch the tunnel's cover to {site.Name} ({site.Sni})?\n\n" +
            "This briefly drops the connection while it re-establishes. If the new cover " +
            "doesn't work, it auto-reverts within ~90 seconds.",
            "Conduit — switch cover site", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;

        _coverBusy = true;
        _coverMenu.Enabled = false;
        try
        {
            var (ok, message) = await _cover.SwitchAsync(site);
            Post(() =>
            {
                RefreshCoverChecks();
                _icon.ShowBalloonTip(4000, "Conduit — cover site", message,
                    ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
            });
        }
        finally
        {
            _coverBusy = false;
            Post(() => _coverMenu.Enabled = true);
        }
    }
#endif

    // ------------------------------------------------------------------ status

    private void OnStatus(object? sender, StatusEventArgs e) => Post(() => Apply(e));

    private void Apply(StatusEventArgs e)
    {
        Icon icon = e.State switch
        {
            TunnelState.Disconnected => _gray,
            TunnelState.Connecting => _yellow,
            TunnelState.Faulted => _red,
            TunnelState.Connected => e.Verified ? _green : _yellow,
            _ => _gray
        };
        _icon.Icon = icon;
        _autoHealItem.Checked = _ctrl.AutoHeal;
        _statusItem.Text = e.Message;
        _icon.Text = Trunc("Conduit — " + e.Message, 63);
        RefreshCoverChecks();

        _connectItem.Enabled = e.State is TunnelState.Disconnected or TunnelState.Faulted;
        _disconnectItem.Enabled = e.State is TunnelState.Connecting or TunnelState.Connected or TunnelState.Faulted;

        if (_notifications.Observe(e) is { } notice)
            _icon.ShowBalloonTip(notice == NoticeKind.Info ? 2500 : 4000, "Conduit", e.Message, MapIcon(notice));
    }

    private static ToolTipIcon MapIcon(NoticeKind k) => k switch
    {
        NoticeKind.Warning => ToolTipIcon.Warning,
        NoticeKind.Error => ToolTipIcon.Error,
        _ => ToolTipIcon.Info
    };

    // ------------------------------------------------------------------ helpers

    private void Post(Action a)
    {
        try { if (_pump.IsHandleCreated) _pump.BeginInvoke(a); else a(); }
        catch { }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static Icon MakeDot(Color color) => BrandIcon.WithStatus(color);

    private async void ExitApp()
    {
        try { if (_ctrl is BrokerClient broker) await broker.ShutdownAsync(); }
        catch (Exception e) { MessageBox.Show(e.Message + "\nUse Restore normal internet if the host is unavailable.", "Could not stop tunnel"); return; }
        Cleanup();
        ExitThread();
    }

    private bool _cleaned;
    private void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;
        try { _ctrl.Dispose(); } catch { }
        try { _dashboard.Shutdown(); _dashboard.Dispose(); } catch { }
        try { if (_icon != null) { _icon.Visible = false; _icon.Dispose(); } } catch { }
        _gray.Dispose(); _yellow.Dispose(); _green.Dispose(); _red.Dispose();
        try { _pump.Dispose(); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Cleanup();
        base.Dispose(disposing);
    }
}
