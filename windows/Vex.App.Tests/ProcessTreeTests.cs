using Vex.Terminal;
using Xunit;

namespace Vex.App.Tests;

public sealed class ProcessTreeTests
{
    [Theory]
    [InlineData("nvim", "node")]
    [InlineData("hx", "rust-analyzer")]
    [InlineData("emacs", "pyright")]
    public void ApplicationHost_KeepsIdentityInsteadOfChildServices(string editor, string service)
    {
        var index = Assert.IsType<ProcessTree.Index>(ProcessTree.Index.Build(new[]
        {
            (Pid: 10u, ParentPid: 0u, Name: "shell"),
            (Pid: 20u, ParentPid: 10u, Name: editor),
            (Pid: 30u, ParentPid: 20u, Name: service),
        }));
        IReadOnlySet<string> helpers = new HashSet<string>();
        IReadOnlySet<string> hosts = new HashSet<string> { editor };

        Assert.Equal((service, 30u), ProcessTree.DeepestDescendant(index, 10, helpers));
        Assert.Equal((editor, 20u), ProcessTree.DeepestDescendant(index, 10, helpers, hosts));
        Assert.Equal((editor, 20u), ProcessTree.DeepestChildProcess(index, 10, helpers, hosts));
    }

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
