using System.Threading;
using System.Windows.Threading;
using Vex.Terminal;

namespace Vex.App.Model;

/// <summary>
/// One shared background tracker reads process trees and shim command lines for
/// every live pane. The UI dispatcher only applies the completed icon snapshot.
/// </summary>
public static class AppIconTracker
{
    private static readonly List<TerminalPane> Panes = new();
    private static readonly object Lock = new();
    private static Timer? _timer;
    private static int _tickInFlight;
    private static HalfDebouncer? _triggerDebouncer;

    private sealed record IconSnapshot(
        ProcessTree.Index Index,
        IReadOnlyDictionary<uint, string?> CommandLines,
        long Timestamp);

    private static IconSnapshot? _latestSnapshot;
    /// <summary>Completed background process-tree snapshot used by close checks.</summary>
    public static ProcessTree.Index? LatestIndex => Volatile.Read(ref _latestSnapshot)?.Index;
    /// <summary>Monotonic completion timestamp in Environment.TickCount64 milliseconds.</summary>
    public static long LatestIndexTimestamp => Volatile.Read(ref _latestSnapshot)?.Timestamp ?? 0;

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

    /// <summary>
    /// Re-evaluates tab icons across all active panes on appearance or theme switch
    /// so icons re-tint to their light/dark contrast variants immediately.
    /// </summary>
    public static void OnThemeChanged()
    {
        TerminalPane[] panesSnapshot;
        lock (Lock)
        {
            if (Panes.Count == 0)
                return;
            panesSnapshot = Panes.ToArray();
        }

        foreach (var pane in panesSnapshot)
            pane.ResetIconCache();

        if (Volatile.Read(ref _latestSnapshot) is { } snapshot)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                _ = dispatcher.BeginInvoke(() =>
                {
                    foreach (var pane in panesSnapshot)
                        pane.RefreshAppIcon(snapshot.Index, snapshot.CommandLines);
                }, DispatcherPriority.Background);
            }
            else
            {
                foreach (var pane in panesSnapshot)
                    pane.RefreshAppIcon(snapshot.Index, snapshot.CommandLines);
            }
        }
        else
        {
            TriggerDebounced();
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

            var snapshot = await Task.Run(() =>
            {
                var entries = ProcessTree.Snapshot();
                var index = entries is null ? null : ProcessTree.Index.Build(entries);
                if (index is null)
                    return null;

                // Reading shim command lines opens other processes and walks
                // their memory. Keep that I/O off the UI dispatcher, including
                // theme changes that reuse the last completed snapshot.
                var commandLines = new Dictionary<uint, string?>();
                foreach (var pane in panesSnapshot)
                {
                    if (pane.ProcessId is not { } pid)
                        continue;
                    var process = ProcessTree.DeepestDescendant(index, (uint)pid, AppIconCatalog.ConsoleHelpers);
                    if (process is { } child && AppIconCatalog.IsShimHost(child.Name)
                        && !commandLines.ContainsKey(child.Pid))
                        commandLines.Add(child.Pid, ProcessCommandLine.Get(child.Pid));
                }
                return new IconSnapshot(index, commandLines, Environment.TickCount64);
            });

            Volatile.Write(ref _latestSnapshot, snapshot);
            if (snapshot is not null)
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    _ = dispatcher.BeginInvoke(() =>
                    {
                        foreach (var pane in panesSnapshot)
                            pane.RefreshAppIcon(snapshot.Index, snapshot.CommandLines);
                    }, DispatcherPriority.Background);
                }
                else
                {
                    foreach (var pane in panesSnapshot)
                        pane.RefreshAppIcon(snapshot.Index, snapshot.CommandLines);
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
