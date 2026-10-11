#if DEBUG || VEX_SELFTEST
using System.Windows;
using System.Windows.Threading;
using Vex.App.Terminal.Native;

namespace Vex.App;

public sealed partial class MainWindow
{
    internal bool SelfTestTabPeek(NativeTerminalControl terminal)
    {
        var size = terminal.RenderSize;
        var cols = terminal.SelfTestCols;
        var rows = terminal.SelfTestRows;
        var project = _workspace.SelectedProject;
        if (project is null) return false;
        var selected = project.SelectedTab;
        TabPeek.Show(project);
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        var unchanged = terminal.RenderSize == size && terminal.SelfTestCols == cols && terminal.SelfTestRows == rows
            && ReferenceEquals(selected, project.SelectedTab) && TabPeek.IsRefreshingPreviews;
        TabPeek.Hide();
        return unchanged && !TabPeek.IsRefreshingPreviews;
    }
}
#endif
