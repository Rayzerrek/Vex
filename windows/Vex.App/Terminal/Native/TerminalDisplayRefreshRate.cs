using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Vex.App.Terminal.Native;

/// <summary>Reads the terminal window's display refresh rate without retaining a native display context.</summary>
internal static class TerminalDisplayRefreshRate
{
    internal const int DefaultRefreshRate = 60;

    /// <summary>Returns display Hz, or 60 Hz when the visual is detached or the display does not report a usable rate.</summary>
    internal static int ReadRefreshRate(Visual visual)
    {
        if (PresentationSource.FromVisual(visual) is not HwndSource source)
            return DefaultRefreshRate;
        var monitor = MonitorFromWindow(source.Handle, 2);
        if (monitor == IntPtr.Zero)
            return DefaultRefreshRate;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), DeviceName = "" };
        if (!GetMonitorInfo(monitor, ref info))
            return DefaultRefreshRate;
        var context = CreateDC("DISPLAY", info.DeviceName, null, IntPtr.Zero);
        if (context == IntPtr.Zero)
            return DefaultRefreshRate;
        try
        {
            const int verticalRefresh = 116;
            var rate = GetDeviceCaps(context, verticalRefresh);
            return rate is >= 30 and <= 1000 ? rate : DefaultRefreshRate;
        }
        finally
        {
            DeleteDC(context);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr mode);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr context, int index);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr context);
}
