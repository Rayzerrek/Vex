using System.Windows.Threading;
using Vex.App.Model;
using Vex.App.Terminal.Native;
using Vex.Terminal;
using Xunit;

namespace Vex.App.Tests;

[Collection("CustomTheme")]
public sealed class EditorAppIconTests
{
    [Fact]
    public void NeovimWithTypeScriptLanguageServer_KeepsEditorIdentityUntilItExits()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var settings = AppSettings.Instance;
            var previousShell = settings.ShellId;
            try
            {
                settings.ShellId = "cmd";
                using var pane = new TerminalPane(Environment.CurrentDirectory);
                var control = Assert.IsType<NativeTerminalControl>(pane.View);
                var frame = new DispatcherFrame();
                var started = System.Diagnostics.Stopwatch.StartNew();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) =>
                {
                    if (control.ProcessId is not null || started.Elapsed > TimeSpan.FromSeconds(5))
                        frame.Continue = false;
                };
                timer.Start();
                try { Dispatcher.PushFrame(frame); }
                finally { timer.Stop(); }

                var shellPid = (uint)Assert.IsType<int>(control.ProcessId);
                var index = Assert.IsType<ProcessTree.Index>(ProcessTree.Index.Build(new[]
                {
                    (Pid: shellPid, ParentPid: 0u, Name: "cmd"),
                    (Pid: 1u, ParentPid: shellPid, Name: "nvim"),
                    (Pid: 2u, ParentPid: 1u, Name: "node"),
                }));
                pane.ApplyTerminalTitle("main.ts - NVIM");
                Assert.Same(AppIcon.Glyph("neovim"), pane.AppIcon);
                pane.RefreshAppIcon(index, new Dictionary<uint, string?>
                {
                    [2] = @"node C:\npm\node_modules\typescript\lib\tsserver.js",
                });

                Assert.Same(AppIcon.Glyph("neovim"), pane.AppIcon);
                Assert.Equal("nvim", pane.ActiveProcessName);
                Assert.Equal("main.ts", pane.Title);
                Assert.Equal("nvim", TabCloseConfirmation.GetCloseInfo(pane, index).AppName);

                var shellOnly = Assert.IsType<ProcessTree.Index>(ProcessTree.Index.Build(new[]
                {
                    (Pid: shellPid, ParentPid: 0u, Name: "cmd"),
                }));
                pane.ApplyTerminalTitle(@"C:\work\project");
                pane.RefreshAppIcon(shellOnly, new Dictionary<uint, string?>());

                Assert.NotSame(AppIcon.Glyph("neovim"), pane.AppIcon);
                Assert.False(pane.HasActiveProcess);
                Assert.Null(pane.ActiveProcessName);
                Assert.Equal("project", pane.Title);
                Assert.False(TabCloseConfirmation.GetCloseInfo(pane, shellOnly).NeedsConfirmation);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                settings.ShellId = previousShell;
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Editor application icon test did not finish.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
