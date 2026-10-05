namespace WorkTunnel;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--release-smoke-test")
        { Environment.ExitCode = ReleaseSmokeChecks.Run(args[1]); return; }
        if (args.Length == 2 && args[0] == "--release-resource-test")
        { Environment.ExitCode = ReleaseSmokeChecks.Measure(args[1]); return; }
        if (args.Length == 1 && args[0] == "--restore-network")
        {
            try
            {
                InstallSecurity.RequireInstalled();
                if (!InstallSecurity.IsAdministrator) throw new UnauthorizedAccessException("Run this recovery command as administrator.");
                NetworkLock.Remove();
                ShowMessage("Conduit's network lock has been removed. Disconnect any running tunnel to restore its routes.", "Network recovery");
            }
            catch (Exception e) { ShowMessage(e.Message, "Network recovery"); }
            return;
        }
        if (args.Length == 1 && args[0] == "--tunnel-host")
        {
            try { TunnelBroker.RunAsync().GetAwaiter().GetResult(); }
            catch (Exception e) { ShowMessage(e.Message, "Conduit host"); }
            return;
        }
        if (args.Length == 3 && args[0] == "--prepare-update")
        { UpdateInstaller.PrepareWorkerAsync(args[1], args[2]).GetAwaiter().GetResult(); return; }
        if (args.Length == 3 && args[0] == "--apply-update")
        { UpdateInstaller.RunWorkerAsync(args[1], args[2]).GetAwaiter().GetResult(); return; }
        RunDashboard(args);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ShowMessage(string text, string title) => MessageBox.Show(text, title);

    // Keep UI startup out of the host entry point's JIT compilation.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunDashboard(string[] args)
    {
        // Single instance — the tray app should never run twice (double cores).
        using var mutex = new Mutex(true, "WorkTunnel_SingleInstance_2f7a", out bool created);
        if (!created)
        {
            try { using var show = EventWaitHandle.OpenExisting("Local\\WorkTunnel_Show_2f7a"); show.Set(); } catch { }
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\WorkTunnel_Show_2f7a");
        using var context = new TrayContext(args.Contains("--connect"));
        var registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => context.RequestDashboard(), null, Timeout.Infinite, false);
        try { Application.Run(context); }
        finally { registration.Unregister(null); }

        GC.KeepAlive(mutex);
    }
}
