using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Text;
using Vex.App.Terminal.Native;
using Vex.Libghostty;
using Vex.Terminal;
using Xunit;

namespace Vex.App.Tests;

[Collection("CustomTheme")]
public class TerminalStartupRegressionTests
{
    [Theory]
    [InlineData("cmd")]
    [InlineData("gitbash")]
    [InlineData("wsl")]
    public void DetectedBuiltInShell_ResolvesItsDeclaredProgramAndArguments(string shellId)
    {
        var declared = Model.ShellRegistry.Detected().SingleOrDefault(shell => shell.Id == shellId);
        var resolved = Model.ShellRegistry.Resolve(shellId);
        if (declared is null)
        {
            Assert.Null(resolved);
            return;
        }
        var actual = Assert.IsType<(string Program, string Arguments)>(resolved);
        Assert.Equal(declared.Program, actual.Program);
        Assert.Equal(declared.Arguments, actual.Arguments);
    }

    [Fact]
    public void Prewarmer_ConsecutiveTakesHaveReplacementSessions()
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var shellId = Model.AppSettings.Instance.ShellId;
        try
        {
            TerminalSessionPrewarmer.StartPrewarm(directory, shellId);
            using var first = TerminalSessionPrewarmer.Take(directory, shellId);
            Assert.NotNull(first);

            using var second = TerminalSessionPrewarmer.Take(directory, shellId);
            Assert.NotNull(second);
            Assert.NotSame(first.SessionTask, second.SessionTask);
        }
        finally
        {
            TerminalSessionPrewarmer.Dispose();
        }
    }

    [Fact]
    public async Task ShellSessionReceivesDa1AndRendersPromptPromptly()
    {
        var resolved = Model.ShellRegistry.Resolve(Model.AppSettings.Instance.ShellId);
        if (resolved is null)
            return;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        TerminalSessionPrewarmer.StartPrewarm(userProfile, Model.AppSettings.Instance.ShellId);

        // Simulate short window startup interval
        await Task.Delay(100);

        var lease = TerminalSessionPrewarmer.Take(userProfile, Model.AppSettings.Instance.ShellId);
        Assert.NotNull(lease);

        var prewarmed = await lease.SessionTask;
        using var terminal = new GhosttyTerminal(80, 24);
        TerminalSession? activeSession = prewarmed;

        terminal.WritePty += (data, len) =>
        {
            activeSession?.WriteResponse(data.AsSpan(0, len));
        };

        var promptTcs = new TaskCompletionSource<long>();
        var sw = Stopwatch.StartNew();
        var chunks = new List<string>();

        lease.AttachOutputHandler(chunk =>
        {
            if (chunk.Array is not { } buffer) return;
            try
            {
                var text = Encoding.UTF8.GetString(buffer, chunk.Offset, chunk.Count);
                lock (chunks) chunks.Add(text);

                if (text.Contains('❯') || text.Contains('>') || text.Contains('$'))
                {
                    promptTcs.TrySetResult(sw.ElapsedMilliseconds);
                }

                terminal.Feed(buffer, chunk.Offset, chunk.Count);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        });

        var completed = await Task.WhenAny(promptTcs.Task, Task.Delay(5000));
        Assert.True(promptTcs.Task.IsCompleted, "Shell prompt did not arrive within 5000ms; DA1 response may be dropped or delayed.");

        var fullOutput = string.Join("", chunks);
        Assert.DoesNotContain("TERM=dumb", fullOutput);

        prewarmed.Dispose();
        lease.Dispose();
        TerminalSessionPrewarmer.Dispose();
    }

    [Fact]
    public async Task ShellSwitchFromNushellToPwsh_InvalidatesOldSlotAndStartsNewPrewarm()
    {
        var originalShellId = Model.AppSettings.Instance.ShellId;
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            TerminalSessionPrewarmer.StartPrewarm(userProfile, "nu");
            await Task.Delay(100);

            var lease1 = TerminalSessionPrewarmer.Take(userProfile, "nu");
            Assert.NotNull(lease1);
            var session1 = await lease1.SessionTask;

            // Now user switches settings to pwsh
            Model.AppSettings.Instance.ShellId = "pwsh";

            // When Take is called with pwsh, the mismatched "nu" slot is discarded and pwsh prewarm starts
            var lease2 = TerminalSessionPrewarmer.Take(userProfile, "pwsh");
            Assert.Null(lease2);

            // Allow the newly initiated pwsh prewarm to spin up
            await Task.Delay(300);

            var lease3 = TerminalSessionPrewarmer.Take(userProfile, "pwsh");
            Assert.NotNull(lease3);
            var session3 = await lease3.SessionTask;
            Assert.NotNull(session3);

            session3.Dispose();
            lease3.Dispose();
            session1.Dispose();
            lease1.Dispose();
        }
        finally
        {
            Model.AppSettings.Instance.ShellId = originalShellId;
            TerminalSessionPrewarmer.Dispose();
        }
    }

    [Fact]
    public void TerminalSession_ResizeBeforeChildSpawn_ReturnsSafelyWithoutDeadlock()
    {
        using var session = new TerminalSession();
        // When process is not spawned yet, Resize must not call ResizePseudoConsole or deadlock
        var result = session.Resize(120, 40);
        Assert.True(result);
        Assert.False(session.LastResizeFailed);
    }

    [Fact]
    public void NativeTerminalControl_ColdStart_ExecutesTextEnteredBeforeDispatcherPump()
    {
        TerminalSessionPrewarmer.Dispose();
        var originalShellId = Model.AppSettings.Instance.ShellId;
        Model.AppSettings.Instance.ShellId = "cmd";
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            NativeTerminalControl? control = null;
            Window? window = null;
            DispatcherTimer? timer = null;
            try
            {
                control = new NativeTerminalControl(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                var marker = "VEX_BUFFERED_żółw_" + Guid.NewGuid().ToString("N");
                var keyboard = InputManager.Current.PrimaryKeyboardDevice;
                foreach (var text in new[] { "@echo VEX_BU^FFERED_", marker["VEX_BUFFERED_".Length..] })
                {
                    var composition = new TextComposition(InputManager.Current, control, text);
                    control.RaiseEvent(new TextCompositionEventArgs(keyboard, composition)
                    {
                        RoutedEvent = TextCompositionManager.TextInputEvent,
                    });
                }
                var field = typeof(NativeTerminalControl).GetField("_terminal", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(field);
                var terminal = Assert.IsType<GhosttyTerminal>(field.GetValue(control));
                window = new Window { Content = control, Width = 800, Height = 500, ShowActivated = false };
                window.Show();
                var source = PresentationSource.FromVisual(window);
                Assert.NotNull(source);
                control.RaiseEvent(new KeyEventArgs(keyboard, source, Environment.TickCount, Key.Enter)
                {
                    RoutedEvent = Keyboard.KeyDownEvent,
                });
                control.RaiseEvent(new KeyEventArgs(keyboard, source, Environment.TickCount, Key.Enter)
                {
                    RoutedEvent = Keyboard.KeyUpEvent,
                });
                var frame = new DispatcherFrame();
                var watch = Stopwatch.StartNew();
                var executed = false;
                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) =>
                {
                    executed = string.Concat(terminal.FrameRows.SelectMany(row => row.Cells).Select(cell => cell.Text))
                        .Contains(marker, StringComparison.Ordinal);
                    if (executed || watch.Elapsed > TimeSpan.FromSeconds(8))
                        frame.Continue = false;
                };
                timer.Start();
                Dispatcher.PushFrame(frame);
                Assert.True(executed, "Text entered during cold startup did not execute; echoed input cannot match the marker.");
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
            finally
            {
                timer?.Stop();
                control?.Dispose();
                window?.Close();
            }
        });
        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(15000), "Buffered startup input test did not finish.");
            Assert.Null(threadEx);
        }
        finally
        {
            Model.AppSettings.Instance.ShellId = originalShellId;
            Model.AppSettings.Instance.Flush();
        }
    }

    [Fact]
    public void NativeTerminalControl_ColdStart_PublishesSessionWithoutDispatcherPump()
    {
        TerminalSessionPrewarmer.Dispose();
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            NativeTerminalControl? control = null;
            try
            {
                control = new NativeTerminalControl(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                // Startup queries must have a response destination while WPF
                // is still building the window and cannot pump its dispatcher.
                Assert.True(SpinWait.SpinUntil(() => control.ProcessId is not null, 5000),
                    "Cold session publication waited for the UI dispatcher.");
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
            finally
            {
                control?.Dispose();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(10000), "Cold session startup/dispose did not finish.");
        Assert.Null(threadEx);
    }

    [Fact]
    public void NativeTerminalControl_PendingResizeAfterDisposal_DoesNotCrash()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            NativeTerminalControl? control = null;
            Window? window = null;
            var dispatcher = Dispatcher.CurrentDispatcher;
            DispatcherUnhandledExceptionEventHandler onUnhandledException = (_, e) =>
            {
                threadEx = e.Exception;
                e.Handled = true;
            };
            dispatcher.UnhandledException += onUnhandledException;
            try
            {
                control = new NativeTerminalControl(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                window = new Window { Content = control, Width = 800, Height = 500, ShowActivated = false };
                window.Show();
                window.UpdateLayout();
                PumpPendingLayout();
                Assert.True(control.IsLoaded);

                window.Width = 1000;
                window.UpdateLayout();
                window.Content = null;
                control.Dispose();
                // WPF unload is deferred: the pending Render-priority resize
                // can still see IsLoaded after the native emulator is freed.
                Assert.True(control.IsLoaded);
                PumpPendingLayout();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
            finally
            {
                control?.Dispose();
                window?.Close();
                dispatcher.UnhandledException -= onUnhandledException;
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(10000), "Disposed terminal resize test did not finish.");
        Assert.Null(threadEx);

        static void PumpPendingLayout()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    [Fact]
    public void NativeTerminalControl_ColdStart_DoesNotDeadlockOnResize()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var control = new NativeTerminalControl(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                control.Focus();
                control.Dispose();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(3000), "Thread deadlocked during terminal control startup/dispose");
        Assert.Null(threadEx);
    }
}
