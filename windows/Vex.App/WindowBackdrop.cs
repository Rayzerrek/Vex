using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Vex.App;

/// <summary>
/// Enables the DWM acrylic blur-behind backdrop via the undocumented but
/// stable (Windows 10 1803+) SetWindowCompositionAttribute path, so the
/// desktop shows through the chrome and terminal surface — the vibrancy
/// look from the reference video. Callers must fall back to an opaque
/// background when <see cref="EnableAcrylic"/> returns false: a transparent
/// window without a backdrop renders black.
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

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>
    /// Turns on acrylic blur with a color tint over the blurred desktop.
    /// Returns false when the call is unavailable or rejected.
    /// </summary>
    public static bool EnableAcrylic(Window window, Color tint, byte alpha)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return false;

        var policy = new AccentPolicy
        {
            AccentState = AccentState.EnableAcrylicBlurBehind,
            // AccentFlags=2 blends the tint as a gradient overlay rather than
            // a flat wash, which reads closer to macOS vibrancy.
            AccentFlags = 2,
            GradientColor = (alpha << 24) | (tint.B << 16) | (tint.G << 8) | tint.R,
        };

        var size = Marshal.SizeOf(policy);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = ptr,
                SizeOfData = size,
            };
            return SetWindowCompositionAttribute(handle, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
