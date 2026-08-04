using System.Windows.Threading;

namespace Vex.App.Model;

/// <summary>
/// One shared timer polls every live pane's process tree so tab icons track
/// the app running inside each pane. Panes register on view creation and
/// unregister on dispose; the timer stops when the last pane goes away.
/// </summary>
public static class AppIconTracker
{
    private static readonly List<TerminalPane> Panes = new();
    private static DispatcherTimer? _timer;

    public static void Register(TerminalPane pane)
    {
        Panes.Add(pane);
        _timer ??= StartTimer();
    }

    public static void Unregister(TerminalPane pane)
    {
        Panes.Remove(pane);
        if (Panes.Count == 0)
        {
            _timer?.Stop();
            _timer = null;
        }
    }

    private static DispatcherTimer StartTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        return timer;
    }

    private static void Tick()
    {
        // One system-wide process snapshot per tick, shared by every pane;
        // enumerating processes per pane would multiply the cost by pane count.
        var entries = Vex.Terminal.ProcessTree.Snapshot();
        if (entries is null)
            return;
        // A copy: a pane may unregister mid-tick when its shell exits.
        foreach (var pane in Panes.ToArray())
            pane.RefreshAppIcon(entries);
    }
}
