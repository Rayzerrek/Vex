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
        "pwsh", "powershell", "cmd", "nu", "bash", "zsh", "fish", "conhost", "wslhost",
    };

    /// <summary>
    /// Hosts that run other apps (node claude.js, node pi, ...); their own
    /// name never identifies the app, only their command line does.
    /// </summary>
    private static readonly HashSet<string> ShimHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "nodejs", "deno",
    };

    /// <summary>
    /// Command-line path segments that say nothing about the real app
    /// (global npm layouts, launcher dirs); skipped when scanning shims.
    /// </summary>
    private static readonly HashSet<string> CommandLineNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "npm", "npx", "node_modules", "bin", "lib", "cli", "scripts", "cmd",
    };

    private static readonly Lazy<IReadOnlyDictionary<string, AppIcon>> ProcessIconsLazy = new(() =>
        new Dictionary<string, AppIcon>(StringComparer.OrdinalIgnoreCase)
        {
            // Editors and git tooling.
            ["nvim"] = AppIcon.Glyph("neovim"),
            ["vim"] = AppIcon.Glyph("vim"),
            ["git"] = AppIcon.Glyph("git"),
            ["lazygit"] = AppIcon.Badge("lg", Color.FromRgb(0x00, 0xAA, 0xDD)),
            ["htop"] = AppIcon.Glyph("htop"),
            ["btop"] = AppIcon.Badge("bt", Color.FromRgb(0x00, 0xA8, 0x96)),
            ["tmux"] = AppIcon.Glyph("tmux"),
            ["ssh"] = AppIcon.Badge("S", Color.FromRgb(0x2B, 0x8C, 0xBE)),

            // Agent CLIs: claude ships a real exe; pi/antigravity get badges.
            ["claude"] = AppIcon.Glyph("claudecode"),
            ["pi"] = AppIcon.Badge("π", Color.FromRgb(0xFF, 0x7A, 0x59)),
            ["antigravity"] = AppIcon.Badge("ag", HashColor("antigravity")),

            // Runtimes and package managers.
            ["node"] = AppIcon.Glyph("nodedotjs"),
            ["npm"] = AppIcon.Glyph("npm"),
            ["pnpm"] = AppIcon.Glyph("pnpm"),
            ["bun"] = AppIcon.Glyph("bun"),
            ["python"] = AppIcon.Glyph("python"),
            ["python3"] = AppIcon.Glyph("python"),

            // Ops tooling.
            ["docker"] = AppIcon.Glyph("docker"),
            ["gh"] = AppIcon.Glyph("github"),
            ["kubectl"] = AppIcon.Glyph("kubernetes"),
            ["rustc"] = AppIcon.Glyph("rust"),
            ["cargo"] = AppIcon.Glyph("rust"),
            ["go"] = AppIcon.Glyph("go"),

            // Shells themselves (shown when nothing else is running).
            ["fish"] = AppIcon.Glyph("fishshell"),
            ["bash"] = AppIcon.Glyph("gnubash"),
            ["zsh"] = AppIcon.Glyph("zsh"),
            ["nu"] = AppIcon.Glyph("nushell"),
            ["pwsh"] = AppIcon.Badge("P", Color.FromRgb(0x53, 0x91, 0xFE)),
            ["powershell"] = AppIcon.Badge("P", Color.FromRgb(0x53, 0x91, 0xFE)),
            ["cmd"] = AppIcon.Badge(">", Color.FromRgb(0x00, 0x78, 0xD4)),
            ["wsl"] = AppIcon.Badge("W", HashColor("wsl")),
        });

    private static readonly Lazy<IReadOnlyList<(Regex Pattern, AppIcon Icon)>> TitleRulesLazy = new(() =>
        new (Regex, AppIcon)[]
        {
            (new Regex(@"\bnvim\b", RegexOptions.IgnoreCase), AppIcon.Glyph("neovim")),
            (new Regex(@"\blazyvim\b", RegexOptions.IgnoreCase), AppIcon.Glyph("lazyvim")),
            (new Regex(@"\bvim\b", RegexOptions.IgnoreCase), AppIcon.Glyph("vim")),
            (new Regex(@"\blazygit\b", RegexOptions.IgnoreCase), AppIcon.Badge("lg", Color.FromRgb(0x00, 0xAA, 0xDD))),
            (new Regex(@"\bhtop\b", RegexOptions.IgnoreCase), AppIcon.Glyph("htop")),
            (new Regex(@"\bbtop\b", RegexOptions.IgnoreCase), AppIcon.Badge("bt", Color.FromRgb(0x00, 0xA8, 0x96))),
            (new Regex(@"\btmux\b", RegexOptions.IgnoreCase), AppIcon.Glyph("tmux")),
            (new Regex(@"claude", RegexOptions.IgnoreCase), AppIcon.Glyph("claudecode")),
            (new Regex(@"\bpi\b", RegexOptions.IgnoreCase), AppIcon.Badge("π", Color.FromRgb(0xFF, 0x7A, 0x59))),
            (new Regex(@"antigravity", RegexOptions.IgnoreCase), AppIcon.Badge("ag", HashColor("antigravity"))),
            (new Regex(@"\bssh\b", RegexOptions.IgnoreCase), AppIcon.Badge("S", Color.FromRgb(0x2B, 0x8C, 0xBE))),
        });

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
            if (ProcessIconsLazy.Value.TryGetValue(name, out var known))
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

        var icons = ProcessIconsLazy.Value;
        foreach (var segment in PathSegments(commandLine))
        {
            // The host's own exe (node.exe) names no app; only the script does.
            if (segment.Equals(shimName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (CommandLineNoise.Contains(segment))
                continue;
            if (icons.TryGetValue(segment, out var icon))
                return icon;
            // npm lays pi out as @earendil-works\pi-coding-agent\dist\cli.js.
            if (segment.StartsWith("pi-", StringComparison.OrdinalIgnoreCase))
                return icons["pi"];
            if (segment.Contains("claude", StringComparison.OrdinalIgnoreCase))
                return icons["claude"];
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
        if (ProcessIconsLazy.Value.TryGetValue(processName, out var known))
            return known;
        var initial = char.ToUpperInvariant(processName.Length > 0 ? processName[0] : '?');
        return AppIcon.Badge(initial.ToString(), HashColor(processName));
    }

    /// <summary>Icon named by the OSC 0/2 terminal title, or null.</summary>
    internal static AppIcon? FromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        foreach (var (pattern, icon) in TitleRulesLazy.Value)
        {
            if (pattern.IsMatch(title))
                return icon;
        }
        return null;
    }

    /// <summary>Stable color from a name: FNV-1a hash mapped to a pastel hue.</summary>
    private static Color HashColor(string seed)
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
