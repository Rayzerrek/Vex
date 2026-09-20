using System.Collections.Frozen;
using System.Text;
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
    /// Hosts that run other apps (node claude.js, bun pi, wsl nvim, ...); their own
    /// name never identifies the app, only their command line or the OSC
    /// title does — the process tree cannot see inside a script or a VM.
    /// </summary>
    private static readonly FrozenSet<string> ShimHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "node", "nodejs", "deno", "wsl", "bun", "bunx", "tsx", "ts-node", "npx", "pnpm", "pnpx", "yarn",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Command-line path segments that say nothing about the real app
    /// (global npm layouts, launcher dirs); skipped when scanning shims.
    /// </summary>
    private static readonly FrozenSet<string> CommandLineNoise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "npm", "npx", "node_modules", "bin", "lib", "cli", "scripts", "cmd",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tools that use custom letter badges rather than SVG glyphs.
    /// </summary>
    private static readonly FrozenDictionary<string, Func<AppIcon>> SpecialBadges =
        new Dictionary<string, Func<AppIcon>>(StringComparer.OrdinalIgnoreCase)
        {
            ["lazygit"] = () => AppIcon.Badge("lg", Color.FromRgb(0x00, 0xAA, 0xDD)),
            ["btop"] = () => AppIcon.Badge("bt", Color.FromRgb(0x00, 0xA8, 0x96)),
            ["ssh"] = () => AppIcon.Badge("S", Color.FromRgb(0x2B, 0x8C, 0xBE)),
            ["aider"] = () => AppIcon.Badge("ai", Color.FromRgb(0x8B, 0x5C, 0xF6)),
            ["cmd"] = () => AppIcon.Badge(">", Color.FromRgb(0x00, 0x78, 0xD4)),
            ["wsl"] = () => AppIcon.Badge("W", HashColor("wsl")),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Universal alias dictionary mapping CLI and process variants to canonical glyph slugs.
    /// E.g. "agy" or "antigravity" -> "googlegemini", "oc" -> "opencode".
    /// </summary>
    private static readonly FrozenDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Agent CLIs
            ["agy"] = "googlegemini",
            ["antigravity"] = "googlegemini",
            ["gemini"] = "googlegemini",
            ["oc"] = "opencode",
            ["open-code"] = "opencode",
            ["claude"] = "claudecode",
            ["claude-code"] = "claudecode",
            ["codex"] = "codex",

            // Editors & tools
            ["nvim"] = "neovim",
            ["vim"] = "vim",
            ["lazyvim"] = "lazyvim",
            ["hx"] = "helix",
            ["copilot"] = "githubcopilot",
            ["zed"] = "zedindustries",
            ["next"] = "nextdotjs",
            ["nextjs"] = "nextdotjs",
            ["http"] = "httpie",
            ["cargo"] = "rust",
            ["rustc"] = "rust",
            ["py"] = "python",
            ["python3"] = "python",
            ["node"] = "nodedotjs",
            ["nodejs"] = "nodedotjs",
            ["tsx"] = "typescript",
            ["ts-node"] = "typescript",
            ["deno"] = "typescript",
            ["nu"] = "nushell",
            ["pwsh"] = "powershell",
            ["powershell"] = "powershell",
            ["bash"] = "gnubash",
            ["sh"] = "gnubash",
            ["fish"] = "fishshell",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<FrozenSet<string>> KnownAppsLazy = new(BuildKnownApps);
    private static FrozenSet<string> KnownApps => KnownAppsLazy.Value;

    private static FrozenSet<string> BuildKnownApps()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in SpecialBadges.Keys)
            set.Add(key);
        foreach (var (k, v) in Aliases)
        {
            set.Add(k);
            set.Add(v);
        }
        foreach (var g in Glyphs)
            set.Add(g.Slug);

        set.Add("opencode");
        set.Add("claude");
        set.Add("pi");
        set.Add("neovim");
        return set.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if a string span matches a known application name or alias without allocating.
    /// </summary>
    internal static bool IsKnownApp(ReadOnlySpan<char> name) =>
        KnownApps.GetAlternateLookup<ReadOnlySpan<char>>().Contains(name);

    /// <summary>
    /// Universally resolves an icon for an application or tool name.
    /// </summary>
    internal static AppIcon? ResolveIcon(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (SpecialBadges.TryGetValue(name, out var badgeFactory))
            return badgeFactory();

        if (Aliases.TryGetValue(name, out var canonical))
        {
            if (GlyphBySlug.TryGetValue(canonical, out var cg))
                return AppIcon.Glyph(cg.Slug);
        }

        if (GlyphBySlug.TryGetValue(name, out var g))
            return AppIcon.Glyph(g.Slug);

        return null;
    }

    /// <summary>
    /// Resolves the tab icon. A shim host (node, bun) is identified by the script
    /// in its command line first, then by the OSC title, and only then by its
    /// own icon (a bare `node` or `bun` session). Other processes are looked up
    /// in the catalog directly, fall back to the title, and finally to a letter badge.
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
            if (ResolveIcon(name) is { } known)
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
    /// Scans a command line for the actual tool name being run.
    /// </summary>
    private static AppIcon? FromCommandLine(string? commandLine, string shimName)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        var parsed = TerminalTitleFormatter.Parse(commandLine);
        if (!string.IsNullOrEmpty(parsed.AppName) &&
            !parsed.AppName.Equals(shimName, StringComparison.OrdinalIgnoreCase) &&
            ResolveIcon(parsed.AppName) is { } parsedIcon)
        {
            return parsedIcon;
        }

        foreach (var segment in PathSegments(commandLine))
        {
            if (segment.Equals(shimName, StringComparison.OrdinalIgnoreCase) || CommandLineNoise.Contains(segment))
                continue;

            if (ResolveIcon(segment) is { } icon)
                return icon;

            if (segment.StartsWith("pi-", StringComparison.OrdinalIgnoreCase))
                return ResolveIcon("pi");
        }

        return null;
    }

    /// <summary>
    /// Scans a shim command line for the tool name being run (e.g. "pi" in "bun pi > vex").
    /// </summary>
    internal static string? ResolveToolName(string processName, string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || !ShimHosts.Contains(processName))
            return null;

        var parsed = TerminalTitleFormatter.Parse(commandLine);
        if (!string.IsNullOrEmpty(parsed.AppName) &&
            !parsed.AppName.Equals(processName, StringComparison.OrdinalIgnoreCase))
        {
            return parsed.AppName;
        }

        foreach (var segment in PathSegments(commandLine))
        {
            if (segment.Equals(processName, StringComparison.OrdinalIgnoreCase) || CommandLineNoise.Contains(segment))
                continue;

            if (IsKnownApp(segment.AsSpan()))
                return segment;
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
        if (ResolveIcon(processName) is { } known)
            return known;
        var initial = char.ToUpperInvariant(processName.Length > 0 ? processName[0] : '?');
        return AppIcon.Badge(initial.ToString(), HashColor(processName));
    }

    /// <summary>
    /// Universally resolves an icon from the terminal title using the universal parser.
    /// </summary>
    internal static AppIcon? FromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var parsed = TerminalTitleFormatter.Parse(title);
        if (!string.IsNullOrEmpty(parsed.AppName) && ResolveIcon(parsed.AppName) is { } icon)
            return icon;

        if (!string.IsNullOrEmpty(parsed.TabTitle) && ResolveIcon(parsed.TabTitle) is { } tabIcon)
            return tabIcon;

        foreach (var segment in PathSegments(title))
        {
            if (ResolveIcon(segment) is { } match)
                return match;
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
