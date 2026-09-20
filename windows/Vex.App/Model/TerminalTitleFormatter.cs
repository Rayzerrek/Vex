namespace Vex.App.Model;

/// <summary>
/// Represents the parsed components of a terminal title.
/// </summary>
public readonly record struct TitleParseResult(string TabTitle, string? AppName);

/// <summary>
/// Universal terminal title parser and formatter.
/// Extracts the primary application name and clean tab title from:
/// - Command lines with runners and redirections: "bun pi > vex" -> (TabTitle: "pi", AppName: "pi")
/// - Session titles: "OpenCode - nazwa sesji" -> (TabTitle: "nazwa sesji", AppName: "OpenCode")
/// - Reverse titles: "main.rs - NVIM" -> (TabTitle: "main.rs", AppName: "NVIM")
/// - Shell wrappers: "pwsh - agy" -> (TabTitle: "agy", AppName: "agy")
/// </summary>
public static class TerminalTitleFormatter
{
    /// <summary>
    /// Formats the raw title into a concise tab title.
    /// </summary>
    public static string Format(string? title) => Parse(title).TabTitle;

    /// <summary>
    /// Parses the raw title into its tab title and application identity.
    /// </summary>
    public static TitleParseResult Parse(string? rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
            return new TitleParseResult("", null);

        var span = rawTitle.AsSpan().Trim();
        if (span.IsEmpty)
            return new TitleParseResult("", null);

        // 1. Strip elevated shell prefix: "Administrator: "
        if (span.StartsWith("Administrator: ", StringComparison.OrdinalIgnoreCase))
            span = span["Administrator: ".Length..].TrimStart();

        // 2. Strip shell window title prefix, e.g. "C:\...\pwsh.exe - <command>" or "pwsh - <command>"
        var dashIdx = span.IndexOf(" - ".AsSpan(), StringComparison.Ordinal);
        if (dashIdx > 0)
        {
            var beforeDash = span[..dashIdx].Trim();
            if (IsShellNameOrPath(beforeDash))
                span = span[(dashIdx + 3)..].TrimStart();
        }

        // 3. Status bracket plugin format: "[*] Working | Session Title"
        if (span.StartsWith("[") && span.IndexOf(']') is var rBrk && rBrk > 0)
        {
            var afterBrk = span[(rBrk + 1)..].TrimStart();
            var pipe = afterBrk.IndexOf('|');
            if (pipe >= 0)
            {
                var session = afterBrk[(pipe + 1)..].Trim();
                if (!session.IsEmpty)
                    return new TitleParseResult(session.ToString(), null);
            }
        }

        // 4. Check for App/Session or File/App separator: " - ", " : ", " | "
        if (TryExtractSeparatedTitle(span, out var separated))
            return separated;

        // 5. Neovim / Vim suffix without spaces: "<file> - NVIM" or "<file> - VIM"
        if (span.EndsWith(" - NVIM", StringComparison.OrdinalIgnoreCase))
        {
            var file = span[..^7].Trim();
            return new TitleParseResult(file.ToString(), "neovim");
        }
        if (span.EndsWith(" - VIM", StringComparison.OrdinalIgnoreCase))
        {
            var file = span[..^6].Trim();
            return new TitleParseResult(file.ToString(), "vim");
        }

        // 6. Truncate at unquoted redirection or pipe operators: >, <, |, &, 2>, 1>
        var inQuotes = false;
        var quoteChar = '\0';
        var truncateAt = -1;
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if ((c == '"' || c == '\'') && (!inQuotes || c == quoteChar))
            {
                inQuotes = !inQuotes;
                quoteChar = inQuotes ? c : '\0';
            }
            else if (!inQuotes)
            {
                if (c is '>' or '<' or '|' or '&')
                {
                    truncateAt = i;
                    if (i > 0 && (span[i - 1] is '1' or '2') &&
                        (i == 1 || char.IsWhiteSpace(span[i - 2])))
                    {
                        truncateAt = i - 1;
                    }
                    break;
                }
            }
        }
        if (truncateAt >= 0)
            span = span[..truncateAt].TrimEnd();

        if (span.IsEmpty)
            return new TitleParseResult("", null);

        // 7. Check if it's a standalone path without arguments (e.g. "C:\Users\...\vex" or "~/code/vex")
        if (span.IndexOf(' ') < 0 && span.IndexOfAny('\\', '/') >= 0)
        {
            var lastSep = span.LastIndexOfAny('\\', '/');
            var folder = span[(lastSep + 1)..].Trim();
            if (!folder.IsEmpty)
            {
                var folderStr = folder.ToString();
                return new TitleParseResult(folderStr, folderStr);
            }
        }

        // 8. Tokenize command line and strip runner prefixes (bun, npx, pnpm, python, ...)
        var firstToken = GetNextToken(ref span);
        if (firstToken.IsEmpty)
            return new TitleParseResult("", null);

        var cleanedFirst = CleanToken(firstToken);
        var isRunner = IsRunner(cleanedFirst);
        var isCargoRun = cleanedFirst.Equals("cargo", StringComparison.OrdinalIgnoreCase);

        if (isRunner || isCargoRun)
        {
            if (isCargoRun)
            {
                var peek = span.TrimStart();
                if (!peek.StartsWith("run", StringComparison.OrdinalIgnoreCase))
                {
                    var prog = CleanProgramName(firstToken);
                    return new TitleParseResult(prog, prog);
                }
            }

            while (!span.IsEmpty)
            {
                var next = GetNextToken(ref span);
                if (next.IsEmpty)
                    break;

                var cleanedNext = CleanToken(next);
                if (cleanedNext.Equals("run", StringComparison.OrdinalIgnoreCase) ||
                    cleanedNext.Equals("exec", StringComparison.OrdinalIgnoreCase) ||
                    cleanedNext.Equals("dlx", StringComparison.OrdinalIgnoreCase) ||
                    cleanedNext.Equals("x", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (next.StartsWith("-"))
                    continue;

                var targetProg = CleanProgramName(next);
                return new TitleParseResult(targetProg, targetProg);
            }

            var runnerStr = cleanedFirst.ToString();
            return new TitleParseResult(runnerStr, runnerStr);
        }

        var progName = CleanProgramName(firstToken);
        if (progName.Equals("opencode", StringComparison.OrdinalIgnoreCase) ||
            progName.Equals("open-code", StringComparison.OrdinalIgnoreCase) ||
            progName.Equals("oc", StringComparison.OrdinalIgnoreCase))
        {
            return new TitleParseResult("OpenCode", "opencode");
        }
        return new TitleParseResult(progName, progName);
    }


    private static bool TryExtractSeparatedTitle(ReadOnlySpan<char> span, out TitleParseResult result)
    {
        result = default;

        int sepIdx = -1;
        int sepLen = 0;
        foreach (var sep in (ReadOnlySpan<string>)[" - ", " : ", " | ", ": ", "| "])
        {
            var idx = span.IndexOf(sep.AsSpan(), StringComparison.Ordinal);
            if (idx > 0)
            {
                sepIdx = idx;
                sepLen = sep.Length;
                break;
            }
        }

        if (sepIdx <= 0)
            return false;

        var left = span[..sepIdx].Trim();
        var right = span[(sepIdx + sepLen)..].Trim();

        if (left.IsEmpty && right.IsEmpty)
            return false;

        var leftIsApp = AppIconCatalog.IsKnownApp(left);
        var rightIsApp = AppIconCatalog.IsKnownApp(right);

        if (leftIsApp)
        {
            var tab = !right.IsEmpty ? right.ToString() : left.ToString();
            result = new TitleParseResult(tab, left.ToString());
            return true;
        }
        if (rightIsApp)
        {
            var tab = !left.IsEmpty ? left.ToString() : right.ToString();
            result = new TitleParseResult(tab, right.ToString());
            return true;
        }

        // Neither is known in the catalog, but left looks like a single-word application name
        // (e.g. "CustomApp - Task 1")
        if (left.IndexOfAny(' ', '\t') < 0 && !right.IsEmpty)
        {
            result = new TitleParseResult(right.ToString(), left.ToString());
            return true;
        }

        return false;
    }

    private static bool IsShellNameOrPath(ReadOnlySpan<char> text)
    {
        var lastSep = text.LastIndexOfAny('\\', '/');
        var name = lastSep >= 0 ? text[(lastSep + 1)..] : text;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        return name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("bash", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("zsh", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("fish", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("sh", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("nu", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("nushell", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("wsl", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("wslhost", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("conhost", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Windows PowerShell", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Command Prompt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRunner(ReadOnlySpan<char> name)
    {
        return name.Equals("bun", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("bunx", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("npx", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("pnpm", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("pnpx", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("npm", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("yarn", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("deno", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("node", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("python", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("python3", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("py", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("sudo", StringComparison.OrdinalIgnoreCase);
    }

    private static ReadOnlySpan<char> GetNextToken(ref ReadOnlySpan<char> span)
    {
        span = span.TrimStart();
        if (span.IsEmpty)
            return default;

        if (span[0] is '"' or '\'')
        {
            var quote = span[0];
            var closeIdx = span[1..].IndexOf(quote);
            if (closeIdx >= 0)
            {
                var token = span[1..(closeIdx + 1)];
                span = span[(closeIdx + 2)..];
                return token;
            }
        }

        var spaceIdx = span.IndexOfAny(' ', '\t');
        if (spaceIdx < 0)
        {
            var token = span;
            span = default;
            return token;
        }
        else
        {
            var token = span[..spaceIdx];
            span = span[(spaceIdx + 1)..];
            return token;
        }
    }

    private static ReadOnlySpan<char> CleanToken(ReadOnlySpan<char> token)
    {
        if (token.Length >= 2 &&
            ((token[0] == '"' && token[^1] == '"') ||
             (token[0] == '\'' && token[^1] == '\'')))
        {
            token = token[1..^1];
        }
        var lastSep = token.LastIndexOfAny('\\', '/');
        if (lastSep >= 0)
            token = token[(lastSep + 1)..];

        return StripExtension(token);
    }

    private static string CleanProgramName(ReadOnlySpan<char> token)
    {
        if (token.Length >= 2 &&
            ((token[0] == '"' && token[^1] == '"') ||
             (token[0] == '\'' && token[^1] == '\'')))
        {
            token = token[1..^1];
        }

        // If it contains slashes, inspect path segments
        if (token.IndexOfAny('\\', '/') >= 0)
        {
            var segments = new List<string>();
            var remaining = token;
            while (!remaining.IsEmpty)
            {
                var sepIdx = remaining.IndexOfAny('\\', '/');
                ReadOnlySpan<char> piece;
                if (sepIdx < 0)
                {
                    piece = remaining;
                    remaining = default;
                }
                else
                {
                    piece = remaining[..sepIdx];
                    remaining = remaining[(sepIdx + 1)..];
                }
                piece = StripExtension(piece);
                if (!piece.IsEmpty && !piece.Equals(".", StringComparison.Ordinal) && !piece.Equals("..", StringComparison.Ordinal))
                    segments.Add(piece.ToString());
            }

            // If any segment names a known app, that wins
            for (var i = segments.Count - 1; i >= 0; i--)
            {
                var seg = segments[i];
                if (AppIconCatalog.IsKnownApp(seg.AsSpan()))
                    return seg;
            }

            // If the last segment is generic noise (cli, index, main, bin), look at previous segment
            if (segments.Count >= 2)
            {
                var last = segments[^1];
                if (IsGenericNoise(last))
                    return segments[^2];
            }
            if (segments.Count > 0)
                return segments[^1];
        }

        return StripExtension(token).ToString();
    }

    private static ReadOnlySpan<char> StripExtension(ReadOnlySpan<char> token)
    {
        if (token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            token.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            token.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
            token.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ||
            token.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) ||
            token.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase) ||
            token.EndsWith(".mts", StringComparison.OrdinalIgnoreCase) ||
            token.EndsWith(".cts", StringComparison.OrdinalIgnoreCase))
        {
            return token[..^4];
        }
        if (token.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            token.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
            token.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ||
            token.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
        {
            return token[..^3];
        }
        return token;
    }

    private static bool IsGenericNoise(string segment) =>
        segment.Equals("cli", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("index", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("main", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("run", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("bin", StringComparison.OrdinalIgnoreCase);
}
