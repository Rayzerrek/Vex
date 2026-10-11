using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vex.App.Model;
using Vex.App.Terminal.Native;
using Vex.Libghostty;
using Vex.Terminal;
using Xunit;

namespace Vex.App.Tests;

[Collection("CustomTheme")]
public sealed class WslUbuntuIntegrationTests
{
    [Theory]
    [InlineData("wsl", null)]
    [InlineData("cmd", null)]
    [InlineData("cmd", "wsl:/bin/bash")]
    public async Task UbuntuFolder_RendersCommandAndClosesAsShell(string defaultShell, string? paneShell)
    {
        // Opt in because normal test machines need not have Ubuntu installed.
        if (Environment.GetEnvironmentVariable("VEX_TEST_UBUNTU") != "1")
            return;

        var directory = await WslProjectLauncher.ResolveDirectoryAsync("Ubuntu", "~", CancellationToken.None);
        Assert.True(Directory.Exists(directory));
        var originalShell = AppSettings.Instance.ShellId;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            TerminalPane? pane = null;
            Window? window = null;
            try
            {
                AppSettings.Instance.ShellId = defaultShell;
                TerminalSessionPrewarmer.Dispose();
                pane = new TerminalPane(directory,
                    "printf '__VEX_%s__\\n' UBUNTU_LIVE; pwd; . /etc/os-release; printf '__DISTRO_%s__\\n' \"$ID\"; printf '\\033]7;file://vex-linux/\\007'", paneShell);
                var control = Assert.IsType<NativeTerminalControl>(pane.View);
                var field = Assert.IsAssignableFrom<FieldInfo>(typeof(NativeTerminalControl)
                    .GetField("_terminal", BindingFlags.NonPublic | BindingFlags.Instance));
                var terminal = Assert.IsType<GhosttyTerminal>(field.GetValue(control));
                window = new Window { Content = control, Width = 900, Height = 550, ShowActivated = false };
                window.Show();
                try { WaitForDispatcher(() => ReadScreen(terminal).Contains("__DISTRO_ubuntu__", StringComparison.Ordinal)); }
                catch (Exception exception) { throw new InvalidOperationException("Ubuntu terminal screen: " + ReadScreen(terminal), exception); }
                var screen = ReadScreen(terminal);
                Assert.Contains("__VEX_UBUNTU_LIVE__", screen);
                Assert.Contains(WslShellPaths.ToLinuxPath(directory), screen);
                Assert.NotNull(control.ProcessId);
                WaitForDispatcher(() => control.WorkingDirectory == @"\\wsl.localhost\Ubuntu\");
                Assert.Equal(@"\\wsl.localhost\Ubuntu\", pane.WorkingDirectory);

                var index = ProcessTree.Index.Build(Assert.IsAssignableFrom<IReadOnlyList<(uint, uint, string)>>(ProcessTree.Snapshot()));
                Assert.NotNull(index);
                pane.RefreshAppIcon(index, new Dictionary<uint, string?>());
                Assert.False(pane.HasActiveProcess);
                Assert.Null(pane.ActiveProcessName);
                Assert.Same(AppIconCatalog.ResolveIcon("wsl"), pane.AppIcon);
                Assert.False(TabCloseConfirmation.GetCloseInfo(pane, index).NeedsConfirmation);

                var reportDirectory = Environment.GetEnvironmentVariable("VEX_TEST_ARTIFACTS");
                if (!string.IsNullOrEmpty(reportDirectory))
                {
                    Directory.CreateDirectory(reportDirectory);
                    File.WriteAllText(Path.Combine(reportDirectory, "ubuntu-terminal.txt"), screen);
                    var bitmap = new RenderTargetBitmap(900, 550, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(reportDirectory, "ubuntu-terminal.png"));
                    encoder.Save(output);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                pane?.Dispose();
                window?.Close();
                TerminalSessionPrewarmer.Dispose();
                AppSettings.Instance.ShellId = originalShell;
                AppSettings.Instance.Flush();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(35)), "Ubuntu WPF integration test did not finish.");
        Assert.Null(failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplicationWithoutOscTitle_ReplacesWslIconAndRestoresItOnExit(bool prewarmed)
    {
        if (Environment.GetEnvironmentVariable("VEX_TEST_UBUNTU") != "1")
            return;

        var originalShell = AppSettings.Instance.ShellId;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            TerminalPane? pane = null;
            Window? window = null;
            try
            {
                AppSettings.Instance.ShellId = "wsl";
                TerminalSessionPrewarmer.Dispose();
                const string directory = @"\\wsl.localhost\Ubuntu\";
                const string command = "python3 -c 'import time; print(\"__VEX_\"+\"PYTHON_READY__\", flush=True); time.sleep(120)'";
                if (prewarmed)
                    TerminalSessionPrewarmer.StartPrewarm(directory, "wsl");
                pane = new TerminalPane(directory, prewarmed ? null : command);
                var control = Assert.IsType<NativeTerminalControl>(pane.View);
                var field = Assert.IsAssignableFrom<FieldInfo>(typeof(NativeTerminalControl)
                    .GetField("_terminal", BindingFlags.NonPublic | BindingFlags.Instance));
                var terminal = Assert.IsType<GhosttyTerminal>(field.GetValue(control));
                window = new Window { Content = control, Width = 900, Height = 550, ShowActivated = false };
                window.Show();
                var sessionField = Assert.IsAssignableFrom<FieldInfo>(typeof(NativeTerminalControl)
                    .GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance));
                if (prewarmed)
                {
                    WaitForDispatcher(() => control.ProcessId is not null);
                    Assert.IsType<TerminalSession>(sessionField.GetValue(control))
                        .Write(System.Text.Encoding.UTF8.GetBytes(command + "\r"));
                }
                WaitForDispatcher(() => ReadScreen(terminal).Contains("__VEX_PYTHON_READY__", StringComparison.Ordinal));

                var index = Assert.IsType<ProcessTree.Index>(ProcessTree.Index.Build(
                    Assert.IsAssignableFrom<IReadOnlyList<(uint, uint, string)>>(ProcessTree.Snapshot())));
                pane.RefreshAppIcon(index, new Dictionary<uint, string?>());
                WaitForDispatcher(() => ReferenceEquals(AppIconCatalog.ResolveIcon("python3"), pane.AppIcon));
                Assert.True(pane.HasActiveProcess);
                Assert.Equal("python3", pane.ActiveProcessName);

                Assert.IsType<TerminalSession>(sessionField.GetValue(control)).Write(new byte[] { 3 });
                WaitForDispatcher(() => ReferenceEquals(AppIconCatalog.ResolveIcon("wsl"), pane.AppIcon));
                Assert.False(pane.HasActiveProcess);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                pane?.Dispose();
                window?.Close();
                TerminalSessionPrewarmer.Dispose();
                AppSettings.Instance.ShellId = originalShell;
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(50)), "WSL application icon test did not finish.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static string ReadScreen(GhosttyTerminal terminal) =>
        string.Join("\n", terminal.FrameRows.Select(row => string.Concat(row.Cells.Select(cell => cell.Text))));

    private static void WaitForDispatcher(Func<bool> ready)
    {
        var frame = new DispatcherFrame();
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            if (ready() || watch.Elapsed > TimeSpan.FromSeconds(20))
                frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(ready(), "Ubuntu command did not render in the Vex terminal within 20 seconds.");
    }
}
