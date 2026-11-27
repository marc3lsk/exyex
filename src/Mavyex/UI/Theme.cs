using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Mavyex.Core;


namespace Mavyex.UI;

/// <summary>Light / dark palette (same colours as mavyex) as application-wide DynamicResources; "System" follows the
/// Windows app theme. Also switches the native title bar to dark.</summary>
internal static class Theme
{
    public static bool IsDark { get; private set; }
    private static ThemeChoice current;
    private static bool listening;

    private static Color Rgb(byte r, byte g, byte b, byte a = 255) => Color.FromArgb(a, r, g, b);
    private static Color Gray(byte v) => Rgb(v, v, v);
    public static readonly Color Accent = Rgb(76, 139, 245);

    public static void Apply(ThemeChoice choice)
    {
        current = choice;
        if (!listening)
        {
            SystemEvents.UserPreferenceChanged += (_, _) =>
            {
                if (current == ThemeChoice.System) Application.Current?.Dispatcher.BeginInvoke(() => Apply(ThemeChoice.System));
            };
            listening = true;
        }
        IsDark = choice == ThemeChoice.Dark || (choice == ThemeChoice.System && SystemIsDark());
        bool d = IsDark;
        var r = Application.Current.Resources;
        void Set(string key, Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            r[key] = b;
        }
        Set("PanelBg", d ? Rgb(28, 29, 33) : Rgb(246, 246, 248));
        Set("InputBg", d ? Rgb(20, 21, 24) : Colors.White);
        Set("MenuBg", d ? Rgb(36, 37, 42) : Colors.White);
        Set("Fg", d ? Gray(222) : Gray(20));
        Set("WeakFg", d ? Gray(140) : Gray(110));
        Set("Line", d ? Gray(52) : Gray(210));
        Set("Accent", Accent);
        Set("AccentFill", d ? Rgb(57, 104, 184) : Rgb(66, 125, 230));
        Set("ButtonBg", d ? Gray(46) : Gray(228));
        Set("ButtonPressed", d ? Gray(80) : Gray(196));
        Set("Overlay", d ? Rgb(255, 255, 255, 24) : Rgb(0, 0, 0, 20));
        Set("RowBg", d ? Rgb(255, 255, 255, 7) : Rgb(0, 0, 0, 7));
        Set("RowHover", d ? Rgb(255, 255, 255, 16) : Rgb(0, 0, 0, 14));
        Set("RowSel", d ? Rgb(76, 139, 245, 82) : Rgb(76, 139, 245, 56));
        Set("RowSelBorder", Rgb(76, 139, 245, 204));
        Set("HlFg", d ? Rgb(255, 196, 92) : Rgb(176, 84, 0));
        Set("HlBg", d ? Rgb(255, 196, 92, 28) : Rgb(255, 160, 40, 45));
        Set("ErrorFg", d ? Rgb(255, 110, 110) : Rgb(200, 30, 30));
        foreach (Window w in Application.Current.Windows) TitleBar(w);
    }

    /// <summary>Dark / light native title bar for a window (call after its handle exists).</summary>
    public static void TitleBar(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        int v = IsDark ? 1 : 0;
        DwmSetWindowAttribute(h, 20, ref v, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static bool SystemIsDark()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }
}
