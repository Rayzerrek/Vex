using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class ShellChoiceListTests
{
    [Fact]
    public void LinuxChoices_DoNotCountLoginAliasesOrCompatibilityShellsAsExtraChoices()
    {
        var profiles = WslProjectLauncher.ParseShells("/bin/bash\n/usr/bin/bash\n/bin/sh\n/bin/dash\n");
        var choices = ShellChoiceList.Linux(profiles, "wsl");
        var choice = Assert.Single(choices);
        Assert.Equal("wsl", choice.Id);
        Assert.Equal("Bash", choice.Name);
        Assert.Equal("/bin/bash", choice.Program);
    }

    [Fact]
    public void LinuxChoices_PreserveAnExplicitProjectDefaultInsteadOfItsLoginAlias()
    {
        var profiles = WslProjectLauncher.ParseShells("/bin/bash\n/usr/bin/zsh\n");
        var choices = ShellChoiceList.Linux(profiles, "wsl:/bin/bash");
        Assert.Equal(new[] { "wsl:/bin/bash", "wsl:/usr/bin/zsh" }, choices.Select(profile => profile.Id));
    }

    [Fact]
    public void LinuxChoices_DoNotInventALoginShellWhenItIsUnavailable()
    {
        var profiles = WslProjectLauncher.ParseShells("\n/bin/bash\n");
        var choice = Assert.Single(ShellChoiceList.Linux(profiles, "wsl"));
        Assert.Equal("wsl:/bin/bash", choice.Id);
    }

    [Fact]
    public void LinuxChoices_KeepAnInstalledCompatibilityShellWhenItIsTheLoginShell()
    {
        var choice = Assert.Single(ShellChoiceList.Linux(WslProjectLauncher.ParseShells("/bin/sh\n/bin/sh\n/bin/dash\n"), "wsl"));
        Assert.Equal("wsl", choice.Id);
        Assert.Equal("sh", choice.Name);
    }

    [Fact]
    public void WindowsDefault_IsTheActualInstalledShellNotAnExtraOption()
    {
        var installed = new[] { new ShellProfile { Id = "cmd", Name = "Command Prompt", Program = @"C:\Windows\System32\cmd.exe", IsBuiltIn = true } };
        var choice = Assert.Single(ShellChoiceList.Windows(installed, "system", @"C:\Windows\System32\cmd.exe"));
        Assert.Equal("system", choice.Id);
        Assert.Equal("Command Prompt", choice.Name);
        Assert.Empty(ShellChoiceList.Windows([], "system", "cmd.exe"));
    }

    [Fact]
    public void WindowsChoices_ShowThePreferredPowerShellAndKeepLegacyShellsUnderMore()
    {
        var installed = new[] { "cmd", "powershell", "pwsh", "nu", "gitbash" }.Select(id => new ShellProfile { Id = id }).ToArray();
        var choices = ShellChoiceList.ForMenu(installed, "nu");
        Assert.Equal(new[] { "nu", "pwsh" }, choices.Primary.Select(profile => profile.Id));
        Assert.Equal(new[] { "cmd", "powershell", "gitbash" }, choices.More.Select(profile => profile.Id));
    }

    [Fact]
    public void ShellMenu_AtMostThreePrimaryChoicesAndNoMissingDefault()
    {
        var profiles = ShellChoiceList.Linux(WslProjectLauncher.ParseShells("/bin/bash\n/bin/zsh\n/bin/fish\n/bin/nu\n/bin/pwsh\n"), "wsl");
        var choices = ShellChoiceList.ForMenu(profiles, "wsl");
        Assert.Equal(3, choices.Primary.Count);
        Assert.Equal(2, choices.More.Count);
        Assert.Equal(profiles.Length, choices.Primary.Count + choices.More.Count);
        var fallback = ShellChoiceList.ForMenu([new ShellProfile { Id = "cmd" }], "missing");
        Assert.Equal("cmd", Assert.Single(fallback.Primary).Id);
        Assert.Empty(fallback.More);
    }
}
