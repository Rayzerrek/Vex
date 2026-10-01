using System.Buffers;
using System.IO;
using Vex.App.Terminal.Native;
using Vex.Terminal;
using Xunit;

namespace Vex.App.Tests;

[Collection("CustomTheme")]
public sealed class TerminalPrewarmBufferTests
{
    [Fact]
    public void BufferedOutput_RetainsOwnedBufferAndReplaysInOrder()
    {
        var slot = new TerminalSessionPrewarmer.Slot("C:\\buffer-test", "cmd");
        using var session = new TerminalSession();
        using var lease = new TerminalSessionPrewarmer.Lease(slot, Task.FromResult(session));
        var first = ArrayPool<byte>.Shared.Rent(64);
        first[3] = 12;
        var second = ArrayPool<byte>.Shared.Rent(64);
        second[0] = 34;
        TerminalSessionPrewarmer.OnOutput(slot, new ArraySegment<byte>(first, 3, 1));
        TerminalSessionPrewarmer.OnOutput(slot, new ArraySegment<byte>(second, 0, 1));
        Assert.Same(first, slot.BufferedOutput[0].Array);
        var received = new List<byte>();
        Assert.True(lease.AttachOutputHandler(data =>
        {
            received.Add(data.Array![data.Offset]);
            ArrayPool<byte>.Shared.Return(data.Array);
        }));
        Assert.Equal(new byte[] { 12, 34 }, received);
        Assert.Empty(slot.BufferedOutput);
        Assert.Equal(0, slot.BufferedByteCount);
    }

    [Fact]
    public void FailedReplay_DoesNotReturnTransferredBuffersAgain()
    {
        var slot = new TerminalSessionPrewarmer.Slot("C:\\replay-test", "cmd");
        var session = new TerminalSession();
        slot.Session = session;
        using var lease = new TerminalSessionPrewarmer.Lease(slot, Task.FromResult(session));
        TerminalSessionPrewarmer.OnOutput(slot, new ArraySegment<byte>(ArrayPool<byte>.Shared.Rent(64)));
        TerminalSessionPrewarmer.OnOutput(slot, new ArraySegment<byte>(ArrayPool<byte>.Shared.Rent(64)));
        Assert.Throws<InvalidOperationException>(() => lease.AttachOutputHandler(data =>
        {
            ArrayPool<byte>.Shared.Return(data.Array!);
            throw new InvalidOperationException("replay failure");
        }));
        Assert.Null(slot.BufferedOutput[0].Array);
        Assert.NotNull(slot.BufferedOutput[1].Array);
        lease.Dispose();
        Assert.Empty(slot.BufferedOutput);
        Assert.Equal(0, slot.BufferedByteCount);
    }

    [Fact]
    public async Task LiveOutputFlood_InvalidatesAndClosesSessionWithoutReaderDeadlock()
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var slot = new TerminalSessionPrewarmer.Slot(directory, "cmd");
        var session = new TerminalSession();
        slot.Session = session;
        using var lease = new TerminalSessionPrewarmer.Lease(slot, Task.FromResult(session));
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OutputReceived += data => TerminalSessionPrewarmer.OnOutput(slot, data);
        session.Exited += code => exited.TrySetResult(code);
        try
        {
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            session.Start(directory, 80, 24, shell,
                "/d /c for /L %i in (1,1,100000) do @echo vex-prewarm-output-flood-012345678901234567890123456789");
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(slot.Invalidated);
            Assert.Empty(slot.BufferedOutput);
            Assert.False(lease.AttachOutputHandler(_ => Assert.Fail("Overflowed session must cold-start")));
            Assert.Null(TerminalSessionPrewarmer.Take(directory, "cmd"));
        }
        finally { TerminalSessionPrewarmer.Dispose(); }
    }

    [Fact]
    public async Task BufferOverflow_InvalidatesWholeStreamAndDisablesMatchingPrewarm()
    {
        const string directory = "C:\\overflow-test";
        var slot = new TerminalSessionPrewarmer.Slot(directory, "cmd");
        using var session = new TerminalSession();
        using var lease = new TerminalSessionPrewarmer.Lease(slot, Task.FromResult(session));
        try
        {
            // The retained pool capacity, not the tiny payload, consumes the budget.
            for (var i = 0; i < TerminalSessionPrewarmer.MaxBufferedOutputBytes / 65536; i++)
                TerminalSessionPrewarmer.OnOutput(slot, new ArraySegment<byte>(ArrayPool<byte>.Shared.Rent(65536), 0, 1));
            Assert.False(slot.Invalidated);
            Assert.Equal(TerminalSessionPrewarmer.MaxBufferedOutputBytes, slot.BufferedByteCount);
            TerminalSessionPrewarmer.OnOutput(slot, new ArraySegment<byte>(ArrayPool<byte>.Shared.Rent(65536), 0, 1));
            Assert.True(slot.Invalidated);
            Assert.Empty(slot.BufferedOutput);
            Assert.Equal(0, slot.BufferedByteCount);
            Assert.False(lease.AttachOutputHandler(_ => Assert.Fail("A truncated VT stream must never be replayed")));
            TerminalSessionPrewarmer.StartPrewarm(directory, "cmd");
            Assert.Null(TerminalSessionPrewarmer.Take(directory, "cmd"));
            // Output arriving after invalidation is discarded without growing the buffer.
            TerminalSessionPrewarmer.OnOutput(slot, new ArraySegment<byte>(ArrayPool<byte>.Shared.Rent(65536)));
            Assert.Empty(slot.BufferedOutput);

            var changedDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            TerminalSessionPrewarmer.StartPrewarm(changedDirectory, "cmd");
            using var changedLease = TerminalSessionPrewarmer.Take(changedDirectory, "cmd");
            Assert.NotNull(changedLease);
            await changedLease.SessionTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { TerminalSessionPrewarmer.Dispose(); }
    }
}
