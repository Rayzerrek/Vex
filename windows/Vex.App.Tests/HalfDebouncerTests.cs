using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

/// <summary>
/// The debouncer guards on-disk persistence for settings and session state.
/// The contract that matters: one leading execution, a coalesced trailing
/// execution, and an explicit flush so a closing window never loses the last
/// edit.
///
/// Tests that only care about coalescing use <see cref="QuietWindow"/>, long
/// enough that the timer cannot fire while the test runs, and drive the
/// trailing edge with <see cref="HalfDebouncer.Flush"/>. Waiting on a real
/// timer made these tests fail on loaded CI runners, where the callback was
/// delayed past the timeout.
/// </summary>
public sealed class HalfDebouncerTests
{
    /// <summary>Long enough that no timer fires during a test body.</summary>
    private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(30);

    /// <summary>Short enough to keep the one timer-driven test fast.</summary>
    private static readonly TimeSpan TimerWindow = TimeSpan.FromMilliseconds(60);

    [Fact]
    public void LeadingEdge_FiresImmediately()
    {
        var count = 0;
        using var debouncer = new HalfDebouncer(QuietWindow, () => Interlocked.Increment(ref count));

        debouncer.Trigger();

        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void Burst_CollapsesIntoOneTrailingExecution()
    {
        var count = 0;
        using var debouncer = new HalfDebouncer(QuietWindow, () => Interlocked.Increment(ref count));

        for (var i = 0; i < 10; i++)
            debouncer.Trigger();

        // The burst produced exactly one leading execution. Further calls were
        // coalesced rather than run eagerly.
        Assert.Equal(1, Volatile.Read(ref count));

        debouncer.Flush();

        // One trailing execution carries the collapsed burst, not ten.
        Assert.Equal(2, Volatile.Read(ref count));
    }

    [Fact]
    public void LeadingEdge_AloneDoesNotScheduleATrailingRun()
    {
        // With leadingEdge: true a lone trigger is fully served by the leading
        // execution; only a second trigger inside the window marks trailing
        // work as pending. This is what makes the first keystroke instant.
        var count = 0;
        using var debouncer = new HalfDebouncer(TimerWindow, () => Interlocked.Increment(ref count));

        debouncer.Trigger();
        Assert.Equal(1, Volatile.Read(ref count));

        Thread.Sleep(TimerWindow * 5);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void TrailingEdge_FiresAfterTheWindowWhenATriggerWasCoalesced()
    {
        // The one test that waits on the real timer. Two triggers are needed to
        // create pending trailing work, so it gets a wide timeout: the runner
        // can delay the callback well past the 60 ms window.
        var count = 0;
        using var debouncer = new HalfDebouncer(TimerWindow, () => Interlocked.Increment(ref count));

        debouncer.Trigger();
        debouncer.Trigger();
        Assert.Equal(1, Volatile.Read(ref count));

        Assert.True(
            SpinWaitUntil(() => Volatile.Read(ref count) == 2),
            $"trailing execution did not run; count was {Volatile.Read(ref count)}");
    }

    [Fact]
    public void TrailingDisabled_FiresOnlyOnFlush()
    {
        var count = 0;
        using var debouncer = new HalfDebouncer(QuietWindow, () => Interlocked.Increment(ref count), leadingEdge: false);

        debouncer.Trigger();
        Assert.Equal(0, Volatile.Read(ref count));

        debouncer.Flush();
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void Cancel_DropsPendingTrailingExecution()
    {
        var count = 0;
        using var debouncer = new HalfDebouncer(TimerWindow, () => Interlocked.Increment(ref count), leadingEdge: false);

        debouncer.Trigger();
        debouncer.Cancel();

        // Comfortably longer than the window: a pending trailing run would
        // have happened by now.
        Thread.Sleep(TimerWindow * 5);
        Assert.Equal(0, Volatile.Read(ref count));
    }

    [Fact]
    public void Flush_IsIdempotent()
    {
        var count = 0;
        using var debouncer = new HalfDebouncer(QuietWindow, () => Interlocked.Increment(ref count), leadingEdge: false);

        debouncer.Trigger();
        debouncer.Flush();
        debouncer.Flush();

        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void Generic_PassesTheLatestValueToTrailingExecution()
    {
        var seen = new List<int>();
        using var debouncer = new HalfDebouncer<int>(QuietWindow, v => { lock (seen) seen.Add(v); });

        debouncer.Trigger(1);
        debouncer.Trigger(2);
        debouncer.Trigger(3);
        debouncer.Flush();

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
            QuietWindow,
            () => throw new InvalidOperationException("debounced work failed"));

        // A settings write failure must never tear down the UI thread.
        debouncer.Trigger();
        debouncer.Flush();
    }

    [Fact]
    public void Trigger_AfterDispose_IsIgnored()
    {
        var count = 0;
        var debouncer = new HalfDebouncer(QuietWindow, () => Interlocked.Increment(ref count));
        debouncer.Dispose();

        debouncer.Trigger();
        debouncer.Flush();

        Assert.Equal(0, Volatile.Read(ref count));
    }

    private static bool SpinWaitUntil(Func<bool> condition, int timeoutMs = 10_000)
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
