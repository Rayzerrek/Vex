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

        var completed = await Task.WhenAny(promptTcs.Task, Task.Delay(2500));
        Assert.True(promptTcs.Task.IsCompleted, "Shell prompt did not arrive within 2500ms; DA1 response may be dropped or delayed.");

        var fullOutput = string.Join("", chunks);
        Assert.DoesNotContain("TERM=dumb", fullOutput);

        prewarmed.Dispose();
        lease.Dispose();
        TerminalSessionPrewarmer.Dispose();
    }
}
