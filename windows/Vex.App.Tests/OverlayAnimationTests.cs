using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Vex.App;
using Xunit;

namespace Vex.App.Tests;

public sealed class OverlayAnimationTests
{
    [Fact]
    public void CloseDuringOpen_ContinuesFromTheVisibleFrame()
    {
        RunOnSta(() =>
        {
            using var overlay = new TestOverlay();
            overlay.Show();
            PumpDispatcher(45);
            var opacity = overlay.Panel.Opacity;
            Assert.InRange(opacity, 0.01, 0.95);

            overlay.Hide();
            PumpDispatcher(20);

            Assert.True(overlay.Panel.Opacity <= opacity + 0.02,
                "Closing a partially open panel jumped toward full opacity.");
            PumpDispatcher(200);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.Equal(1, overlay.CloseCount);
        });
    }

    [Fact]
    public void ReopenDuringClose_PreservesTheFrameAndCancelsDismissal()
    {
        RunOnSta(() =>
        {
            using var overlay = new TestOverlay();
            overlay.Show();
            PumpDispatcher(250);
            overlay.Hide();
            PumpDispatcher(30);
            var opacity = overlay.Panel.Opacity;
            Assert.InRange(opacity, 0.01, 0.99);

            overlay.Show();
            PumpDispatcher(20);

            Assert.True(overlay.Panel.Opacity >= opacity - 0.02,
                "Reopening a closing panel jumped toward zero opacity.");
            PumpDispatcher(250);
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.Equal(1, overlay.Panel.Opacity);
            Assert.Equal(0, overlay.CloseCount);
        });
    }

    [Fact]
    public void RepeatedClose_DoesNotRestartTheFade()
    {
        RunOnSta(() =>
        {
            using var overlay = new TestOverlay();
            overlay.Show();
            PumpDispatcher(250);
            overlay.Hide();
            PumpDispatcher(30);
            var opacity = overlay.Panel.Opacity;

            overlay.Hide();
            PumpDispatcher(20);

            Assert.True(overlay.Panel.Opacity <= opacity + 0.02,
                "Repeated dismissal restarted the closing animation.");
            PumpDispatcher(200);
            Assert.Equal(1, overlay.CloseCount);
        });
    }

    private static void PumpDispatcher(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(10000), "Overlay animation test did not finish.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class TestOverlay : OverlayControl, IDisposable
    {
        private readonly Border _backdrop = new() { Opacity = 0 };
        private readonly ScaleTransform _scale = new(0.96, 0.96);
        private readonly TranslateTransform _translate = new(0, 8);
        private readonly Window _host;

        public Border Panel { get; } = new() { Opacity = 0 };
        public int CloseCount { get; private set; }

        public TestOverlay()
        {
            var grid = new Grid();
            grid.Children.Add(_backdrop);
            grid.Children.Add(Panel);
            Content = grid;
            _host = new Window
            {
                Content = this,
                Width = 400,
                Height = 300,
                Left = -10000,
                Top = -10000,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            _host.Show();
            PumpDispatcher(100);
        }

        public void Show()
        {
            Visibility = Visibility.Visible;
            AnimateOverlayOpen(_backdrop, Panel, _scale, _translate);
        }

        public void Hide() => HideWithAnimation(_backdrop, Panel, () => CloseCount++);

        protected override void HideCore() => Hide();

        public void Dispose() => _host.Close();
    }
}
