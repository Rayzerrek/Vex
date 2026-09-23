using Vex.App;
using Xunit;

namespace Vex.App.Tests;

public sealed class WindowBackdropTests
{
    [Theory]
    [InlineData(10, 0, 19045, false)]
    [InlineData(10, 0, 22000, true)]
    [InlineData(10, 0, 22621, true)]
    public void SupportsModernAcrylic_UsesWindows11BuildBoundary(
        int major, int minor, int build, bool expected)
    {
        var version = new Version(major, minor, build);

        Assert.Equal(expected, WindowBackdrop.SupportsModernAcrylic(version));
    }
}
