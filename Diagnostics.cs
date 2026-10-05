using System.Diagnostics;

namespace WorkTunnel;

/// <summary>Runs the bundled tether-diag.ps1 in a live console window.</summary>
internal static class Diagnostics
{
    public static void Run()
    {
        if (!File.Exists(AppPaths.DiagScript))
        {
            MessageBox.Show("Diagnostic script not found in this install.", "Conduit",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoExit -ExecutionPolicy Bypass -File \"{AppPaths.DiagScript}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Conduit", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
