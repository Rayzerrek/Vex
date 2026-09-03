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
    private readonly bool _leadingEdge;
    private readonly Timer _timer;
    private readonly object _lock = new();

    private bool _hasPendingTrailing;
    private bool _inWindow;
    private bool _disposed;

    public HalfDebouncer(TimeSpan window, Action action, bool leadingEdge = true)
    {
        _window = window;
        _action = action;
        _leadingEdge = leadingEdge;
        _timer = new Timer(OnTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Triggers the debounced action. When leadingEdge is true, the first
    /// invocation fires immediately; subsequent invocations within the window
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
                if (_leadingEdge)
                {
                    executeNow = true;
                }
                else
                {
                    _hasPendingTrailing = true;
                }
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
            try { _action(); }
            catch { /* Best effort: debounced action must never throw to caller */ }
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
            try { _action(); }
            catch { /* Best effort: background timer must never crash process */ }
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
            try { _action(); }
            catch { }
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

/// <summary>
/// Generic half-debouncer that captures and passes the latest payload <typeparamref name="T"/>
/// to the trailing-edge execution.
/// </summary>
public sealed class HalfDebouncer<T> : IDisposable
{
    private readonly TimeSpan _window;
    private readonly Action<T> _action;
    private readonly bool _leadingEdge;
    private readonly Timer _timer;
    private readonly object _lock = new();

    private T _latestValue = default!;
    private bool _hasPendingTrailing;
    private bool _inWindow;
    private bool _disposed;

    public HalfDebouncer(TimeSpan window, Action<T> action, bool leadingEdge = true)
    {
        _window = window;
        _action = action;
        _leadingEdge = leadingEdge;
        _timer = new Timer(OnTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Trigger(T value)
    {
        bool executeNow = false;

        lock (_lock)
        {
            if (_disposed)
                return;

            _latestValue = value;

            if (!_inWindow)
            {
                _inWindow = true;
                _hasPendingTrailing = false;
                if (_leadingEdge)
                {
                    executeNow = true;
                }
                else
                {
                    _hasPendingTrailing = true;
                }
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
            try { _action(value); }
            catch { }
        }
    }

    private void OnTimerElapsed(object? state)
    {
        T valueToExecute = default!;
        bool executeTrailing = false;

        lock (_lock)
        {
            if (_disposed)
                return;

            if (_hasPendingTrailing)
            {
                executeTrailing = true;
                _hasPendingTrailing = false;
                valueToExecute = _latestValue;
            }
            _inWindow = false;
        }

        if (executeTrailing)
        {
            try { _action(valueToExecute); }
            catch { }
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _hasPendingTrailing = false;
            _inWindow = false;
            _latestValue = default!;
        }
    }

    public void Flush()
    {
        T valueToExecute = default!;
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
                valueToExecute = _latestValue;
            }
            _inWindow = false;
        }

        if (execute)
        {
            try { _action(valueToExecute); }
            catch { }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _hasPendingTrailing = false;
            _inWindow = false;
            _latestValue = default!;
            _timer.Dispose();
        }
    }
}
