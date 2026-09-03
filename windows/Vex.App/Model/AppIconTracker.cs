using System.Threading;
using System.Windows.Threading;
using Vex.Terminal;

namespace Vex.App.Model;

/// <summary>
/// One shared background tracker polls every live pane's process tree so tab icons track
/// the app running inside each pane. Uses ThreadPool Timer and HalfDebouncer rather than
/// DispatcherTimer so it has zero UI-dispatch overhead and zero impact on application startup.
/// </summary>
public static class AppIconTracker
{
    private static readonly List<TerminalPane> Panes = new();
    private static readonly object Lock = new();
    private static Timer? _timer;
    private static int _tickInFlight;
    private static HalfDebouncer? _triggerDebouncer;

    public static void Register(TerminalPane pane)
    {
        lock (Lock)
        {
            Panes.Add(pane);
            _timer ??= StartTimer();
        }
        TriggerDebounced();
    }

    public static void Unregister(TerminalPane pane)
    {
        lock (Lock)
        {
            Panes.Remove(pane);
            if (Panes.Count == 0)
            {
                _timer?.Dispose();
                _timer = null;
                _triggerDebouncer?.Dispose();
                _triggerDebouncer = null;
            }
        }
    }

    public static void TriggerDebounced()
    {
        lock (Lock)
        {
            if (Panes.Count == 0)
                return;
            _triggerDebouncer ??= new HalfDebouncer(TimeSpan.FromMilliseconds(500), () => _ = TickAsync(), leadingEdge: false);
            _triggerDebouncer.Trigger();
        }
    }

    private static Timer StartTimer()
    {
        // 2500ms initial quiet period: allow the app to finish startup and initial
        // frame rendering completely before the first periodic process-tree snapshot.
        return new Timer(_ => _ = TickAsync(), null, TimeSpan.FromMilliseconds(2500), TimeSpan.FromSeconds(2.0));
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
            TerminalPane[] panesSnapshot;
            lock (Lock)
            {
                if (Panes.Count == 0)
                    return;
                panesSnapshot = Panes.ToArray();
            }

            var index = await Task.Run(() =>
            {
                var entries = ProcessTree.Snapshot();
                return entries is null ? null : ProcessTree.Index.Build(entries);
            });

            if (index is not null)
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    _ = dispatcher.BeginInvoke(() =>
                    {
                        foreach (var pane in panesSnapshot)
                            pane.RefreshAppIcon(index);
                    }, DispatcherPriority.Background);
                }
                else
                {
                    foreach (var pane in panesSnapshot)
                        pane.RefreshAppIcon(index);
                }
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
