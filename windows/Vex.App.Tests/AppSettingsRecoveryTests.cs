using System.Text;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class AppSettingsRecoveryTests
{
    [Theory]
    [InlineData("{\"CustomShells\":null}")]
    [InlineData("{\"CustomShells\":[null]}")]
    [InlineData("{\"CustomShells\":[null,{\"Id\":null,\"Name\":null,\"Program\":null,\"Arguments\":null}]}")]
    public void DeserializeSettings_NullShellDataBecomesEmptyUsableList(string json)
    {
        var settings = AppSettings.DeserializeSettings(Encoding.UTF8.GetBytes(json));
        Assert.Empty(settings.CustomShells);
    }

    [Theory]
    [InlineData(-1, 8)]
    [InlineData(0, 8)]
    [InlineData(14, 14)]
    [InlineData(int.MaxValue, 72)]
    public void DeserializeSettings_InvalidFontMetricsAreBounded(int size, int expected)
    {
        var json = $"{{\"FontFamily\":null,\"FontSize\":{size}}}";
        var settings = AppSettings.DeserializeSettings(Encoding.UTF8.GetBytes(json));
        Assert.Equal("Cascadia Mono", settings.FontFamily);
        Assert.Equal(expected, settings.FontSize);
    }

    [Fact]
    public void DeserializeSettings_PreservesValidShellsAndDoesNotScheduleWrites()
    {
        var settings = AppSettings.DeserializeSettings(Encoding.UTF8.GetBytes("""
            {"ShellId":"custom","CustomShells":[null,{"Id":"custom","Name":"Shell","Program":"cmd.exe","Arguments":null}]}
            """));
        var shell = Assert.Single(settings.CustomShells);
        Assert.Equal("custom", settings.ShellId);
        Assert.Equal("cmd.exe", shell.Program);
        Assert.Equal("", shell.Arguments);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        Assert.Null(typeof(AppSettings).GetField("_saveDebouncer", flags)!.GetValue(settings));
    }
}
