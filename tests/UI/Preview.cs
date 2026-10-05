using System.Reflection;
using System.Runtime.InteropServices;

namespace WorkTunnel;
internal static class Preview
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private static void CheckFrame(Form owner, List<string> issues)
    {
        using var window = new ConduitWindow { Text = "Frame checks", Opacity = 0, ShowInTaskbar = false, Size = new(800, 600), StartPosition = FormStartPosition.Manual, Location = owner.Location };
        window.Show(); window.PerformLayout();
        int Hit(Point point)
        {
            var screen = window.PointToScreen(point);
            return (int)SendMessageW(window.Handle, 0x84, IntPtr.Zero, (IntPtr)((screen.X & 0xffff) | ((screen.Y & 0xffff) << 16)));
        }
        foreach (var (point, expected) in new (Point, int)[] { (new(0, 0), 13), (new(window.ClientSize.Width - 1, 0), 14), (new(0, window.ClientSize.Height - 1), 16), (new(window.ClientSize.Width - 1, window.ClientSize.Height - 1), 17), (new(100, 20), 2), (new(100, 100), 1) })
            if (Hit(point) != expected) issues.Add($"Frame hit target {point}: expected {expected}");
        var max = window.MaximizeTarget;
        if (Hit(new(max.Left + max.Width / 2, max.Top + max.Height / 2)) != 9) issues.Add("Maximize target does not expose native Snap hit code");
        // Exercise the native non-client button message path, not just its helper method.
        void Click(int hit) { SendMessageW(window.Handle, 0xA1, (IntPtr)hit, IntPtr.Zero); SendMessageW(window.Handle, 0xA2, (IntPtr)hit, IntPtr.Zero); }
        var restored = window.Bounds;
        Click(9);
        var clientScreen = window.RectangleToScreen(window.ClientRectangle);
        if (window.WindowState != FormWindowState.Maximized || clientScreen != Screen.FromControl(window).WorkingArea) issues.Add("Maximized client does not fill the monitor work area");
        Click(9);
        if (window.WindowState != FormWindowState.Normal || window.Bounds != restored) issues.Add("Restore did not recover the original window bounds");
        Click(8);
        if (window.WindowState != FormWindowState.Minimized) issues.Add("Minimize caption action failed");
        window.WindowState = FormWindowState.Normal;
        window.Location = new(-400, 100);
        if (Hit(new(100, 20)) != 2) issues.Add("Caption hit testing fails at negative screen coordinates");
        bool closeRequested = false;
        window.FormClosing += (_, e) => { closeRequested = true; e.Cancel = true; window.Hide(); };
        Click(20);
        if (!closeRequested || window.IsDisposed || window.Visible) issues.Add("Close bypasses the close-to-tray FormClosing handler");
    }
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 0) args = ["--demo", "--large-text"];
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var controller = new TunnelController { AutoHeal = false };
        if (args.Contains("--import-preview") || args.Contains("--first-run-preview"))
        {
            using Form wizard = args.Contains("--import-preview") ? new V2rayImportForm() : new FirstRunForm();
            wizard.Opacity = 0; wizard.ShowInTaskbar = false;
            if (args.Contains("--large-text")) UiPreferences.Apply(wizard, 150);
            int snapshot = Array.IndexOf(args, "--snapshot");
            wizard.Shown += (_, _) =>
            {
                if (snapshot >= 0 && snapshot + 1 < args.Length)
                {
                    using var bitmap = new Bitmap(wizard.Width, wizard.Height);
                    wizard.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(args[snapshot + 1], System.Drawing.Imaging.ImageFormat.Png);
                }
                wizard.Close();
            };
            Application.Run(wizard); return;
        }
        if (args.Contains("--tools"))
        {
            using var tools = new ToolsForm(controller, new HistoryStore(null), QualityResult.FromSamples([23, 24, 22, 25]), _ => { }, _ => throw new InvalidOperationException("Preview only"), onlineUpdates: false);
            tools.Text = "Conduit - TOOLS PREVIEW (actions disabled)";
            void Disable(Control c) { if (c is Button && c.Parent?.Parent is not ToolPages) c.Enabled = false; foreach (Control child in c.Controls) Disable(child); }
            Disable(tools);
            if (args.Contains("--large-text")) UiPreferences.Apply(tools, 150);
            if (args.Contains("--small")) tools.Size = tools.MinimumSize;
            int snapshot = Array.IndexOf(args, "--snapshot");
            if (snapshot >= 0 && snapshot + 1 < args.Length)
            {
                tools.Opacity = 0; tools.ShowInTaskbar = false;
                tools.Shown += (_, _) => tools.BeginInvoke((Action)(() =>
                {
                    var issues = new List<string>();
                    if (args.Contains("--switch-tools"))
                    {
                        var pages = tools.Controls.OfType<ToolPages>().Single();
                        var entries = (List<(Panel Page, Button Button)>)typeof(ToolPages).GetField("_pages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pages)!;
                        bool moving = AnimationSettings.Allowed(pages);
                        entries[1].Button.PerformClick();
                        if (pages.SelectedTab != entries[1].Page) issues.Add("History navigation failed");
                        var body = (Panel)typeof(ToolPages).GetField("_body", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pages)!;
                        if (moving && !body.Controls.OfType<PageTransition>().Any()) issues.Add("Page transition was not created");
                        using (var image = new Bitmap(body.Width, body.Height)) body.DrawToBitmap(image, body.ClientRectangle);
                        AnimationSettings.SetEnabled(tools, false);
                        if (body.Controls.OfType<PageTransition>().Any()) issues.Add("Page transition survives reduced motion");
                        entries[0].Button.PerformClick();
                    }
                    int toolPage = Array.IndexOf(args, "--tool-page");
                    if (toolPage >= 0 && toolPage + 1 < args.Length)
                    {
                        var pages = tools.Controls.OfType<ToolPages>().Single();
                        var entries = (List<(Panel Page, Button Button)>)typeof(ToolPages).GetField("_pages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pages)!;
                        entries.Single(p => p.Button.Text == args[toolPage + 1]).Button.PerformClick();
                    }
                    void Check(Control c)
                    {
                        foreach (Control child in c.Controls)
                        {
                            if (!child.Visible) continue;
                            if (c is FlowLayoutPanel && (child.Right > c.ClientSize.Width || child.Bottom > c.ClientSize.Height)) issues.Add("Clipped button: " + child.Text);
                            Check(child);
                        }
                    }
                    Check(tools);
                    if (issues.Count > 0) Environment.ExitCode = 1;
                    File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "tools-layout-check.txt"), issues.Count == 0 ? ["PASS: visible tool buttons fit their containers"] : issues);
                    using var bitmap = new Bitmap(tools.Width, tools.Height);
                    tools.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(args[snapshot + 1], System.Drawing.Imaging.ImageFormat.Png);
                    tools.Close();
                }));
            }
            Application.Run(tools); return;
        }
        if (args.Contains("--compact"))
        {
            using var compact = new CompactForm(controller, () => "Preview: RX 1.6 MiB/s / TX 48 KiB/s", () => { }, persist: false);
            compact.Text = "Conduit - COMPACT PREVIEW"; Application.Run(compact); return;
        }
        using var form = new DashboardForm(controller, persistHistory: false) { Text = "Conduit - DESIGN PREVIEW (controls disabled; current tunnel unchanged)" };
        int capture = Array.IndexOf(args, "--snapshot");
        if (capture >= 0) { form.Opacity = 0; form.ShowInTaskbar = false; }
        if (args.Contains("--small")) form.ClientSize = new(1060, 680);
        if (args.Contains("--large-text")) UiPreferences.Apply(form, 150);
        if (args.Contains("--medium-text")) UiPreferences.Apply(form, 125);
        if (args.Contains("--physical-small")) { form.MinimumSize = new(900, 600); form.ClientSize = new(1060, 680); }
        form.FormClosing += (_, e) => e.Cancel = false;
        using var inspect = new System.Windows.Forms.Timer { Interval = 1500 };
        inspect.Tick += (_, _) =>
        {
            inspect.Stop();
            T Field<T>(string name) => (T)typeof(DashboardForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            Field<System.Windows.Forms.Timer>("_timer").Stop();
            // Snapshots capture the settled layout regardless of which window is active.
            AnimationSettings.SetEnabled(form, false);
            if (args.Contains("--demo") || args.Contains("--fault"))
            {
                typeof(TunnelController).GetProperty("State")!.SetValue(controller, TunnelState.Connected);
                typeof(TunnelController).GetField("_desired", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, true);
                bool healthy = !args.Contains("--fault");
                var health = new TunnelHealth(new(true, "Full-device adapter is up"), new(true, "Exit matches your VM", 114),
                    new(healthy, healthy ? "DNS answered through the tunnel" : "DNS request timed out", healthy ? 24 : null),
                    new(healthy, healthy ? "STUN reply from expected exit" : "No UDP response", healthy ? 38 : null), "203.0.113.7", DateTimeOffset.Now,
                    new(new(true, "Sampled IPv4 routes use Conduit"), new(true, "Sampled IPv6 routes use Conduit"), new(true, "Ordinary app exit matched")));
                typeof(TunnelController).GetProperty("Health")!.SetValue(controller, health);
                typeof(TunnelController).GetProperty("Verified")!.SetValue(controller, healthy);
                controller.KillSwitch = true;
                typeof(DashboardForm).GetMethod("ApplyHealth", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [health]);
                Field<Label>("_status").Text = "Design preview - illustrative data only. Your current tunnel is unchanged.";
                Field<Label>("_profile").Text = "PERSONAL SERVER / VLESS · REALITY / PREVIEW";
                Field<DashboardMetric>("_network").UpdateValue("Wi-Fi", "Illustrative local network");
                if (healthy)
                {
                    var data = new ConnectionSnapshot("203.0.113.7", [new("198.51.100.1", "Illustrative preview", "tcp", 24800, 8000000, true), new("198.51.100.2", "Illustrative preview", "udp", 14000, 48000, true)], DateTimeOffset.Now);
                    Field<ConnectionInspector>("_inspector").UpdateData(data);
                    var list = Field<ListView>("_destinations");
                    foreach (var d in data.Destinations) { var item = new ListViewItem(d.Address) { Tag = d.Address }; item.SubItems.Add(d.Network.ToUpperInvariant()); item.SubItems.Add(TrafficMeter.Bytes(d.Uploaded + d.Downloaded)); list.Items.Add(item); }
                }
                if (args.Contains("--detail")) { form.Navigate("Connections"); Field<ListView>("_destinations").Items[0].Selected = true; }
                Field<DashboardMetric>("_speed").UpdateValue("RX 1.6 MiB/s / TX 48 KiB/s", "Illustrative throughput samples");
                Field<DashboardMetric>("_cores").UpdateValue("Xray / sing-box", "Preview - process IDs are not simulated");
                Field<DashboardMetric>("_adapter").UpdateValue("worktunnel", "Configured MTU 1400 / gVisor");
                var traffic = Field<DashboardChart>("_trafficChart"); var latency = Field<DashboardChart>("_latencyChart");
                for (int i = 0; i < 300; i++) traffic.Add(i % 71 == 0 ? null : 1600000 + Math.Sin(i * .15) * 800000 + (i % 43 == 0 ? 6000000 : 0), 45000 + Math.Cos(i * .2) * 30000);
                for (int i = 0; i < 120; i++) latency.Add(i % 33 == 0 ? null : 114 + Math.Sin(i * .2) * 40 + (i % 28 == 0 ? 230 : 0));
            }
            int pageArgument = Array.IndexOf(args, "--page");
            if (pageArgument >= 0 && pageArgument + 1 < args.Length) form.Navigate(args[pageArgument + 1]);
            if (args.Contains("--collapsed")) Field<WorkspaceNavigation>("_navigation").ToggleCollapsed();
            void Settle(Control control) { foreach (Control child in control.Controls) Settle(child); control.PerformLayout(); }
            Settle(form);
            var issues = new List<string>();
            if (args.Contains("--frame-checks")) CheckFrame(form, issues);
            if (args.Contains("--workspace-checks"))
            {
                AnimationSettings.SetEnabled(form, false);
                form.Navigate("Connections");
                var list = Field<ListView>("_destinations"); list.Items[0].Selected = true;
                if (Field<ConnectionInspector>("_inspector").SelectedAddress != "198.51.100.1") issues.Add("Destination row does not select the local connection inspector");
                form.Navigate("Overview");
                if (Field<ConnectionInspector>("_inspector").Visible) issues.Add("Connection inspector obscures the simple Overview");
                form.Navigate("Connections");
                if (Field<ConnectionInspector>("_inspector").SelectedAddress != "198.51.100.1" || !Field<ConnectionInspector>("_inspector").Visible) issues.Add("Returning to Connections loses the local selection");
                form.Navigate("Diagnostics");
                if (Field<ConnectionInspector>("_inspector").Visible) issues.Add("Inspector obscures Diagnostics");
                IEnumerable<Control> Descendants(Control root) { foreach (Control child in root.Controls) { yield return child; foreach (var next in Descendants(child)) yield return next; } }
                var expand = Descendants(form).OfType<Button>().Single(b => b.Text.StartsWith("Show engine and adapter")); expand.PerformClick();
                if (!Field<DashboardMetric>("_cores").Visible) issues.Add("Engine details did not expand");
                expand.PerformClick();
                form.Navigate("Settings");
                if (!Field<CheckBox>("_auto").Visible || !Field<ToolsForm>("_settingsTools").Visible) issues.Add("Settings page is not accessible");
                var navigation = Field<WorkspaceNavigation>("_navigation"); int expanded = navigation.PreferredWidth; navigation.ToggleCollapsed();
                if (navigation.PreferredWidth >= expanded || navigation.SelectedPage != "Settings") issues.Add("Collapsed sidebar loses its selection or width");
                navigation.ToggleCollapsed(); form.Navigate("Overview");
                Settle(form);
            }
            if (args.Contains("--debug-layout"))
            {
                var lines = new List<string>();
                for (Control? c = Field<DashboardMetric>("_exit"); c != null; c = c.Parent) lines.Add(c.GetType().Name + " " + c.Bounds + " client " + c.ClientSize + (c is ScrollableControl scroll ? " scroll " + scroll.AutoScrollPosition : ""));
                File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "layout-debug.txt"), lines);
            }
            void Walk(Control control)
            {
                if (control is Button) control.Enabled = false;
                foreach (Control child in control.Controls)
                {
                    if (!child.Visible) continue;
                    if (control is TableLayoutPanel or FlowLayoutPanel && (child.Top < 0 || child.Left < 0 || child.Right > control.ClientSize.Width + 1 || child.Bottom > control.ClientSize.Height + 1))
                        issues.Add(child.GetType().Name + " clips in " + control.GetType().Name + ": " + child.Text);
                    Walk(child);
                }
            }
            Walk(form);
            if (args.Contains("--interaction-checks"))
            {
                form.Navigate("Connections");
                var list = Field<ListView>("_destinations"); list.Items[0].Selected = true;
                if (Field<ConnectionInspector>("_inspector").SelectedAddress != "198.51.100.1") issues.Add("Local destination selection failed");
                typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [new KeyEventArgs(Keys.Escape)]);
                if (Field<ConnectionInspector>("_inspector").SelectedAddress != null) issues.Add("Escape did not close local destination details");
                AnimationSettings.SetLoad(form, VisualLoad.LowPower, persist: false);
                if (AnimationSettings.Allowed(form)) issues.Add("Low power must stop decorative motion");
                form.Navigate("Overview");
                var chart = Field<DashboardChart>("_trafficChart");
                typeof(DashboardChart).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(chart, [new KeyEventArgs(Keys.Left)]);
                if (!chart.AccessibleDescription!.Contains("samples ago")) issues.Add("Keyboard cannot inspect chart history");
            }
            foreach (var metric in new[] { "_dns", "_udp", "_exit", "_latency", "_speed", "_network", "_adapter", "_cores" })
            {
                if (!Field<DashboardMetric>(metric).Visible) continue;
                foreach (Label label in Field<DashboardMetric>(metric).Controls[0].Controls)
                {
                    int required = TextRenderer.MeasureText("Ag", label.Font, Size.Empty, TextFormatFlags.SingleLine).Height;
                    if (label.ClientSize.Height < required)
                        issues.Add($"{metric}: {label.Text} needs {required}px text height, has {label.ClientSize.Height}px");
                }
            }
            if (issues.Count > 0) Environment.ExitCode = 1;
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "layout-check.txt"), issues.Count == 0 ? ["PASS: all table children fit their containers"] : issues);
            if (capture >= 0 && capture + 1 < args.Length)
            {
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(args[capture + 1], System.Drawing.Imaging.ImageFormat.Png);
                form.Shutdown();
            }
        };
        form.Shown += (_, _) => inspect.Start();
        // No connection or administrative actions are allowed in this preview.
        Application.Run(form);
    }
}
