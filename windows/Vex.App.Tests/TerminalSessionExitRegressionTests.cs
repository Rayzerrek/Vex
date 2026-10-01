using System.Reflection;
using System.Windows.Threading;
using Vex.App.Terminal.Native;
using Vex.Terminal;
using Xunit;

namespace Vex.App.Tests;

[Collection("CustomTheme")]
public sealed class TerminalSessionExitRegressionTests
{
    [Fact]
    public void QueuedExit_FromReplacedSessionDoesNotCloseCurrentPane()
    {
        RunOnStaThread(control =>
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var sessionField = typeof(NativeTerminalControl).GetField("_session", flags)!;
            var current = sessionField.GetValue(control);
            using var retired = new TerminalSession();
            var exits = 0;
            control.ProcessExited += _ => exits++;
            sessionField.SetValue(control, retired);
            var callback = typeof(NativeTerminalControl).GetMethod("OnSessionExited", flags)!;
            callback.Invoke(control, new object[] { retired, 7 });
            sessionField.SetValue(control, current);
            PumpDispatcher();
            Assert.Equal(0, exits);
        });
    }

    [Fact]
    public void RejectedPrewarm_ExitDoesNotCloseColdStartedPane()
    {
        RunOnStaThread(control => WithRetiredSession(control, (retired, restoreCurrent) =>
        {
            var slot = new TerminalSessionPrewarmer.Slot("C:\\rejected-exit-test", "cmd")
            {
                Session = retired,
                Invalidated = true,
            };
            using var lease = new TerminalSessionPrewarmer.Lease(slot, Task.FromResult(retired));
            var exits = 0;
            control.ProcessExited += _ => exits++;
            Assert.False(control.TryAttachPrewarmedSession(lease, retired));
            restoreCurrent();
            RaiseSessionExit(retired);
            PumpDispatcher();
            Assert.Equal(0, exits);
        }));
    }

    [Fact]
    public void AcceptedPrewarm_ExitBeforeAttachmentIsReplayedExactlyOnce()
    {
        RunOnStaThread(control => WithRetiredSession(control, (retired, _) =>
        {
            var slot = new TerminalSessionPrewarmer.Slot("C:\\accepted-exit-test", "cmd") { Session = retired };
            using var lease = new TerminalSessionPrewarmer.Lease(slot, Task.FromResult(retired));
            var exits = new List<int>();
            control.ProcessExited += code => exits.Add(code);
            RaiseSessionExit(retired);
            Assert.Equal(-1, retired.ExitCode);
            Assert.True(control.TryAttachPrewarmedSession(lease, retired));
            // The event and cached exit can both reach the observer during subscription.
            var handler = (Action<int>)typeof(TerminalSession)
                .GetField("Exited", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(retired)!;
            handler(-1);
            PumpDispatcher();
            Assert.Equal(new[] { -1 }, exits);
        }));
    }

    private static void RaiseSessionExit(TerminalSession session) => typeof(TerminalSession)
        .GetMethod("RaiseExitedOnce", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null);

    private static void WithRetiredSession(NativeTerminalControl control, Action<TerminalSession, Action> test)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var sessionField = typeof(NativeTerminalControl).GetField("_session", flags)!;
        var handlerField = typeof(NativeTerminalControl).GetField("_sessionExitedHandler", flags)!;
        var current = sessionField.GetValue(control);
        var currentHandler = handlerField.GetValue(control);
        using var retired = new TerminalSession();
        try { test(retired, () => sessionField.SetValue(control, current)); }
        finally
        {
            sessionField.SetValue(control, current);
            handlerField.SetValue(control, currentHandler);
        }
    }

    private static void RunOnStaThread(Action<NativeTerminalControl> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            NativeTerminalControl? control = null;
            try
            {
                control = new NativeTerminalControl(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                Assert.True(SpinWait.SpinUntil(() => control.ProcessId is not null, 5000));
                PumpDispatcher();
                test(control);
            }
            catch (Exception ex) { failure = ex; }
            finally { control?.Dispose(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Session exit regression timed out");
        Assert.Null(failure);
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
