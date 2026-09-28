using System.Diagnostics;
using System.Text;
using Vex.App.Terminal.Native;
using Vex.Libghostty;
using Vex.Terminal;
using Xunit;

namespace Vex.App.Tests;

public class TerminalStartupRegressionTests
{
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
