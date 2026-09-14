using System.Text.RegularExpressions;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

/// <summary>
/// The displayed version is derived from assembly metadata so a release only
/// bumps &lt;Version&gt; in the csproj. These tests guard the stripping of the
/// source-revision suffix that the SDK appends to InformationalVersion.
/// </summary>
public sealed partial class AppInfoTests
{
    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex SemanticVersion();

    [Fact]
    public void Version_IsSemanticWithoutBuildMetadata()
    {
        // The SDK writes "1.2.1+<sha>"; the UI must show "1.2.1".
        Assert.DoesNotContain('+', AppInfo.Version);
        Assert.Matches(SemanticVersion(), AppInfo.Version);
    }

    [Fact]
    public void VersionLabel_PrefixesTheProductName()
    {
        Assert.Equal($"Vex {AppInfo.Version}", AppInfo.VersionLabel);
    }
}
