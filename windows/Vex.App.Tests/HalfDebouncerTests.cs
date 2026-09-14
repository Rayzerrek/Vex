using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

/// <summary>
/// The debouncer guards on-disk persistence for settings and session state.
/// The contract that matters: one leading execution, coalesced trailing
/// execution, and an explicit flush so a closing window never loses the last
/// edit.
/// </summary>
public sealed class HalfDebouncerTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(60);

    [Fact]
    public void LeadingEdge_FiresImmediately()
    {
        var count = 0;
        using var debouncer = new HalfDebouncer(Window, () => Interlocked.Increment(ref count));

        debouncer.Trigger();

        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void Burst_CollapsesIntoOneTrailingExecution()
    {
        var count = 0;
        using var debouncer = new HalfDebouncer(Window, () => Interlocked.Increment(ref count));

        for (var i = 0; i < 10; i++)
            debouncer.Trigger();
        Assert.Equal(1, Volatile.Read(ref count));

        Assert.True(SpinWaitUntil(() => Volatile.Read(ref count) == 2));
    }

    [Fact]
    public void TrailingDisabled_FiresOnlyOnFlush()
    {
        var count = 0;
        using var debouncer = new HalfDebouncer(Window, () => Interlocked.Increment(ref count), leadingEdge: false);

        debouncer.Trigger();
        Assert.Equal(0, Volatile.Read(ref count));

        debouncer.Flush();
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void Cancel_DropsPendingTrailingExecution()
    {
        var count = 0;
        using var debouncer = new HalfDebouncer(Window, () => Interlocked.Increment(ref count), leadingEdge: false);

        debouncer.Trigger();
        debouncer.Cancel();

        Thread.Sleep(Window * 3);
        Assert.Equal(0, Volatile.Read(ref count));
    }

    [Fact]
    public void Flush_IsIdempotent()
    {
        var count = 0;
        using var debouncer = new HalfDebouncer(Window, () => Interlocked.Increment(ref count), leadingEdge: false);

        debouncer.Trigger();
        debouncer.Flush();
        debouncer.Flush();

        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void Generic_PassesTheLatestValueToTrailingExecution()
    {
        var seen = new List<int>();
        using var debouncer = new HalfDebouncer<int>(Window, v => { lock (seen) seen.Add(v); });

        debouncer.Trigger(1);
        debouncer.Trigger(2);
        debouncer.Trigger(3);

        Assert.True(SpinWaitUntil(() =>
        {
            lock (seen) return seen.Count == 2;
        }));
        lock (seen)
        {
            // Leading edge carries the first value; trailing carries the last.
            Assert.Equal(new[] { 1, 3 }, seen);
        }
    }

    [Fact]
    public void ThrowingAction_DoesNotPropagateToCaller()
    {
        using var debouncer = new HalfDebouncer(
            Window,
            () => throw new InvalidOperationException("debounced work failed"));

        // A settings write failure must never tear down the UI thread.
        debouncer.Trigger();
        debouncer.Flush();
    }

    [Fact]
    public void Trigger_AfterDispose_IsIgnored()
    {
        var count = 0;
        var debouncer = new HalfDebouncer(Window, () => Interlocked.Increment(ref count));
        debouncer.Dispose();

        debouncer.Trigger();
        debouncer.Flush();

        Assert.Equal(0, Volatile.Read(ref count));
    }

    private static bool SpinWaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(5);
        }
        return condition();
    }
}
