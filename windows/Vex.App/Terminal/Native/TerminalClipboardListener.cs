using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Vex.App.Terminal.Native;

/// <summary>Receives Windows clipboard changes on the pane's UI thread; never polls or reads clipboard text.</summary>
internal sealed class TerminalClipboardListener : IDisposable
{
    private readonly HwndSource _source;
    private readonly Action<uint, uint> _changed;

    internal static uint SequenceNumber => GetClipboardSequenceNumber();

    internal TerminalClipboardListener(Action<uint, uint> changed)
    {
        _changed = changed;
        _source = new HwndSource(new HwndSourceParameters("Vex clipboard listener")
        {
            ParentWindow = new IntPtr(-3), // Message-only window, independent of tab reparenting.
            WindowStyle = 0,
        });
        _source.AddHook(OnClipboardMessage);
        AddClipboardFormatListener(_source.Handle);
    }

    private IntPtr OnClipboardMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x031D)
        {
            GetWindowThreadProcessId(GetClipboardOwner(), out var ownerProcessId);
            _changed(SequenceNumber, ownerProcessId);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(OnClipboardMessage);
        _source.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
