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

    [Fact]
    public void DeepestChildProcess_ReturnsNullForRootWithoutChildren()
    {
        var index = ProcessTree.Index.Build(new[]
        {
            (Pid: 10u, ParentPid: 1u, Name: "unknown-shell")
        });

        var child = ProcessTree.DeepestChildProcess(index ?? throw new InvalidOperationException("Expected a process index."), 10);

        Assert.Null(child);
    }

    [Fact]
    public void DeepestChildProcess_ReturnsApplicationWithoutKnowingShellName()
    {
        var index = ProcessTree.Index.Build(new[]
        {
            (Pid: 10u, ParentPid: 1u, Name: "unknown-shell"),
            (Pid: 20u, ParentPid: 10u, Name: "custom-editor")
        });

        var child = ProcessTree.DeepestChildProcess(index ?? throw new InvalidOperationException("Expected a process index."), 10);

        Assert.Equal(("custom-editor", 20u), child);
    }
}
