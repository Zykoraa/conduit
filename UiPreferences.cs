using System.Runtime.CompilerServices;
namespace WorkTunnel;

internal static class UiPreferences
{
    private sealed class ScaleState { public float Factor = 1; }
    private static readonly ConditionalWeakTable<Form, ScaleState> Scales = new();
    private static string PathName => Path.Combine(AppPaths.DataDir, "text-size.txt");
    public static int Percent
    {
        get { try { return int.TryParse(File.ReadAllText(PathName), out int value) && value is 100 or 125 or 150 ? value : 100; } catch { return 100; } }
        set { if (value is not (100 or 125 or 150)) throw new ArgumentOutOfRangeException(nameof(value)); Directory.CreateDirectory(AppPaths.DataDir); File.WriteAllText(PathName, value.ToString()); }
    }
    public static void Apply(Form form, int? percent = null)
    {
        var state = Scales.GetOrCreateValue(form);
        float target = (percent ?? Percent) / 100f, ratio = target / state.Factor;
        if (Math.Abs(ratio - 1) < .01) return;
        form.SuspendLayout();
        void Fonts(Control control)
        {
            // Capture descendants first: inherited Font changes otherwise compound the scale.
            foreach (Control child in control.Controls) Fonts(child);
            if (control is ListView list) foreach (ColumnHeader column in list.Columns) column.Width = (int)Math.Round(column.Width * ratio);
            control.Font = new Font(control.Font.FontFamily, control.Font.Size * ratio, control.Font.Style);
        }
        Fonts(form); form.Scale(new SizeF(ratio, ratio)); state.Factor = target;
        form.ResumeLayout(true);
        var area = Screen.FromControl(form).WorkingArea;
        form.MinimumSize = new(Math.Min(form.MinimumSize.Width, area.Width - 24), Math.Min(form.MinimumSize.Height, area.Height - 24));
        form.Size = new(Math.Min(form.Width, area.Width - 24), Math.Min(form.Height, area.Height - 24));
    }
}
