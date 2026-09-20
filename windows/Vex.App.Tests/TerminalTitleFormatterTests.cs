using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class TerminalTitleFormatterTests
{
    [Theory]
    [InlineData("bun pi > vex", "pi")]
    [InlineData("bun pi >> vex.log", "pi")]
    [InlineData("bun pi 2>&1", "pi")]
    [InlineData("bun pi | grep error", "pi")]
    [InlineData("bun run pi", "pi")]
    [InlineData("bun x pi", "pi")]
    [InlineData("bunx pi", "pi")]
    [InlineData("npx pi", "pi")]
    [InlineData("pnpm dlx pi", "pi")]
    [InlineData("pnpm exec pi", "pi")]
    [InlineData("pnpm pi", "pi")]
    public void Format_RunnerWithRedirection_ExtractsTargetProgram(string input, string expected)
    {
        var result = TerminalTitleFormatter.Format(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("OpenCode - nazwa sesji", "nazwa sesji")]
    [InlineData("OC - nazwa sesji", "nazwa sesji")]
    [InlineData("opencode - nazwa sesji", "nazwa sesji")]
    [InlineData("open-code - nazwa sesji", "nazwa sesji")]
    [InlineData("OpenCode: nazwa sesji", "nazwa sesji")]
    [InlineData("OC: nazwa sesji", "nazwa sesji")]
    [InlineData("OpenCode | nazwa sesji", "nazwa sesji")]
    [InlineData("OC | nazwa sesji", "nazwa sesji")]
    [InlineData("[*] Working | Session Title", "Session Title")]
    [InlineData("[✓] Done | Session Title", "Session Title")]
    public void Format_OpenCodeSession_PreservesSessionName(string input, string expected)
    {
        var result = TerminalTitleFormatter.Format(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("OpenCode", "OpenCode")]
    [InlineData("open-code", "OpenCode")]
    [InlineData("OC", "OpenCode")]
    [InlineData("opencode", "OpenCode")]
    public void Format_BareOpenCode_ReturnsOpenCode(string input, string expected)
    {
        var result = TerminalTitleFormatter.Format(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("agy", "agy")]
    [InlineData("antigravity", "antigravity")]
    [InlineData("agy > output.log", "agy")]
    public void Format_AntigravityCli_ExtractsName(string input, string expected)
    {
        var result = TerminalTitleFormatter.Format(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe - bun pi > vex", "pi")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe - bun pi > vex", "pi")]
    [InlineData("pwsh - bun pi > vex", "pi")]
    [InlineData("powershell - agy", "agy")]
    [InlineData("cmd.exe - agy", "agy")]
    [InlineData("Administrator: pwsh - agy", "agy")]
    [InlineData("Administrator: Windows PowerShell - OC - sesja", "sesja")]
    public void Format_ShellAndAdminPrefixes_AreStripped(string input, string expected)
    {
        var result = TerminalTitleFormatter.Format(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("main.rs - NVIM", "main.rs")]
    [InlineData("app.ts - VIM", "app.ts")]
    [InlineData("git commit -m \"msg\"", "git")]
    [InlineData("git status", "git")]
    [InlineData("cargo test", "cargo")]
    [InlineData("cargo run --bin myapp", "myapp")]
    [InlineData("python app.py", "app")]
    [InlineData("python", "python")]
    [InlineData("bun", "bun")]
    [InlineData("Terminal", "Terminal")]
    [InlineData(@"C:\Users\Ziut\code\vex", "vex")]
    [InlineData("deno run cli.ts > out", "cli")]
    [InlineData("CustomTool - Task 42", "Task 42")]
    public void Format_GeneralCommandsAndPaths_FormatCleanly(string input, string expected)
    {
        var result = TerminalTitleFormatter.Format(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void AppIconCatalog_AgyAndAntigravity_ResolvesToGemini()
    {
        var fromAgyTitle = AppIconCatalog.FromTitle("agy");
        Assert.NotNull(fromAgyTitle);

        var fromAntigravityTitle = AppIconCatalog.FromTitle("antigravity");
        Assert.NotNull(fromAntigravityTitle);

        var fromProcess = AppIconCatalog.Resolve("agy", null, null);
        Assert.NotNull(fromProcess);

        var fromAntigravityProcess = AppIconCatalog.Resolve("antigravity", null, null);
        Assert.NotNull(fromAntigravityProcess);

        var geminiIcon = AppIcon.Glyph("googlegemini");
        Assert.Same(geminiIcon, fromAgyTitle);
        Assert.Same(geminiIcon, fromAntigravityTitle);
        Assert.Same(geminiIcon, fromProcess);
        Assert.Same(geminiIcon, fromAntigravityProcess);
    }

    [Fact]
    public void AppIconCatalog_OpenCodeAndOc_ResolvesToOpenCode()
    {
        var fromTitle = AppIconCatalog.FromTitle("OpenCode - nazwa sesji");
        Assert.NotNull(fromTitle);

        var fromOcTitle = AppIconCatalog.FromTitle("OC - nazwa sesji");
        Assert.NotNull(fromOcTitle);

        var fromOcProcess = AppIconCatalog.Resolve("oc", null, null);
        Assert.NotNull(fromOcProcess);

        var fromOpenCodeProcess = AppIconCatalog.Resolve("opencode", null, null);
        Assert.NotNull(fromOpenCodeProcess);

        var opencodeIcon = AppIcon.Glyph("opencode");
        Assert.Same(opencodeIcon, fromTitle);
        Assert.Same(opencodeIcon, fromOcTitle);
        Assert.Same(opencodeIcon, fromOcProcess);
        Assert.Same(opencodeIcon, fromOpenCodeProcess);
    }

    [Fact]
    public void AppIconCatalog_BunRunningPi_ResolvesToPiIcon()
    {
        var fromTitle = AppIconCatalog.FromTitle("bun pi > vex");
        Assert.NotNull(fromTitle);

        var piIcon = AppIcon.Glyph("pi");
        Assert.Same(piIcon, fromTitle);

        var fromCommandLine = AppIconCatalog.Resolve("bun", "bun pi > vex", "bun pi > vex");
        Assert.NotNull(fromCommandLine);
        Assert.Same(piIcon, fromCommandLine);

        var toolName = AppIconCatalog.ResolveToolName("bun", "bun pi > vex");
        Assert.Equal("pi", toolName);
    }

    [Fact]
    public void AppIconCatalog_BareBun_ResolvesToBunIcon()
    {
        var bunIcon = AppIcon.Glyph("bun");
        var resolved = AppIconCatalog.Resolve("bun", null, null);
        Assert.NotNull(resolved);
        Assert.Same(bunIcon, resolved);
    }

    [Fact]
    public void UniversalParser_CustomToolWithSession_ExtractsBoth()
    {
        var parsed = TerminalTitleFormatter.Parse("CustomAgent - Feature Login");
        Assert.Equal("Feature Login", parsed.TabTitle);
        Assert.Equal("CustomAgent", parsed.AppName);
    }

    [Theory]
    [InlineData("docker ps", "docker")]
    [InlineData("git status", "git")]
    [InlineData("main.rs - NVIM", "neovim")]
    [InlineData("config.lua - LazyVim", "lazyvim")]
    [InlineData("python app.py", "python")]
    public void AppIconCatalog_UniversalResolution_ResolvesKnownBrandIcons(string input, string expectedGlyph)
    {
        var icon = AppIconCatalog.FromTitle(input);
        Assert.NotNull(icon);
        Assert.Same(AppIcon.Glyph(expectedGlyph), icon);
    }
}
