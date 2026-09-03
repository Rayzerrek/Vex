using System.Threading;

namespace Vex.App.Model;

/// <summary>
/// High-performance half-debouncer (leading + trailing debounce):
/// - Leading edge: The first trigger executes immediately (zero latency).
/// - Coalescing: Subsequent triggers within the time window are debounced.
/// - Trailing edge: When calls stop and the quiet period elapses, the trailing
///   execution runs to process the latest state.
///
/// Uses <see cref="Timer"/> from the thread pool rather than WPF's
/// DispatcherTimer so it has zero thread affinity, incurs zero UI-dispatch
/// overhead, and has zero impact on application startup time.
/// </summary>
public sealed class HalfDebouncer : IDisposable
{
    private readonly TimeSpan _window;
    private readonly Action _action;
    private readonly Timer _timer;
    private readonly object _lock = new();

    private bool _hasPendingTrailing;
    private bool _inWindow;
    private bool _disposed;

    public HalfDebouncer(TimeSpan window, Action action)
    {
        _window = window;
        _action = action;
        _timer = new Timer(OnTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Triggers the debounced action. The first invocation fires
    /// immediately (leading edge); subsequent invocations within the window
    /// are coalesced into a single trailing execution once activity ceases.</summary>
    public void Trigger()
    {
        bool executeNow = false;

        lock (_lock)
        {
            if (_disposed)
                return;

            if (!_inWindow)
            {
                _inWindow = true;
                _hasPendingTrailing = false;
                executeNow = true;
                _timer.Change(_window, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _hasPendingTrailing = true;
                _timer.Change(_window, Timeout.InfiniteTimeSpan);
            }
        }

        if (executeNow)
        {
            _action();
        }
    }

    private void OnTimerElapsed(object? state)
    {
        bool executeTrailing = false;

        lock (_lock)
        {
            if (_disposed)
                return;

            if (_hasPendingTrailing)
            {
                executeTrailing = true;
                _hasPendingTrailing = false;
            }
            _inWindow = false;
        }

        if (executeTrailing)
        {
            _action();
        }
    }

    /// <summary>Cancels any pending trailing execution and resets the window.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _hasPendingTrailing = false;
            _inWindow = false;
        }
    }

    /// <summary>Immediately executes any pending trailing action and closes the window.</summary>
    public void Flush()
    {
        bool execute = false;

        lock (_lock)
        {
            if (_disposed)
                return;

            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            if (_hasPendingTrailing)
            {
                execute = true;
                _hasPendingTrailing = false;
            }
            _inWindow = false;
        }

        if (execute)
        {
            _action();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _hasPendingTrailing = false;
            _inWindow = false;
            _timer.Dispose();
        }
    }
}
