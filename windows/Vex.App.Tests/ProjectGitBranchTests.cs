using System.IO;
using System.Threading.Tasks;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class ProjectGitBranchTests
{
    [Fact]
    public async Task RefreshGitBranchAsync_ResolvesBranchFromGitHead()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vex_test_git_" + Path.GetRandomFileName());
        var gitDir = Path.Combine(tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/feature/sidebar\n");

        try
        {
            var project = new Project("Test", tempDir);
            await project.RefreshGitBranchAsync();

            Assert.Equal("feature/sidebar", project.GitBranch);
            Assert.True(project.HasGitBranch);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshGitBranchAsync_ResolvesDetachedHead()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vex_test_git_" + Path.GetRandomFileName());
        var gitDir = Path.Combine(tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "0123456789abcdef\n");

        try
        {
            var project = new Project("Test", tempDir);
            await project.RefreshGitBranchAsync();

            Assert.Equal("0123456", project.GitBranch);
            Assert.True(project.HasGitBranch);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshGitBranchAsync_NonGitDirectory_HasNullBranch()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vex_test_nongit_" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var project = new Project("Test", tempDir);
            await project.RefreshGitBranchAsync();

            Assert.Null(project.GitBranch);
            Assert.False(project.HasGitBranch);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshGitBranchAsync_FiresPropertyChangedEvents()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vex_test_events_" + Path.GetRandomFileName());
        var gitDir = Path.Combine(tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");

        try
        {
            var project = new Project("Test", tempDir);
            var branchChanged = false;
            var hasBranchChanged = false;
            project.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Project.GitBranch)) branchChanged = true;
                if (e.PropertyName == nameof(Project.HasGitBranch)) hasBranchChanged = true;
            };

            await project.RefreshGitBranchAsync();

            Assert.True(branchChanged);
            Assert.True(hasBranchChanged);
            Assert.Equal("main", project.GitBranch);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
