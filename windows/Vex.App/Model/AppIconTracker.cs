using System.Threading;
using System.Windows.Threading;
using Vex.Terminal;

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
    private static int _tickInFlight;

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

    private static void Tick() => _ = TickAsync();

    private static async Task TickAsync()
    {
        // The system-wide snapshot is the expensive part of a cycle; run it
        // off the UI thread so its periodic process enumeration never hitches
        // typing or rendering. Skip when the previous cycle is still running.
        if (Interlocked.CompareExchange(ref _tickInFlight, 1, 0) != 0)
            return;

        try
        {
            var index = await Task.Run(() =>
            {
                var entries = ProcessTree.Snapshot();
                return entries is null ? null : ProcessTree.Index.Build(entries);
            });

            if (index is not null)
            {
                // A copy: a pane may unregister mid-tick when its shell exits.
                foreach (var pane in Panes.ToArray())
                    pane.RefreshAppIcon(index);
            }
        }
        catch
        {
            // Best-effort icon tracking: errors must never interrupt or crash the UI.
        }
        finally
        {
            Interlocked.Exchange(ref _tickInFlight, 0);
        }
    }
}
