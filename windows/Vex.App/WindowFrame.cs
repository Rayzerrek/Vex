using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace Vex.App;

/// <summary>Lets Windows 11 render smooth window corners and the native frame shadow.</summary>
internal static class WindowFrame
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmRoundCorners = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Applies rounded corners and a frame that follows the app appearance; older Windows keeps its existing chrome.</summary>
    public static bool ApplyRoundedFrame(Window window, bool isDark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return false;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return false;

        if (WindowChrome.GetWindowChrome(window) is { } chrome)
        {
            // Without a glass edge WPF installs a window region, which prevents
            // DWM from antialiasing the corners even with an explicit preference.
            chrome.GlassFrameThickness = new Thickness(1);
            chrome.CornerRadius = new CornerRadius(0);
        }

        var darkMode = isDark ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref darkMode, sizeof(int));
        var preference = DwmRoundCorners;
        return DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref preference, sizeof(int)) >= 0;
    }
}
