using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace Vex.App.Model;

/// <summary>
/// Maps the app running in a pane (process tree, shim command line, terminal
/// title) to a tab icon. The process name is the source of truth: shells are
/// skipped, so the deepest non-shell process is the app. Node-hosted CLIs are
/// identified by the script in their command line, and the OSC title is a
/// fallback for everything the process tree cannot see (WSL, builtins).
/// Unknown real apps get a letter badge; unidentifiable shims get nothing so
/// a stale generic badge never replaces the current icon.
/// </summary>
internal static partial class AppIconCatalog
{
    /// <summary>Process names that are just the shell, never the tab's app.</summary>
    internal static readonly IReadOnlySet<string> ExcludedShells = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pwsh", "powershell", "cmd", "nu", "bash", "sh", "zsh", "fish", "conhost", "wslhost",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Hosts that run other apps (node claude.js, wsl nvim, ...); their own
    /// name never identifies the app, only their command line or the OSC
    /// title does — the process tree cannot see inside a script or a VM.
    /// </summary>
    private static readonly FrozenSet<string> ShimHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "node", "nodejs", "deno", "wsl", "bunx", "tsx", "ts-node",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Command-line path segments that say nothing about the real app
    /// (global npm layouts, launcher dirs); skipped when scanning shims.
    /// </summary>
    private static readonly FrozenSet<string> CommandLineNoise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "npm", "npx", "node_modules", "bin", "lib", "cli", "scripts", "cmd",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, Func<AppIcon>> ProcessIconFactories =
        new Dictionary<string, Func<AppIcon>>(StringComparer.OrdinalIgnoreCase)
        {
            // Editors, terminals, shells and git tooling.
            ["nvim"] = () => AppIcon.Glyph("neovim"),
            ["vim"] = () => AppIcon.Glyph("vim"),
            ["git"] = () => AppIcon.Glyph("git"),
            ["lazygit"] = () => AppIcon.Badge("lg", Color.FromRgb(0x00, 0xAA, 0xDD)),
            ["htop"] = () => AppIcon.Glyph("htop"),
            ["btop"] = () => AppIcon.Badge("bt", Color.FromRgb(0x00, 0xA8, 0x96)),
            ["tmux"] = () => AppIcon.Glyph("tmux"),
            ["ssh"] = () => AppIcon.Badge("S", Color.FromRgb(0x2B, 0x8C, 0xBE)),
            ["hx"] = () => AppIcon.Glyph("helix"),
            ["ghostty"] = () => AppIcon.Glyph("ghostty"),
            ["wezterm"] = () => AppIcon.Glyph("wezterm"),
            ["alacritty"] = () => AppIcon.Glyph("alacritty"),
            ["starship"] = () => AppIcon.Glyph("starship"),

            // Agent CLIs: brand marks where available, letter badges otherwise.
            ["claude"] = () => AppIcon.Glyph("claudecode"),
            ["pi"] = () => AppIcon.Glyph("pi"),
            ["antigravity"] = () => AppIcon.Glyph("antigravity"),
            ["claude-code"] = () => AppIcon.Glyph("claudecode"),
            ["claudecode"] = () => AppIcon.Glyph("claudecode"),
            ["codex"] = () => AppIcon.Glyph("codex"),
            ["opencode"] = () => AppIcon.Glyph("opencode"),
            ["deepseek"] = () => AppIcon.Glyph("deepseek"),
            ["qwen"] = () => AppIcon.Glyph("qwen"),
            ["aider"] = () => AppIcon.Badge("ai", Color.FromRgb(0x8B, 0x5C, 0xF6)),
            ["gemini"] = () => AppIcon.Glyph("googlegemini"),

            // Runtimes and package managers.
            ["node"] = () => AppIcon.Glyph("nodedotjs"),
            ["npm"] = () => AppIcon.Glyph("npm"),
            ["pnpm"] = () => AppIcon.Glyph("pnpm"),
            ["bun"] = () => AppIcon.Glyph("bun"),
            ["python"] = () => AppIcon.Glyph("python"),
            ["python3"] = () => AppIcon.Glyph("python"),
            ["tsx"] = () => AppIcon.Glyph("typescript"),
            ["deno"] = () => AppIcon.Glyph("typescript"),

            // Ops tooling.
            ["docker"] = () => AppIcon.Glyph("docker"),
            ["gh"] = () => AppIcon.Glyph("github"),
            ["kubectl"] = () => AppIcon.Glyph("kubernetes"),
            ["rustc"] = () => AppIcon.Glyph("rust"),
            ["cargo"] = () => AppIcon.Glyph("rust"),
            ["go"] = () => AppIcon.Glyph("go"),
            ["terraform"] = () => AppIcon.Glyph("terraform"),
            ["ansible-playbook"] = () => AppIcon.Glyph("ansible"),
            ["nginx"] = () => AppIcon.Glyph("nginx"),
            ["gitlab-runner"] = () => AppIcon.Glyph("gitlab"),
            ["gcloud"] = () => AppIcon.Glyph("googlecloud"),
            ["psql"] = () => AppIcon.Glyph("postgresql"),
            ["mysql"] = () => AppIcon.Glyph("mysql"),
            ["redis-cli"] = () => AppIcon.Glyph("redis"),
            ["sqlite3"] = () => AppIcon.Glyph("sqlite"),

            // Shells themselves (shown when nothing else is running).
            ["fish"] = () => AppIcon.Glyph("fishshell"),
            ["bash"] = () => AppIcon.Glyph("gnubash"),
            ["sh"] = () => AppIcon.Glyph("gnubash"),
            ["zsh"] = () => AppIcon.Glyph("zsh"),
            ["nu"] = () => AppIcon.Glyph("nushell"),
            ["pwsh"] = () => AppIcon.Glyph("powershell"),
            ["powershell"] = () => AppIcon.Glyph("powershell"),
            ["cmd"] = () => AppIcon.Badge(">", Color.FromRgb(0x00, 0x78, 0xD4)),
            ["wsl"] = () => AppIcon.Badge("W", HashColor("wsl")),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly (Regex Pattern, Func<AppIcon> Factory)[] TitleRules =
    {
        (TitleNvim(), () => AppIcon.Glyph("neovim")),
        (TitleLazyvim(), () => AppIcon.Glyph("lazyvim")),
        (TitleVim(), () => AppIcon.Glyph("vim")),
        (TitleLazygit(), () => AppIcon.Badge("lg", Color.FromRgb(0x00, 0xAA, 0xDD))),
        (TitleHtop(), () => AppIcon.Glyph("htop")),
        (TitleBtop(), () => AppIcon.Badge("bt", Color.FromRgb(0x00, 0xA8, 0x96))),
        (TitleTmux(), () => AppIcon.Glyph("tmux")),
        (TitleClaude(), () => AppIcon.Glyph("claudecode")),
        (TitlePi(), () => AppIcon.Glyph("pi")),
        (TitleAntigravity(), () => AppIcon.Glyph("antigravity")),
        (TitleCodex(), () => AppIcon.Glyph("codex")),
        (TitleOpencode(), () => AppIcon.Glyph("opencode")),
        (TitleDeepseek(), () => AppIcon.Glyph("deepseek")),
        (TitleQwen(), () => AppIcon.Glyph("qwen")),
        (TitleAider(), () => AppIcon.Badge("ai", Color.FromRgb(0x8B, 0x5C, 0xF6))),
        (TitleGemini(), () => AppIcon.Glyph("googlegemini")),
        (TitleSsh(), () => AppIcon.Badge("S", Color.FromRgb(0x2B, 0x8C, 0xBE))),
    };

    // Source-generated matchers for the OSC title/command-line fallback path.
    // Same patterns and case-insensitivity as before, plus CultureInvariant so
    // matching ASCII tool names never depends on the current UI culture.
    [GeneratedRegex(@"\bnvim\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleNvim();
    [GeneratedRegex(@"\blazyvim\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleLazyvim();
    [GeneratedRegex(@"\bvim\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleVim();
    [GeneratedRegex(@"\blazygit\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleLazygit();
    [GeneratedRegex(@"\bhtop\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleHtop();
    [GeneratedRegex(@"\bbtop\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleBtop();
    [GeneratedRegex(@"\btmux\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleTmux();
    [GeneratedRegex(@"claude", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleClaude();
    [GeneratedRegex(@"\bpi\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitlePi();
    [GeneratedRegex(@"antigravity", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleAntigravity();
    [GeneratedRegex(@"\bcodex\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleCodex();
    [GeneratedRegex(@"\b(opencode|open-code)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleOpencode();
    [GeneratedRegex(@"\bdeepseek\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleDeepseek();
    [GeneratedRegex(@"\bqwen\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleQwen();
    [GeneratedRegex(@"\baider\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleAider();
    [GeneratedRegex(@"\bgemini\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleGemini();
    [GeneratedRegex(@"\bssh\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleSsh();

    private static AppIcon? GetKnownProcessIcon(string name) =>
        ProcessIconFactories.TryGetValue(name, out var factory) ? factory() : null;

    /// <summary>
    /// Resolves the tab icon. A shim host (node) is identified by the script
    /// in its command line first, then by the OSC title, and only then by its
    /// own icon (a bare `node` session). Other processes are looked up in the
    /// map directly, fall back to the title, and finally to a letter badge.
    /// Unknown shim scripts never replace the current icon.
    /// </summary>
    internal static AppIcon? Resolve(string? processName, string? commandLine, string? title)
    {
        if (processName is { } name)
        {
            var isShim = ShimHosts.Contains(name);
            if (isShim)
            {
                if (FromCommandLine(commandLine, name) is { } shimIcon)
                    return shimIcon;
                if (FromTitle(title) is { } shimTitleIcon)
                    return shimTitleIcon;
            }
            if (GetKnownProcessIcon(name) is { } known)
                return known;
            if (!isShim && FromTitle(title) is { } fromTitle)
                return fromTitle;
            if (!isShim)
                return FromProcess(name);
            return null;
        }
        return FromTitle(title);
    }

    /// <summary>True when the process is a script host that needs command-line matching.</summary>
    internal static bool IsShimHost(string processName) => ShimHosts.Contains(processName);

    /// <summary>
    /// Scans a command line for path segments that name a known app
    /// (e.g. <c>...\node_modules\@anthropic-ai\claude-code\cli.js</c>).
    /// Path infrastructure segments are ignored so npm's own directory never
    /// wins over the tool it launches.
    /// </summary>
    private static AppIcon? FromCommandLine(string? commandLine, string shimName)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        foreach (var segment in PathSegments(commandLine))
        {
            // The host's own exe (node.exe) names no app; only the script does.
            if (segment.Equals(shimName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (CommandLineNoise.Contains(segment))
                continue;
            if (GetKnownProcessIcon(segment) is { } icon)
                return icon;
            // npm lays pi out as @earendil-works\pi-coding-agent\dist\cli.js
            // and opencode as @opencode-ai\opencode\...; only the package stem
            // is on the disk, so match by the fragment that names the tool.
            if (segment.StartsWith("pi-", StringComparison.OrdinalIgnoreCase))
                return GetKnownProcessIcon("pi");
            if (segment.Contains("claude", StringComparison.OrdinalIgnoreCase))
                return GetKnownProcessIcon("claude");
            if (segment.Contains("antigravity", StringComparison.OrdinalIgnoreCase))
                return GetKnownProcessIcon("antigravity");
            if (segment.Contains("opencode", StringComparison.OrdinalIgnoreCase))
                return GetKnownProcessIcon("opencode");
            if (segment.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
                return GetKnownProcessIcon("deepseek");
            if (segment.Contains("qwen", StringComparison.OrdinalIgnoreCase))
                return GetKnownProcessIcon("qwen");
        }
        foreach (var (pattern, factory) in TitleRules)
        {
            if (pattern.IsMatch(commandLine))
                return factory();
        }
        return null;
    }

    /// <summary>Path segments of every token in a command line.</summary>
    private static IEnumerable<string> PathSegments(string commandLine)
    {
        var token = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in commandLine)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (token.Length > 0)
                {
                    foreach (var segment in SegmentsOf(token.ToString()))
                        yield return segment;
                    token.Clear();
                }
            }
            else
            {
                token.Append(ch);
            }
        }
        if (token.Length > 0)
        {
            foreach (var segment in SegmentsOf(token.ToString()))
                yield return segment;
        }
    }

    private static IEnumerable<string> SegmentsOf(string token)
    {
        foreach (var piece in token.Split('/', '\\'))
        {
            var segment = System.IO.Path.GetFileNameWithoutExtension(piece);
            if (segment.Length > 0)
                yield return segment;
        }
    }

    /// <summary>Icon for a process name; never null (letter badge fallback).</summary>
    internal static AppIcon FromProcess(string processName)
    {
        if (GetKnownProcessIcon(processName) is { } known)
            return known;
        var initial = char.ToUpperInvariant(processName.Length > 0 ? processName[0] : '?');
        return AppIcon.Badge(initial.ToString(), HashColor(processName));
    }

    /// <summary>Icon named by the OSC 0/2 terminal title, or null.</summary>
    internal static AppIcon? FromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        foreach (var (pattern, factory) in TitleRules)
        {
            if (pattern.IsMatch(title))
                return factory();
        }
        return null;
    }

    /// <summary>Stable color from a name: FNV-1a hash mapped to a pastel hue.</summary>
    internal static Color HashColor(string seed)
    {
        var hash = 2166136261u;
        foreach (var ch in seed)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return FromHsv(hash % 360, 0.55, 0.75);
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
