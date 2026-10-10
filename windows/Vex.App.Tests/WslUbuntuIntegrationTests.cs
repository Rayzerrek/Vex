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
    [Fact]
    public void UbuntuFolder_RendersCommandAndClosesAsShell()
    {
        // Opt in because normal test machines need not have Ubuntu installed.
        if (Environment.GetEnvironmentVariable("VEX_TEST_UBUNTU") != "1")
            return;

        const string directory = @"\\wsl.localhost\Ubuntu\home\kacper";
        Assert.True(Directory.Exists(directory));
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
                pane = new TerminalPane(directory,
                    "printf '__VEX_%s__\\n' UBUNTU_LIVE; pwd; . /etc/os-release; printf '__DISTRO_%s__\\n' \"$ID\"");
                var control = Assert.IsType<NativeTerminalControl>(pane.View);
                var terminal = Assert.IsType<GhosttyTerminal>(typeof(NativeTerminalControl)
                    .GetField("_terminal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(control));
                window = new Window { Content = control, Width = 900, Height = 550, ShowActivated = false };
                window.Show();
                try { WaitForDispatcher(() => ReadScreen(terminal).Contains("__DISTRO_ubuntu__", StringComparison.Ordinal)); }
                catch (Exception exception) { throw new InvalidOperationException("Ubuntu terminal screen: " + ReadScreen(terminal), exception); }
                var screen = ReadScreen(terminal);
                Assert.Contains("__VEX_UBUNTU_LIVE__", screen);
                Assert.Contains("/home/kacper", screen);
                Assert.NotNull(control.ProcessId);

                var index = ProcessTree.Index.Build(ProcessTree.Snapshot()!);
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
