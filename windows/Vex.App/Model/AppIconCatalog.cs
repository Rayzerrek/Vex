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
    /// <summary>Catalog glyph names for validating every bundled application icon.</summary>
    internal static IEnumerable<string> GlyphSlugs => GlyphBySlug.Keys;

    /// <summary>Console-host helper processes that are not user applications.</summary>
    internal static readonly IReadOnlySet<string> ConsoleHelpers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "conhost", "wslhost", "OpenConsole",
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

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> CommandLineNoiseLookup =
        CommandLineNoise.GetAlternateLookup<ReadOnlySpan<char>>();
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
            ["cmd"] = () => AppIcon.Badge(">", Color.FromRgb(0x00, 0x78, 0xD4), isShellIcon: true),
            ["wsl"] = AppIcon.WslShellIcon,
            ["nano"] = () => AppIcon.Badge("na", Color.FromRgb(0x4A, 0x90, 0xE2)),
            ["micro"] = () => AppIcon.Badge("mc", Color.FromRgb(0x50, 0xE3, 0xC2)),
            ["emacs"] = () => AppIcon.Badge("em", Color.FromRgb(0x7F, 0x5A, 0xB6)),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, Func<AppIcon>>.AlternateLookup<ReadOnlySpan<char>> SpecialBadgesLookup =
        SpecialBadges.GetAlternateLookup<ReadOnlySpan<char>>();
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
            ["helix"] = "helix",
            ["nano"] = "nano",
            ["micro"] = "micro",
            ["emacs"] = "emacs",
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

    private static readonly FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> AliasesLookup =
        Aliases.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly Lazy<FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>>> KnownAppsLookupLazy =
        new(() => BuildKnownApps().GetAlternateLookup<ReadOnlySpan<char>>());

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
        KnownAppsLookupLazy.Value.Contains(name);

    /// <summary>
    /// Maps an application name or alias (e.g. "nvim", "hx", "oc") to its canonical lowercase slug.
    /// </summary>
    internal static string CanonicalAppName(ReadOnlySpan<char> name)
    {
        if (AliasesLookup.TryGetValue(name, out var canonical))
            return canonical;
        return name.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Universally resolves an icon for an application or tool name span without allocating strings.
    /// </summary>
    internal static AppIcon? ResolveIcon(ReadOnlySpan<char> name, bool? isDark = null)
    {
        if (name.IsEmpty)
            return null;

        if (SpecialBadgesLookup.TryGetValue(name, out var badgeFactory))
            return badgeFactory();

        if (AliasesLookup.TryGetValue(name, out var canonical))
        {
            if (GlyphBySlug.TryGetValue(canonical, out var cg))
                return AppIcon.Glyph(cg.Slug, isDark);
        }

        var str = name.ToString();
        if (GlyphBySlug.TryGetValue(str, out var g))
            return AppIcon.Glyph(g.Slug, isDark);

        return null;
    }

    /// <summary>
    /// Universally resolves an icon for an application or tool name.
    /// </summary>
    internal static AppIcon? ResolveIcon(string? name, bool? isDark = null) =>
        string.IsNullOrWhiteSpace(name) ? null : ResolveIcon(name.AsSpan(), isDark);

    /// <summary>
    /// Resolves the tab icon. A shim host (node, bun) is identified by the script
    /// in its command line first, then by the OSC title, and only then by its
    /// own icon (a bare `node` or `bun` session). Other processes are looked up
    /// in the catalog directly, fall back to the title, and finally to a letter badge.
    /// </summary>
    internal static AppIcon? Resolve(string? processName, string? commandLine, string? title, bool? isDark = null)
    {
        if (processName is { } name)
        {
            var isShim = ShimHosts.Contains(name);
            if (isShim)
            {
                if (FromCommandLine(commandLine, name, isDark) is { } shimIcon)
                    return shimIcon;
                if (FromTitle(title, isDark) is { } shimTitleIcon)
                    return shimTitleIcon;
            }
            if (ResolveIcon(name, isDark) is { } known)
                return known;
            if (!isShim && FromTitle(title, isDark) is { } fromTitle)
                return fromTitle;
            if (!isShim)
                return FromProcess(name, isDark);
            return null;
        }
        return FromTitle(title, isDark);
    }

    /// <summary>True when the process is a script host that needs command-line matching.</summary>
    internal static bool IsShimHost(string processName) => ShimHosts.Contains(processName);

    /// <summary>
    /// Scans a command line for the actual tool name being run.
    /// </summary>
    private static AppIcon? FromCommandLine(string? commandLine, string shimName, bool? isDark = null)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        var parsed = TerminalTitleFormatter.ParseCommandLine(commandLine);
        if (!string.IsNullOrEmpty(parsed.AppName) &&
            !parsed.AppName.Equals(shimName, StringComparison.OrdinalIgnoreCase) &&
            ResolveIcon(parsed.AppName, isDark) is { } parsedIcon)
        {
            return parsedIcon;
        }

        var packageIndex = commandLine.IndexOf("node_modules", StringComparison.OrdinalIgnoreCase);
        if (packageIndex < 0)
            return null;

        var shimSpan = shimName.AsSpan();
        foreach (var segment in EnumerateSegments(commandLine[(packageIndex + "node_modules".Length)..]))
        {
            if (segment.Equals(shimSpan, StringComparison.OrdinalIgnoreCase) || CommandLineNoiseLookup.Contains(segment))
                continue;

            if (ResolveIcon(segment, isDark) is { } icon)
                return icon;

            if (segment.StartsWith("pi-", StringComparison.OrdinalIgnoreCase))
                return ResolveIcon("pi", isDark);
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

        var parsed = TerminalTitleFormatter.ParseCommandLine(commandLine);
        if (!string.IsNullOrEmpty(parsed.AppName) &&
            !parsed.AppName.Equals(processName, StringComparison.OrdinalIgnoreCase) && IsKnownApp(parsed.AppName))
        {
            return parsed.AppName;
        }

        var packageIndex = commandLine.IndexOf("node_modules", StringComparison.OrdinalIgnoreCase);
        var packagePath = packageIndex < 0 ? null : commandLine[(packageIndex + "node_modules".Length)..];
        var procSpan = processName.AsSpan();
        foreach (var segment in EnumerateSegments(packagePath))
        {
            if (segment.Equals(procSpan, StringComparison.OrdinalIgnoreCase) || CommandLineNoiseLookup.Contains(segment))
                continue;

            if (IsKnownApp(segment))
                return segment.ToString();
            if (segment.StartsWith("pi-", StringComparison.OrdinalIgnoreCase))
                return "pi";
        }

        return string.Equals(parsed.AppName, processName, StringComparison.OrdinalIgnoreCase) ? null : parsed.AppName;
    }

    /// <summary>
    /// Enumerates path segments of every token in a command line or title without heap allocations.
    /// </summary>
    private static PathSegmentRange EnumerateSegments(string? text) => new(text.AsSpan());

    private readonly ref struct PathSegmentRange
    {
        private readonly ReadOnlySpan<char> _text;
        public PathSegmentRange(ReadOnlySpan<char> text) => _text = text;
        public PathSegmentEnumerator GetEnumerator() => new(_text);
    }

    private ref struct PathSegmentEnumerator
    {
        private readonly ReadOnlySpan<char> _span;
        private int _pos;
        private bool _inQuotes;
        private ReadOnlySpan<char> _currentPiece;
        private ReadOnlySpan<char> _remainingPieces;

        public PathSegmentEnumerator(ReadOnlySpan<char> text)
        {
            _span = text.Trim();
            _pos = 0;
            _inQuotes = false;
            _currentPiece = default;
            _remainingPieces = default;
        }

        public readonly ReadOnlySpan<char> Current => _currentPiece;

        public bool MoveNext()
        {
            while (true)
            {
                if (!_remainingPieces.IsEmpty)
                {
                    var sepIdx = _remainingPieces.IndexOfAny('/', '\\');
                    ReadOnlySpan<char> piece;
                    if (sepIdx >= 0)
                    {
                        piece = _remainingPieces[..sepIdx];
                        _remainingPieces = _remainingPieces[(sepIdx + 1)..];
                    }
                    else
                    {
                        piece = _remainingPieces;
                        _remainingPieces = default;
                    }

                    var dotIdx = piece.LastIndexOf('.');
                    piece = dotIdx > 0 ? piece[..dotIdx] : piece;
                    if (!piece.IsEmpty)
                    {
                        _currentPiece = piece;
                        return true;
                    }
                    continue;
                }

                if (_pos >= _span.Length)
                    return false;

                var tokenStart = _pos;
                while (_pos < _span.Length)
                {
                    var ch = _span[_pos];
                    if (ch == '"')
                    {
                        _inQuotes = !_inQuotes;
                    }
                    else if (char.IsWhiteSpace(ch) && !_inQuotes)
                    {
                        break;
                    }
                    _pos++;
                }

                var token = _span[tokenStart.._pos].Trim('"');
                while (_pos < _span.Length && char.IsWhiteSpace(_span[_pos]))
                    _pos++;

                if (!token.IsEmpty)
                {
                    _remainingPieces = token;
                }
            }
        }
    }
    /// <summary>Icon for a process name; never null (letter badge fallback).</summary>
    internal static AppIcon FromProcess(string processName, bool? isDark = null)
    {
        if (ResolveIcon(processName, isDark) is { } known)
            return known;
        var initial = char.ToUpperInvariant(processName.Length > 0 ? processName[0] : '?');
        return AppIcon.Badge(initial.ToString(), HashColor(processName));
    }

    /// <summary>
    /// Universally resolves an icon from the terminal title using the universal parser.
    /// </summary>
    internal static AppIcon? FromTitle(string? title, bool? isDark = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var parsed = TerminalTitleFormatter.Parse(title);
        if (!string.IsNullOrEmpty(parsed.AppName) && ResolveIcon(parsed.AppName, isDark) is { } icon)
            return icon;

        if (parsed.AppName is not null)
        {
            var command = title.AsSpan().Trim();
            var space = command.IndexOfAny(' ', '\t');
            if (space > 0 && ResolveIcon(command[..space], isDark) is { } runnerIcon)
                return runnerIcon;
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
