using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Vex.App;

/// <summary>
/// Enables the modern DWM acrylic backdrop on Windows 11 through the
/// undocumented but long-lived SetWindowCompositionAttribute path. Legacy
/// blur-behind is deliberately disabled on Windows 10: its older composition
/// path can retain stale WPF glyph tiles after Alt+Tab. Callers must fall back
/// to an opaque background when this returns false; a transparent window
/// without a working backdrop renders black. <see cref="Disable"/> turns the
/// accent off again when switching to an opaque appearance.
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

    private const uint RdwInvalidate = 0x0001;
    private const uint RdwErase = 0x0004;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwFrame = 0x0400;

    /// <summary>Per-window live accent parameters. The move/resize toggle hook
    /// reads these on every event; capturing them in the closure instead kept
    /// the FIRST tint forever, so a later theme switch dragged the wrong
    /// color in and out of the move loop.</summary>
    private sealed class AccentParameters
    {
        public Color Tint;
        public byte Alpha;
        public bool AcrylicActive;
        public bool BackdropActive;
    }

    private static readonly ConditionalWeakTable<Window, AccentParameters> HookedWindows = new();

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

    /// <summary>
    /// Turns on the DWM backdrop with a color tint. Returns false on Windows
    /// 10 or when neither acrylic nor blur-behind is available. Idempotent:
    /// repeat calls re-apply the accent with the new tint (DWM sometimes skips
    /// recomposition on a pure tint change, so the window is invalidated).
    /// </summary>
    public static bool EnableAcrylic(Window window, Color tint, byte alpha)
    {
        // Windows 11 still reports major version 10. Build 22000 is the first
        // Windows 11 build; earlier Windows 10 releases use the unstable legacy
        // blur composition that produced stale WPF glyph tiles.
        if (!SupportsModernAcrylic(Environment.OSVersion.Version))
            return false;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return false;

        var parameters = GetOrCreateParameters(window);
        parameters.Tint = tint;
        parameters.Alpha = alpha;
        // A Disable -> Enable cycle (appearance flip) does NOT rebuild the
        // DWM composition surface: the accent policy is accepted, but the
        // backdrop keeps compositing the state from before the disable until
        // a real move/resize. Track the transition and force that rebuild.
        var needsFlush = !parameters.BackdropActive;
        parameters.BackdropActive = true;

        var acrylicActive = TrySetAccent(
            handle, AccentState.EnableAcrylicBlurBehind, tint, alpha, accentFlags: 2);

        if (!acrylicActive && !TrySetAccent(handle, AccentState.EnableBlurBehind, tint, alpha, accentFlags: 0))
        {
            parameters.BackdropActive = false;
            return false;
        }

        parameters.AcrylicActive = acrylicActive;
        InstallMoveToggle(window);

        if (needsFlush)
        {
            // A Disable -> Enable cycle leaves the DWM composition surface
            // stale even though the new accent policy is accepted; only a
            // real move/resize fixed it. A drag works because the move hook
            // drops to blur-behind (which DWM always recomposes) and then
            // restores acrylic — reproduce exactly that sequence here.
            TrySetAccent(handle, AccentState.EnableBlurBehind, tint, alpha, accentFlags: 0);
            window.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                () => TrySetAccent(
                    handle,
                    acrylicActive ? AccentState.EnableAcrylicBlurBehind : AccentState.EnableBlurBehind,
                    tint, alpha,
                    accentFlags: acrylicActive ? 2 : 0));
        }
        else
        {
            // A repeated call with a changed tint does not always trigger DWM
            // recomposition — the new color only appeared after a move/resize.
            // Force a full repaint so the switch is immediate.
            RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwErase | RdwFrame | RdwAllChildren);
        }
        return true;
    }

    /// <summary>Returns whether the running Windows version has the modern
    /// acrylic composition path. Windows 11 keeps major version 10, so the
    /// build number is the compatibility boundary.</summary>
    internal static bool SupportsModernAcrylic(Version version)
        => version.Major > 10 || (version.Major == 10 && version.Build >= 22000);

    /// <summary>Turns the DWM accent off; used when an appearance flip moves
    /// the window onto fully opaque, self-painted chrome.</summary>
    public static void Disable(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        TrySetAccent(handle, AccentState.Disabled, default, 0, accentFlags: 0);
        if (HookedWindows.TryGetValue(window, out var parameters))
            parameters.BackdropActive = false;
    }

    private static AccentParameters GetOrCreateParameters(Window window)
    {
        if (!HookedWindows.TryGetValue(window, out var parameters))
        {
            parameters = new AccentParameters();
            HookedWindows.Add(window, parameters);
        }
        return parameters;
    }

    private static void InstallMoveToggle(Window window)
    {
        if (HookedWindows.TryGetValue(window, out _))
            return;

        var handle = new WindowInteropHelper(window).Handle;
        HwndSource.FromHwnd(handle)?.AddHook((h, msg, _, _, ref handled) =>
        {
            // Acrylic re-rasterizes the backdrop on every WM_MOVE, lagging the
            // drag; drop to blur-behind for the modal move/resize loop, then
            // restore acrylic once it ends. Blur-behind has no such cost, so
            // the toggle is a no-op there. Parameters are read live so a
            // theme change between drags takes effect immediately.
            if (!HookedWindows.TryGetValue(window, out var parameters))
                return IntPtr.Zero;

            switch (msg)
            {
                case WmEnterSizeMove:
                    TrySetAccent(h, AccentState.EnableBlurBehind, parameters.Tint, parameters.Alpha, accentFlags: 0);
                    break;
                case WmExitSizeMove when parameters.AcrylicActive:
                    TrySetAccent(h, AccentState.EnableAcrylicBlurBehind, parameters.Tint, parameters.Alpha, accentFlags: 2);
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
