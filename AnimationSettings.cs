using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace WorkTunnel;

internal interface IAnimatedControl { void RefreshMotion(); }

internal static class AnimationSettings
{
    private sealed class Preference { public bool Enabled = true; public VisualLoad Load = VisualPerformance.Read(VisualPerformance.PathName); }
    private static readonly ConditionalWeakTable<Form, Preference> Preferences = new();
    public static VisualLoad Load(Form form) => Preferences.GetOrCreateValue(form).Load;
    public static bool LowPower(Control control) => SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline ||
        (control.FindForm() is { } form && (Load(form) == VisualLoad.LowPower || form.Owner != null && LowPower(form.Owner)));
    public static void SetLoad(Form form, VisualLoad load, bool persist = true)
    {
        if (persist) VisualPerformance.Write(VisualPerformance.PathName, load);
        Preferences.GetOrCreateValue(form).Load = load;
        SetEnabled(form, Preferences.GetOrCreateValue(form).Enabled);
    }
    public static void SetEnabled(Form form, bool enabled)
    {
        Preferences.GetOrCreateValue(form).Enabled = enabled;
        void Visit(Control control)
        {
            if (control is IAnimatedControl animated) animated.RefreshMotion();
            foreach (Control child in control.Controls.Cast<Control>().ToArray()) Visit(child);
        }
        Visit(form);
    }
    public static Color Blend(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb((int)(from.R + (to.R - from.R) * amount), (int)(from.G + (to.G - from.G) * amount), (int)(from.B + (to.B - from.B) * amount));
    }
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParameter(uint action, uint parameter, out int value, uint flags);
    private static bool Enabled(Form form) => Preferences.GetOrCreateValue(form).Enabled && (form.Owner == null || Enabled(form.Owner));
    public static bool Allowed(Control control) => control.Visible && (control.TopLevelControl as Form ?? control.FindForm()) is { WindowState: not FormWindowState.Minimized } form &&
        Form.ActiveForm == form && Enabled(form) && !LowPower(form) && !SystemInformation.HighContrast && SystemParameter(0x1042, 0, out int enabled, 0) && enabled != 0;
}
