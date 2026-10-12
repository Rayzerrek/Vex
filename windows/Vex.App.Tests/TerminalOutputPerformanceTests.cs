using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Vex.App.Terminal.Native;
using Vex.Libghostty;
using Xunit;
using Xunit.Abstractions;

namespace Vex.App.Tests;

[Collection("CustomTheme")]
public sealed class TerminalOutputPerformanceTests(ITestOutputHelper output)
{
    private delegate void TerminalInputWriter(ReadOnlySpan<byte> input);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InputEcho_BypassesOutputPacingIncludingAnAlreadyArmedTimer(bool timerAlreadyArmed)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new NativeTerminalControl(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                var type = typeof(NativeTerminalControl);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var lastRedraw = type.GetField("_lastOutputRedraw", flags)
                    ?? throw new InvalidOperationException("Terminal output timestamp was not found.");
                var scheduled = type.GetField("_redrawScheduled", flags)
                    ?? throw new InvalidOperationException("Terminal output scheduling state was not found.");
                var receive = (type.GetMethod("OnSessionOutput", flags)
                    ?? throw new InvalidOperationException("Terminal output callback was not found."))
                    .CreateDelegate<Action<ArraySegment<byte>>>(control);
                var write = (type.GetMethod("WriteUserInput", flags)
                    ?? throw new InvalidOperationException("Terminal input writer was not found."))
                    .CreateDelegate<TerminalInputWriter>(control);

                // Keep paced output pending past dispatcher idle without a timing assertion.
                lastRedraw.SetValue(control, Environment.TickCount64 + 5000);
                if (timerAlreadyArmed)
                {
                    SendOutput();
                    PumpUntilIdle();
                    Assert.True(scheduled.GetValue(control) is true);
                }
                write("a"u8);
                SendOutput();
                PumpUntilIdle();
                Assert.True(scheduled.GetValue(control) is false, "Input echo was left waiting on the paced-output timer.");

                void SendOutput()
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(1);
                    buffer[0] = (byte)'a';
                    receive(new ArraySegment<byte>(buffer, 0, 1));
                }
                void PumpUntilIdle()
                {
                    var frame = new DispatcherFrame();
                    control.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
                    Dispatcher.PushFrame(frame);
                }
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Input echo scheduling test timed out.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void VisibleOutput_UsesCompositionFramesAndDetachesAfterPaintingOrDisposal()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            NativeTerminalControl? control = null;
            Window? window = null;
            try
            {
                control = new NativeTerminalControl(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                window = new Window { Content = control, Width = 1000, Height = 600, ShowActivated = false };
                var type = typeof(NativeTerminalControl);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var schedule = (type.GetMethod("ScheduleOutputFrame", flags)
                    ?? throw new InvalidOperationException("Terminal composition scheduling callback was not found."))
                    .CreateDelegate<Action>(control);
                var render = (type.GetMethod("OnOutputRendering", flags)
                    ?? throw new InvalidOperationException("Terminal composition frame callback was not found."))
                    .CreateDelegate<EventHandler>(control);
                var advance = (type.GetMethod("TryAdvanceOutputFrame", flags)
                    ?? throw new InvalidOperationException("Terminal composition pacing method was not found."))
                    .CreateDelegate<Func<TimeSpan, bool>>(control);
                foreach (var millisecond in new[] { 0, 16, 33, 50, 66, 83, 100 })
                    Assert.True(advance(TimeSpan.FromMilliseconds(millisecond)), "Rounded 60 Hz ticks must not be dropped.");
                var accepted = 0;
                for (var millisecond = 0; millisecond < 1000; millisecond++)
                    if (advance(TimeSpan.FromMilliseconds(millisecond)))
                        accepted++;
                Assert.InRange(accepted, 59, 61);
                Assert.Equal(60, TerminalDisplayRefreshRate.ReadRefreshRate(new DrawingVisual()));

                window.Show();
                EventArgs? observedFrame = null;
                void CaptureFrame(object? sender, EventArgs args) => observedFrame = args;
                CompositionTarget.Rendering += CaptureFrame;
                try { PumpFor(TimeSpan.FromMilliseconds(500)); }
                finally { CompositionTarget.Rendering -= CaptureFrame; }
                (type.GetField("_outputCompositionFrame", flags)
                    ?? throw new InvalidOperationException("Terminal composition clock state was not found."))
                    .SetValue(control, null);

                var pending = type.GetField("_redrawScheduled", flags)
                    ?? throw new InvalidOperationException("Terminal output scheduling state was not found.");
                var subscribed = type.GetField("_outputRenderingSubscribed", flags)
                    ?? throw new InvalidOperationException("Terminal composition subscription state was not found.");
                var lastRedraw = type.GetField("_lastOutputRedraw", flags)
                    ?? throw new InvalidOperationException("Terminal output timestamp was not found.");

                lastRedraw.SetValue(control, Environment.TickCount64);
                pending.SetValue(control, true);
                schedule();
                schedule();
                Assert.True(subscribed.GetValue(control) is true);
                var timer = Assert.IsType<DispatcherTimer>(type.GetField("_outputRedrawTimer", flags)?.GetValue(control));
                Assert.Equal(TimeSpan.FromMilliseconds(100), timer.Interval);
                var frame = Assert.IsType<RenderingEventArgs>(observedFrame);
                render(null, frame);
                Assert.True(pending.GetValue(control) is false);
                Assert.True(subscribed.GetValue(control) is false);
                Assert.False(timer.IsEnabled);

                lastRedraw.SetValue(control, Environment.TickCount64);
                pending.SetValue(control, true);
                schedule();
                Assert.True(subscribed.GetValue(control) is true);
                render(null, frame);
                Assert.True(pending.GetValue(control) is true, "Repeated composition timestamp must not paint twice.");
                render(null, EventArgs.Empty);
                Assert.True(pending.GetValue(control) is false);
                Assert.True(subscribed.GetValue(control) is false);

                lastRedraw.SetValue(control, Environment.TickCount64);
                pending.SetValue(control, true);
                schedule();
                Assert.True(subscribed.GetValue(control) is true);
                control.Dispose();
                Assert.True(subscribed.GetValue(control) is false);
                Assert.False(timer.IsEnabled);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                control?.Dispose();
                window?.Close();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Composition scheduling test timed out.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void BusyParser_RedrawDoesNotBlockUiThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            NativeTerminalControl? control = null;
            Thread? parser = null;
            using var locked = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            try
            {
                control = new NativeTerminalControl(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                var terminal = GetTerminal(control);
                var flush = (typeof(NativeTerminalControl).GetMethod("FlushRedraw", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Terminal redraw callback was not found."))
                    .CreateDelegate<Action>(control);
                parser = new Thread(() =>
                {
                    lock (terminal.SyncRoot)
                    {
                        locked.Set();
                        release.Wait(TimeSpan.FromMilliseconds(500));
                    }
                }) { IsBackground = true };
                parser.Start();
                Assert.True(locked.Wait(TimeSpan.FromSeconds(5)));
                var started = Stopwatch.GetTimestamp();
                flush();
                var elapsed = Stopwatch.GetElapsedTime(started);
                output.WriteLine($"Redraw while parser owns terminal: {elapsed.TotalMilliseconds:F1}ms");
                Assert.True(elapsed.TotalMilliseconds < 100, "Redraw waited for the busy parser on the UI thread.");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                release.Set();
                parser?.Join(5000);
                control?.Dispose();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Busy parser test timed out.");
        Assert.Null(failure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ContinuousOutput_BoundsDispatcherWorkAndPreservesLastFrame(bool visible)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            NativeTerminalControl? control = null;
            Window? window = null;
            Thread? producer = null;
            using var stop = new CancellationTokenSource();
            try
            {
                control = new NativeTerminalControl(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                window = new Window { Content = control, Width = 1000, Height = 600, ShowActivated = false };
                window.Show();
                PumpFor(TimeSpan.FromMilliseconds(500));
                if (!visible)
                    control.Visibility = Visibility.Hidden;
                PumpFor(TimeSpan.FromMilliseconds(100));

                var receive = (typeof(NativeTerminalControl).GetMethod("OnSessionOutput", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Terminal output callback was not found."))
                    .CreateDelegate<Action<ArraySegment<byte>>>(control);
                var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("output flood 0123456789 abcdefghijklmnopqrstuvwxyz\r\n", 32)));
                var dispatcher = Dispatcher.CurrentDispatcher;
                var callbacks = 0;
                void CountCallback(object? sender, DispatcherHookEventArgs e)
                {
                    if (e.Operation.Priority == DispatcherPriority.Background)
                        callbacks++;
                }
                dispatcher.Hooks.OperationCompleted += CountCallback;
                var chunks = 0;
                producer = new Thread(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        for (var burst = 0; burst < 8; burst++)
                        {
                            Send(payload);
                            Interlocked.Increment(ref chunks);
                            Thread.Yield();
                        }
                        Thread.Sleep(1);
                    }
                }) { IsBackground = true };
                using var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                producer.Start();
                PumpFor(TimeSpan.FromSeconds(1));
                stop.Cancel();
                Assert.True(producer.Join(5000), "Output producer did not stop.");
                dispatcher.Hooks.OperationCompleted -= CountCallback;
                output.WriteLine($"visible={visible} chunks={chunks} callbacks={callbacks} elapsed={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}ms cpu={(process.TotalProcessorTime - cpu).TotalMilliseconds:F0}ms uiBytes={GC.GetAllocatedBytesForCurrentThread() - allocated}");

                Send("\r\nVEX_FLOOD_LAST_FRAME\r\n"u8.ToArray());
                PumpFor(TimeSpan.FromMilliseconds(150));
                control.Visibility = Visibility.Visible;
                PumpFor(TimeSpan.FromMilliseconds(50));
                var terminal = GetTerminal(control);
                var screen = string.Concat(terminal.FrameRows.SelectMany(row => row.Cells).Select(cell => cell.Text));
                Assert.Contains("VEX_FLOOD_LAST_FRAME", screen);
                Assert.True(chunks >= 8, $"Output producer did not complete a burst: {chunks} chunks.");
                var maximumCallbacks = visible ? 2 * TerminalDisplayRefreshRate.ReadRefreshRate(control) + 30 : 35;
                Assert.InRange(callbacks, 1, maximumCallbacks);

                Send("\x1b[HQUIET_OUTPUT"u8.ToArray());
                var idleFrame = new DispatcherFrame();
                dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => idleFrame.Continue = false);
                Dispatcher.PushFrame(idleFrame);
                Assert.StartsWith("QUIET_OUTPUT", string.Concat(terminal.FrameRows[0].Cells.Select(cell => cell.Text)));

                void Send(byte[] bytes)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(bytes.Length);
                    bytes.CopyTo(buffer, 0);
                    receive(new ArraySegment<byte>(buffer, 0, bytes.Length));
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                stop.Cancel();
                producer?.Join(5000);
                control?.Dispose();
                window?.Close();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Output performance test timed out.");
        Assert.Null(failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TitleFlood_CoalescesUiCallbacksAndKeepsFinalTitle(bool disposeBeforeDispatch)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new NativeTerminalControl(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                var terminal = GetTerminal(control);
                var notifications = 0;
                var lastTitle = "";
                control.TitleRawChanged += title => { notifications++; lastTitle = title; };
                var producer = new Thread(() =>
                {
                    for (var i = 0; i < 1000; i++)
                        terminal.Feed($"\x1b]2;build step {i}\x07");
                });
                producer.Start();
                Assert.True(producer.Join(5000));
                if (disposeBeforeDispatch) control.Dispose();
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
                output.WriteLine($"Title flood: {notifications} UI notifications");
                Assert.Equal(disposeBeforeDispatch ? "" : "build step 999", lastTitle);
                Assert.Equal(disposeBeforeDispatch ? 0 : 1, notifications);
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        Assert.Null(failure);
    }

    private static GhosttyTerminal GetTerminal(NativeTerminalControl control) =>
        typeof(NativeTerminalControl).GetField("_terminal", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(control) is GhosttyTerminal terminal
            ? terminal
            : throw new InvalidOperationException("Terminal emulator was not found.");

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
