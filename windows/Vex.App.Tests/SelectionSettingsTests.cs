using System.Text;
using System.Text.Json;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class SelectionSettingsTests
{
    [Fact]
    public void CopyOnSelect_OldProfilesKeepAutomaticCopyEnabled()
    {
        var settings = AppSettings.DeserializeSettings(Encoding.UTF8.GetBytes("{}"));
        Assert.True(settings.CopyOnSelect);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyOnSelect_PersistsThroughGeneratedJson(bool enabled)
    {
        var settings = new AppSettings { CopyOnSelect = enabled };
        var json = JsonSerializer.SerializeToUtf8Bytes(settings, VexJsonContext.Default.AppSettings);
        Assert.Equal(enabled, AppSettings.DeserializeSettings(json).CopyOnSelect);
    }

    [Fact]
    public void CopyOnSelect_NotifiesBindingsWhenChanged()
    {
        var settings = new AppSettings();
        var changes = new List<string?>();
        settings.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        settings.CopyOnSelect = false;
        settings.CopyOnSelect = false;
        Assert.Equal(new[] { nameof(AppSettings.CopyOnSelect) }, changes);
    }
}
