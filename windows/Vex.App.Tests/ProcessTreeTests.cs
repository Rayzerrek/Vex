using Vex.Terminal;
using Xunit;

namespace Vex.App.Tests;

public sealed class ProcessTreeTests
{
    [Fact]
    public void Snapshot_ContainsCurrentProcess()
    {
        var entries = ProcessTree.Snapshot();

        Assert.NotNull(entries);
        var current = Assert.Single(entries, entry => entry.Pid == (uint)Environment.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(current.Name));
    }
}
