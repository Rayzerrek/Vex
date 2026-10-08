using System.Text;
using Vex.App.Model;
using Vex.App.Terminal.Native;
using Vex.Libghostty;
using Vex.Terminal;
using Xunit;

namespace Vex.App.Tests;

[Collection("CustomTheme")]
public sealed class ProgramStatusTransportTests
{
    [Fact]
    public async Task NushellIntegration_PathWithSpaceAndApostropheStartsCommandOnce()
    {
        if (!ShellRegistry.HasDetected("nu")) return;
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vex's shell test " + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var scriptPath = System.IO.Path.Combine(directory, "Vex.nu");
        try
        {
            System.IO.File.WriteAllText(scriptPath, "$env.config = ($env.config | upsert shell_integration.osc133 true)\nprint 'VEX_BOOTSTRAP_READY'");
            var arguments = ShellLaunchBuilder.BuildNushellArguments(scriptPath, "print 'VEX_COMMAND_READY'");
            using var terminal = new GhosttyTerminal(80, 24);
            using var session = new TerminalSession();
            var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = new StringBuilder();
            terminal.WritePty += (bytes, count) => session.WriteResponse(bytes.AsSpan(0, count));
            session.OutputReceived += data =>
            {
                try
                {
                    terminal.Feed(data.Array!, data.Offset, data.Count);
                    output.Append(Encoding.UTF8.GetString(data.Array!, data.Offset, data.Count));
                    var snapshot = output.ToString();
                    if (snapshot.Contains("VEX_BOOTSTRAP_READY", StringComparison.Ordinal)
                        && snapshot.Contains("VEX_COMMAND_READY", StringComparison.Ordinal)
                        && terminal.ProgramStatus.State == ProgramStatusState.Idle) ready.TrySetResult(snapshot);
                }
                catch (Exception error) { ready.TrySetException(error); }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(data.Array!); }
            };
            session.Start(Environment.CurrentDirectory, 80, 24, ShellRegistry.Resolve("nu")!.Value.Program, "--no-config-file --no-history " + arguments);
            var result = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, result.Split("VEX_BOOTSTRAP_READY", StringSplitOptions.None).Length - 1);
            Assert.Equal(1, result.Split("VEX_COMMAND_READY", StringSplitOptions.None).Length - 1);
        }
        finally
        {
            System.IO.File.Delete(scriptPath);
            System.IO.Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task PowerShellInteractiveCommand_ReportsWorkingAndExitFailure()
    {
        if (!ShellRegistry.HasDetected("pwsh")) return;
        var launch = ShellLaunchBuilder.BuildShellLaunch("pwsh", "Set-PSReadLineOption -HistorySaveStyle SaveNothing");
        using var terminal = new GhosttyTerminal(80, 24);
        using var session = new TerminalSession();
        var prompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var working = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commandStarted = false;
        terminal.ProgramStatusChanged += (status, attention) =>
        {
            if (status.State == ProgramStatusState.Idle) prompt.TrySetResult();
            if (!Volatile.Read(ref commandStarted)) return;
            if (status.State == ProgramStatusState.Working) working.TrySetResult();
            if (status.State == ProgramStatusState.Error) failed.TrySetResult();
        };
        terminal.WritePty += (bytes, count) => session.WriteResponse(bytes.AsSpan(0, count));
        session.OutputReceived += data =>
        {
            try { terminal.Feed(data.Array!, data.Offset, data.Count); }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(data.Array!); }
        };
        session.Start(Environment.CurrentDirectory, 80, 24, launch.Program, "-NoProfile " + launch.Arguments);
        await prompt.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Volatile.Write(ref commandStarted, true);
        session.Write(Encoding.UTF8.GetBytes("Start-Sleep -Milliseconds 200; & $env:ComSpec /d /c exit 7\r"));
        await working.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Exited with code 7", terminal.ProgramStatus.Message);
    }

    [Fact]
    public async Task NushellSavedLayoutCommand_ReportsExitFailure()
    {
        if (!ShellRegistry.HasDetected("nu")) return;
        var launch = ShellLaunchBuilder.BuildShellLaunch("nu", "run-external $env.ComSpec /d /c exit 7");
        using var terminal = new GhosttyTerminal(80, 24);
        using var session = new TerminalSession();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.ProgramStatusChanged += (status, _) =>
        {
            if (status.State == ProgramStatusState.Error) failed.TrySetResult();
        };
        terminal.WritePty += (bytes, count) => session.WriteResponse(bytes.AsSpan(0, count));
        session.OutputReceived += data =>
        {
            try { terminal.Feed(data.Array!, data.Offset, data.Count); }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(data.Array!); }
        };
        session.Start(Environment.CurrentDirectory, 80, 24, launch.Program, "--no-config-file --no-history " + launch.Arguments);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("Exited with code 7", terminal.ProgramStatus.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConPtyProgramStatus_QueryAndReportsSurviveDirectAndPrewarmedStartup(bool prewarm)
    {
        const string script = """
            Add-Type 'using System; using System.Runtime.InteropServices; public static class StatusInputMode { [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int n); [DllImport("kernel32.dll")] public static extern bool SetConsoleMode(IntPtr h, uint mode); }'
            [StatusInputMode]::SetConsoleMode([StatusInputMode]::GetStdHandle(-10), 0x200) | Out-Null
            [Console]::Write([char]27 + ']7501;?' + [char]27 + '\' + [char]27 + '[c')
            $stream = [Console]::OpenStandardInput()
            $reply = ''
            do { $reply += [char]$stream.ReadByte() } until ($reply.EndsWith('\'))
            if ($reply -eq ([char]27 + ']7501;?' + [char]27 + '\')) {
                [Console]::Write([char]27 + ']7501;state=blocked:kind=question:app=probe' + [char]27 + '\')
                [Console]::Write('VEX_STATUS_READY')
            }
            Start-Sleep -Seconds 30
            """;
        var profile = new ShellProfile { Id = "vex-status-probe", Program = TerminalSession.PowerShell(),
            Arguments = "-NoLogo -NoProfile -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) };
        var settings = AppSettings.Instance;
        settings.CustomShells.Add(profile);
        TerminalSessionPrewarmer.Dispose();
        try
        {
            var directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            TerminalSessionPrewarmer.Lease? lease = null;
            using var terminal = new GhosttyTerminal(80, 24);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = new StringBuilder();
            void OnOutput(ArraySegment<byte> data)
            {
                try
                {
                    terminal.Feed(data.Array!, data.Offset, data.Count);
                    lock (output)
                    {
                        output.Append(Encoding.UTF8.GetString(data.Array!, data.Offset, data.Count));
                        if (output.ToString().Contains("VEX_STATUS_READY", StringComparison.Ordinal)) ready.TrySetResult();
                    }
                }
                catch (Exception error) { ready.TrySetException(error); }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(data.Array!); }
            }
            using var session = prewarm ? await StartPrewarmedSession() : new TerminalSession();
            terminal.WritePty += (bytes, count) => session.WriteResponse(bytes.AsSpan(0, count));
            if (lease is not null) Assert.True(lease.AttachOutputHandler(OnOutput));
            else
            {
                session.OutputReceived += OnOutput;
                session.Start(directory, 80, 24, profile.Program, profile.Arguments);
            }
            try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { lock (output) Assert.Fail(output.ToString().Replace("\x1b", "<ESC>")); }
            Assert.Equal(ProgramStatusState.Blocked, terminal.ProgramStatus.State);
            Assert.Equal("question", terminal.ProgramStatus.Kind);
            lease?.Dispose();

            async Task<TerminalSession> StartPrewarmedSession()
            {
                TerminalSessionPrewarmer.StartPrewarm(directory, profile.Id);
                lease = TerminalSessionPrewarmer.Take(directory, profile.Id)!;
                var prewarmed = await lease.SessionTask.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Delay(150);
                return prewarmed;
            }
        }
        finally { TerminalSessionPrewarmer.Dispose(); settings.CustomShells.Remove(profile); }
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("pwsh")]
    [InlineData("nu")]
    public async Task SavedLayoutCommand_StartsOnceAndShellReportsPrompt(string shellId)
    {
        if (!ShellRegistry.HasDetected(shellId)) return;
        var commandFile = System.IO.Path.GetTempFileName();
        try
        {
            var quotedPath = commandFile.Replace("'", "''");
            var command = shellId == "pwsh"
                ? "Set-PSReadLineOption -HistorySaveStyle SaveNothing; Add-Content -LiteralPath '" + quotedPath + "' -Value 'run'; [Console]::WriteLine('VEX_LAYOUT_READY')"
                : shellId == "nu" ? "'run' | save --append \"" + commandFile.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"; print 'VEX_LAYOUT_READY'"
                : "echo run>>\"" + commandFile + "\" & echo VEX_LAYOUT_READY";
            var launch = ShellLaunchBuilder.BuildShellLaunch(shellId, command);
            if (shellId == "pwsh") launch.Arguments = "-NoProfile " + launch.Arguments;
            if (shellId == "nu") launch.Arguments = "--no-config-file --no-history " + launch.Arguments;
            using var terminal = new GhosttyTerminal(80, 24);
            using var session = new TerminalSession();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = new StringBuilder();
            terminal.WritePty += (bytes, count) => session.WriteResponse(bytes.AsSpan(0, count));
            session.OutputReceived += data =>
            {
                try
                {
                    terminal.Feed(data.Array!, data.Offset, data.Count);
                    output.Append(Encoding.UTF8.GetString(data.Array!, data.Offset, data.Count));
                    if (output.ToString().Contains("VEX_LAYOUT_READY\r\n", StringComparison.Ordinal)
                        && terminal.ProgramStatus.State == ProgramStatusState.Idle) ready.TrySetResult();
                }
                catch (Exception error) { ready.TrySetException(error); }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(data.Array!); }
            };
            session.Start(Environment.CurrentDirectory, 80, 24, launch.Program, launch.Arguments);
            try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { Assert.Fail("Shell integration timed out: " + output.ToString().Replace("\x1b", "<ESC>")); }
            // cmd includes the command in OSC 0 titles; an observable side effect
            // distinguishes execution from the same text in terminal metadata.
            Assert.Equal("run", System.IO.File.ReadAllText(commandFile).Trim());
        }
        finally { System.IO.File.Delete(commandFile); }
    }
}
