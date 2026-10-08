using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Vex.App.Terminal.Native;
using Vex.Libghostty;
using Xunit;
using Xunit.Abstractions;

namespace Vex.App.Tests;

[Collection("CustomTheme")]
public sealed class TerminalOutputPerformanceTests(ITestOutputHelper output)
{
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
    [InlineData(true, 150)]
    [InlineData(false, 35)]
    public void ContinuousOutput_BoundsDispatcherWorkAndPreservesLastFrame(bool visible, int maximumCallbacks)
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
