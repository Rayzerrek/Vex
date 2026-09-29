using System.Runtime.InteropServices;
using Vex.App.Terminal.Native;
using Xunit;

namespace Vex.App.Tests;

public class TerminalClipboardTests
{
    [Fact]
    public void ClipboardOperations_WhenAnotherThreadHoldsClipboard_ReturnFalse()
    {
        using var opened = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var locked = false;
        var holder = new Thread(() =>
        {
            try
            {
                for (var attempt = 0; attempt < 20 && !locked; attempt++)
                {
                    locked = OpenClipboard(IntPtr.Zero);
                    if (!locked)
                        Thread.Sleep(25);
                }
                opened.Set();
                if (locked)
                    release.Wait();
            }
            finally
            {
                if (locked)
                    CloseClipboard();
            }
        });
        holder.SetApartmentState(ApartmentState.STA);
        holder.Start();

        try
        {
            Assert.True(opened.Wait(TimeSpan.FromSeconds(5)), "Clipboard holder did not start");
            Assert.True(locked, "Could not lock the clipboard for the test");

            bool? read = null;
            bool? write = null;
            Exception? failure = null;
            var consumer = new Thread(() =>
            {
                try
                {
                    read = NativeTerminalControl.TryGetClipboardText(out _);
                    write = NativeTerminalControl.TrySetClipboardText("test");
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            consumer.SetApartmentState(ApartmentState.STA);
            consumer.Start();
            Assert.True(consumer.Join(TimeSpan.FromSeconds(10)), "Clipboard consumer did not finish");
            Assert.Null(failure);
            Assert.False(read);
            Assert.False(write);
        }
        finally
        {
            release.Set();
            Assert.True(holder.Join(TimeSpan.FromSeconds(5)), "Clipboard holder did not finish");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
}
