using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Vex.App.Terminal.Native;
using Vex.Terminal;
using Xunit;

namespace Vex.App.Tests;

[Collection("CustomTheme")]
public sealed class TerminalPrewarmFailureTests
{
    [Fact]
    public async Task FailedPrewarm_RetriesColdInsteadOfTakingAnotherPrewarm()
    {
        TerminalSessionPrewarmer.Dispose();
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var slotField = Assert.IsAssignableFrom<FieldInfo>(typeof(TerminalSessionPrewarmer).GetField("_slot", BindingFlags.NonPublic | BindingFlags.Static));
        Task<TerminalSession>? replacementTask = null;
        int? coldPid = null;
        Exception? failure = null;
        try
        {
            TerminalSessionPrewarmer.StartPrewarm(directory, "cmd");
            var slot = Assert.IsType<TerminalSessionPrewarmer.Slot>(slotField.GetValue(null));
            await Assert.IsAssignableFrom<Task<TerminalSession>>(slot.SessionTask);
            slot.SessionTask = Task.FromException<TerminalSession>(new IOException("Simulated failed prewarm startup."));
            var thread = new Thread(() =>
            {
                try
                {
                    using var control = new NativeTerminalControl(directory, shellId: "cmd");
                    var replacement = Assert.IsType<TerminalSessionPrewarmer.Slot>(slotField.GetValue(null));
                    replacementTask = Assert.IsAssignableFrom<Task<TerminalSession>>(replacement.SessionTask);
                    var frame = new DispatcherFrame();
                    var started = Environment.TickCount64;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                    timer.Tick += (_, _) =>
                    {
                        if (control.ProcessId is not null || Environment.TickCount64 - started > 5000)
                        {
                            timer.Stop();
                            frame.Continue = false;
                        }
                    };
                    timer.Start();
                    Dispatcher.PushFrame(frame);
                    coldPid = control.ProcessId;
                    Assert.NotNull(coldPid);
                }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(failure);
            var warmReplacement = await Assert.IsAssignableFrom<Task<TerminalSession>>(replacementTask);
            Assert.NotEqual(warmReplacement.ProcessId, coldPid);
        }
        finally { TerminalSessionPrewarmer.Dispose(); }
    }
}
