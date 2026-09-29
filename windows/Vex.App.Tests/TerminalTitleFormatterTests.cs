using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;
[Collection("CustomTheme")]
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
    [InlineData("main.rs [+] - NVIM", "main.rs", "neovim", true)]
    [InlineData("main.rs - NVIM", "main.rs", "neovim", false)]
    [InlineData("app.ts [+] - VIM", "app.ts", "vim", true)]
    [InlineData("app.ts - VIM", "app.ts", "vim", false)]
    [InlineData("[+] - NVIM", "neovim", "neovim", true)]
    [InlineData(@"file.txt [+] (C:\Users\code) - NVIM", "file.txt", "neovim", true)]
    [InlineData(@"file.txt (C:\Users\code) - NVIM", "file.txt", "neovim", false)]
    // Known editors
    [InlineData("main.rs * - Helix", "main.rs", "helix", true)]
    [InlineData("main.rs - Helix", "main.rs", "helix", false)]
    [InlineData("main.rs [*] - Helix", "main.rs", "helix", true)]
    [InlineData("code.py * - micro", "code.py", "micro", true)]
    [InlineData("code.py - micro", "code.py", "micro", false)]
    [InlineData("doc.txt [modified] - nano", "doc.txt", "nano", true)]
    [InlineData("doc.txt - nano", "doc.txt", "nano", false)]
    [InlineData("script.py ● - micro", "script.py", "micro", true)]
    [InlineData("Helix - main.rs [*]", "main.rs", "helix", true)]
    [InlineData("nano: file.txt *", "file.txt", "nano", true)]
    // Completely unknown/custom editors and tools
    [InlineData("main.rs * - kak", "main.rs", "kak", true)]
    [InlineData("main.rs - kak", "main.rs", "kak", false)]
    [InlineData("main.rs [+] - amp", "main.rs", "amp", true)]
    [InlineData("script.py ● - customeditor", "script.py", "customeditor", true)]
    [InlineData("doc.txt [modified] - randomtool", "doc.txt", "randomtool", true)]
    [InlineData("notes.txt (modified) - unknown_app", "notes.txt", "unknown_app", true)]
    [InlineData("kak: main.rs *", "main.rs", "kak", true)]
    [InlineData("mytool: notes.txt [modified]", "notes.txt", "mytool", true)]
    [InlineData("lapce - main.rs", "main.rs", "lapce", false)]
    public void UniversalParser_AllEditors_ExtractsModifiedState(string input, string expectedTabTitle, string expectedApp, bool expectedModified)
    {
        var parsed = TerminalTitleFormatter.Parse(input);
        Assert.Equal(expectedTabTitle, parsed.TabTitle);
        Assert.Equal(expectedApp, parsed.AppName);
        Assert.Equal(expectedModified, parsed.IsModified);
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

    [Fact]
    public void AppIconCatalog_ParseColor_NearWhiteMarks_DarkenedInLightAppearance()
    {
        // In dark mode, opencode and pi are near-white
        var darkOpencode = AppIconCatalog.ParseColor("#F1ECEC", isDark: true);
        var darkPi = AppIconCatalog.ParseColor("#F0EFEF", isDark: true);
        Assert.Equal(0xF1, darkOpencode.R);
        Assert.Equal(0xF0, darkPi.R);

        // In light mode, they map to deep ink/charcoal to avoid blending into white/light backgrounds
        var lightOpencode = AppIconCatalog.ParseColor("#F1ECEC", isDark: false);
        var lightPi = AppIconCatalog.ParseColor("#F0EFEF", isDark: false);
        Assert.Equal(0x24, lightOpencode.R);
        Assert.Equal(0x24, lightOpencode.G);
        Assert.Equal(0x29, lightOpencode.B);
        Assert.Equal(0x24, lightPi.R);
        Assert.True(FastColor.ContrastRatio(lightOpencode, System.Windows.Media.Colors.White) >= 14.0);
    }

    [Fact]
    public void AppIconCatalog_ParseColor_NearBlackMarks_PreservedInLightAppearance()
    {
        // In dark mode, near-black colors like bun, rust, github are lifted to #B8B8BE
        var darkBun = AppIconCatalog.ParseColor("#000000", isDark: true);
        Assert.Equal(0xB8, darkBun.R);
        Assert.Equal(0xB8, darkBun.G);
        Assert.Equal(0xBE, darkBun.B);

        // In light mode, they stay dark (#000000) with high contrast against white
        var lightBun = AppIconCatalog.ParseColor("#000000", isDark: false);
        Assert.Equal(0, lightBun.R);
        Assert.Equal(0, lightBun.G);
        Assert.Equal(0, lightBun.B);
        Assert.True(FastColor.ContrastRatio(lightBun, System.Windows.Media.Colors.White) >= 20.0);
    }

    [Theory]
    [InlineData("react", "#61DAFB")]
    [InlineData("javascript", "#F7DF1E")]
    [InlineData("vitest", "#00FF74")]
    [InlineData("linux", "#FCC624")]
    [InlineData("alacritty", "#F46D01")]
    public void AppIconCatalog_ParseColor_LightBrandColors_MeetContrastFloorInLightAppearance(string slug, string hex)
    {
        var lightColor = AppIconCatalog.ParseColor(hex, isDark: false);
        var contrast = FastColor.ContrastRatio(lightColor, System.Windows.Media.Colors.White);
        Assert.True(contrast >= 3.2, $"{slug} ({hex}) in light mode had contrast {contrast:F2}:1, expected >= 3.2:1");
    }

    [Fact]
    public void AppIcon_Glyph_ReturnsDifferentInstancesForLightAndDark()
    {
        var darkIcon = AppIcon.Glyph("opencode", isDark: true);
        var lightIcon = AppIcon.Glyph("opencode", isDark: false);

        Assert.NotNull(darkIcon);
        Assert.NotNull(lightIcon);
        Assert.NotSame(darkIcon, lightIcon);

        // Calling again returns the cached instance for that theme
        Assert.Same(darkIcon, AppIcon.Glyph("opencode", isDark: true));
        Assert.Same(lightIcon, AppIcon.Glyph("opencode", isDark: false));
    }

    [Fact]
    public void FastColor_ContrastRatio_AccurateAgainstKnownPairs()
    {
        var white = System.Windows.Media.Colors.White;
        var black = System.Windows.Media.Colors.Black;

        Assert.Equal(1.0, FastColor.RelativeLuminance(white), 3);
        Assert.Equal(0.0, FastColor.RelativeLuminance(black), 3);
        Assert.Equal(21.0, FastColor.ContrastRatio(white, black), 1);
        Assert.Equal(1.0, FastColor.ContrastRatio(white, white), 1);
    }

    [Fact]
    public void AppIcon_BuildGlyphImage_RendersValidBitmapAndExtractsBrushColor()
    {
        // 1. Verify brush colors on the actual WPF DrawingImage
        var lightOpenCode = AppIcon.Glyph("opencode", isDark: false);
        var darkOpenCode = AppIcon.Glyph("opencode", isDark: true);
        var lightBun = AppIcon.Glyph("bun", isDark: false);
        var darkBun = AppIcon.Glyph("bun", isDark: true);
        var lightReact = AppIcon.Glyph("react", isDark: false);
        var darkReact = AppIcon.Glyph("react", isDark: true);

        var lightOpenCodeBrush = (System.Windows.Media.SolidColorBrush)((System.Windows.Media.GeometryDrawing)((System.Windows.Media.DrawingGroup)lightOpenCode.Image.Drawing).Children[0]).Brush;
        var darkOpenCodeBrush = (System.Windows.Media.SolidColorBrush)((System.Windows.Media.GeometryDrawing)((System.Windows.Media.DrawingGroup)darkOpenCode.Image.Drawing).Children[0]).Brush;
        var lightBunBrush = (System.Windows.Media.SolidColorBrush)((System.Windows.Media.GeometryDrawing)((System.Windows.Media.DrawingGroup)lightBun.Image.Drawing).Children[0]).Brush;
        var darkBunBrush = (System.Windows.Media.SolidColorBrush)((System.Windows.Media.GeometryDrawing)((System.Windows.Media.DrawingGroup)darkBun.Image.Drawing).Children[0]).Brush;
        var lightReactBrush = (System.Windows.Media.SolidColorBrush)((System.Windows.Media.GeometryDrawing)((System.Windows.Media.DrawingGroup)lightReact.Image.Drawing).Children[0]).Brush;
        var darkReactBrush = (System.Windows.Media.SolidColorBrush)((System.Windows.Media.GeometryDrawing)((System.Windows.Media.DrawingGroup)darkReact.Image.Drawing).Children[0]).Brush;

        // OpenCode: charcoal in light, near-white in dark
        Assert.Equal(System.Windows.Media.Color.FromRgb(0x24, 0x24, 0x29), lightOpenCodeBrush.Color);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0xF1, 0xEC, 0xEC), darkOpenCodeBrush.Color);

        // Bun: black in light, light gray in dark
        Assert.Equal(System.Windows.Media.Color.FromRgb(0, 0, 0), lightBunBrush.Color);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0xB8, 0xB8, 0xBE), darkBunBrush.Color);

        // React: darkened cyan in light, original cyan in dark
        Assert.Equal(System.Windows.Media.Color.FromRgb(0x43, 0x98, 0xAF), lightReactBrush.Color);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0x61, 0xDA, 0xFB), darkReactBrush.Color);

        // 2. Render light and dark icons into actual RenderTargetBitmaps (smoke test for WPF rendering pipeline)
        foreach (var icon in new[] { lightOpenCode, darkOpenCode, lightBun, darkBun, lightReact, darkReact })
        {
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(icon.Image, new System.Windows.Rect(0, 0, 16, 16));
            }
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(16, 16, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(visual);
            Assert.Equal(16, rtb.PixelWidth);
            Assert.Equal(16, rtb.PixelHeight);
        }
    }

    [Fact]
    public void AppIcon_ThemeSwitchLifecycle_UpdatesPaneIconColor()
    {
        var settings = Vex.App.Model.AppSettings.Instance;
        var initialAppearance = settings.Appearance;
        try
        {
            // 1. Start in Dark appearance
            settings.SetAppearance(Vex.App.Model.AppSettings.DarkAppearance);
            var darkIcon = AppIconCatalog.ResolveIcon("opencode");
            Assert.NotNull(darkIcon);
            var darkBrush = (System.Windows.Media.SolidColorBrush)((System.Windows.Media.GeometryDrawing)((System.Windows.Media.DrawingGroup)darkIcon.Image.Drawing).Children[0]).Brush;
            Assert.Equal(System.Windows.Media.Color.FromRgb(0xF1, 0xEC, 0xEC), darkBrush.Color);

            // 2. Switch to Light appearance
            settings.SetAppearance(Vex.App.Model.AppSettings.LightAppearance);
            var lightIcon = AppIconCatalog.ResolveIcon("opencode");
            Assert.NotNull(lightIcon);
            var lightBrush = (System.Windows.Media.SolidColorBrush)((System.Windows.Media.GeometryDrawing)((System.Windows.Media.DrawingGroup)lightIcon.Image.Drawing).Children[0]).Brush;
            Assert.Equal(System.Windows.Media.Color.FromRgb(0x24, 0x24, 0x29), lightBrush.Color);

            // 3. Verify TerminalPane icon update on appearance flip
            var pane = new Vex.App.Model.TerminalPane(@"C:\test");
            pane.AppIcon = darkIcon;
            Assert.Same(darkIcon, pane.AppIcon);

            pane.ResetIconCache();
            pane.AppIcon = AppIconCatalog.ResolveIcon("opencode");
            Assert.Same(lightIcon, pane.AppIcon);
        }
        finally
        {
            settings.SetAppearance(initialAppearance);
        }
    }
}
