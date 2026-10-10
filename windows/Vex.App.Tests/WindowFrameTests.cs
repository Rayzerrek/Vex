using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using Xunit;

namespace Vex.App.Tests;

public sealed class WindowFrameTests
{
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr window, IntPtr region);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundedFrame_DoesNotLeaveAWindowRegionThatDisablesDwmAntialiasing(bool isDark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var window = new Window
            {
                Width = 400,
                Height = 300,
                Left = -10000,
                Top = -10000,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            try
            {
                WindowChrome.SetWindowChrome(window, new WindowChrome
                {
                    CaptionHeight = 44,
                    CornerRadius = new CornerRadius(12),
                    GlassFrameThickness = new Thickness(0),
                    ResizeBorderThickness = new Thickness(6),
                    UseAeroCaptionButtons = false,
                });
                window.SourceInitialized += (_, _) => Assert.True(WindowFrame.ApplyRoundedFrame(window, isDark));
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var handle = new WindowInteropHelper(window).Handle;
                var region = CreateRectRgn(0, 0, 0, 0);
                try
                {
                    Assert.Equal(0, GetWindowRgn(handle, region));
                }
                finally
                {
                    DeleteObject(region);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                window.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(10000), "Window frame test did not finish.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
