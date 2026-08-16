using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Vex.App;

/// <summary>
/// Enables the DWM blur-behind backdrop via the undocumented but stable
/// (since Windows 10 1803) SetWindowCompositionAttribute path, so the desktop
/// shows through the chrome and terminal surface. Classic blur-behind is the
/// primary on Windows 10 (acrylic there lags dragging and sometimes renders
/// opaque); acrylic is attempted first on Windows 11 where DWM still honors
/// it. Callers must fall back to an opaque background when
/// <see cref="EnableAcrylic"/> returns false: a transparent window without a
/// backdrop renders black.
/// </summary>
internal static class WindowBackdrop
{
    private enum AccentState
    {
        Disabled = 0,
        EnableBlurBehind = 3,
        EnableAcrylicBlurBehind = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor; // packed ABGR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute; // WCA_ACCENT_POLICY = 19
        public IntPtr Data;
        public int SizeOfData;
    }

    private const int WcaAccentPolicy = 19;

    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;

    private static readonly ConditionalWeakTable<Window, object> HookedWindows = new();

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>
    /// Turns on the DWM backdrop with a color tint. Returns false when neither
    /// acrylic nor blur-behind is available. Idempotent: repeat calls re-apply
    /// the accent (some Windows 10 builds need it re-applied after the first
    /// frame) without wiring a second move/resize hook.
    /// </summary>
    public static bool EnableAcrylic(Window window, Color tint, byte alpha)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return false;

        // Acrylic is the crisp vibrancy look, but on Windows 10 it lags window
        // dragging and can render fully opaque; classic blur-behind is the
        // reliable choice there and the fallback everywhere else.
        var acrylicActive = !IsWindows10() &&
            TrySetAccent(handle, AccentState.EnableAcrylicBlurBehind, tint, alpha, accentFlags: 2);

        if (!acrylicActive && !TrySetAccent(handle, AccentState.EnableBlurBehind, tint, alpha, accentFlags: 0))
            return false;

        InstallMoveToggle(window, tint, alpha, acrylicActive);
        return true;
    }

    private static bool IsWindows10()
    {
        // Windows 11 is build 22000+; anything else in the 10.x range is 10.
        var version = Environment.OSVersion.Version;
        return version.Major == 10 && version.Build < 22000;
    }

    private static void InstallMoveToggle(Window window, Color tint, byte alpha, bool acrylicActive)
    {
        if (HookedWindows.TryGetValue(window, out _))
            return;
        HookedWindows.Add(window, new object());

        var handle = new WindowInteropHelper(window).Handle;
        HwndSource.FromHwnd(handle)?.AddHook((h, msg, _, _, ref handled) =>
        {
            // Acrylic re-rasterizes the backdrop on every WM_MOVE, lagging the
            // drag; drop to blur-behind for the modal move/resize loop, then
            // restore acrylic once it ends. Blur-behind has no such cost, so
            // the toggle is a no-op there.
            switch (msg)
            {
                case WmEnterSizeMove:
                    TrySetAccent(h, AccentState.EnableBlurBehind, tint, alpha, accentFlags: 0);
                    break;
                case WmExitSizeMove when acrylicActive:
                    TrySetAccent(h, AccentState.EnableAcrylicBlurBehind, tint, alpha, accentFlags: 2);
                    break;
            }
            return IntPtr.Zero;
        });
    }

    private static unsafe bool TrySetAccent(IntPtr handle, AccentState state, Color tint, byte alpha, int accentFlags)
    {
        var policy = new AccentPolicy
        {
            AccentState = state,
            // AccentFlags=2 blends the tint as a gradient overlay rather than
            // a flat wash, which reads closer to macOS vibrancy; blur-behind
            // wants a flat wash, hence the caller-supplied flag.
            AccentFlags = accentFlags,
            GradientColor = (alpha << 24) | (tint.B << 16) | (tint.G << 8) | tint.R,
        };

        var data = new WindowCompositionAttributeData
        {
            Attribute = WcaAccentPolicy,
            Data = (IntPtr)(&policy),
            SizeOfData = sizeof(AccentPolicy),
        };
        return SetWindowCompositionAttribute(handle, ref data) != 0;
    }
}
