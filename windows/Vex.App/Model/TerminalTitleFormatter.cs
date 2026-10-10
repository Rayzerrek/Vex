namespace Vex.App.Model;

/// <summary>
/// Represents the parsed components of a terminal title.
/// </summary>
public readonly record struct TitleParseResult(string TabTitle, string? AppName, bool IsModified = false);

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
    public static TitleParseResult Parse(string? rawTitle) => ParseTitle(rawTitle, isCommandLine: false);

    /// <summary>Extracts an executable identity without treating its arguments as a session title.</summary>
    internal static TitleParseResult ParseCommandLine(string? commandLine) => ParseTitle(commandLine, isCommandLine: true);

    private static TitleParseResult ParseTitle(string? rawTitle, bool isCommandLine)
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

        if (!isCommandLine && TryParseDirectoryTitle(span, out var directoryTitle))
            return new TitleParseResult(directoryTitle, null);

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

        // 5. Standalone buffer/file with modified marker (e.g. "main.rs*", "[+]", "file.txt ●")
        if (CleanFileTitleAndExtractModified(span, "", out var standaloneTitle, out var standaloneMod) && standaloneMod)
        {
            var title = string.IsNullOrEmpty(standaloneTitle) ? "Terminal" : standaloneTitle;
            return new TitleParseResult(title, null, IsModified: true);
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
        if (!isCommandLine && span.IndexOf(' ') < 0 && span.IndexOfAny('\\', '/') >= 0)
        {
            var lastSep = span.LastIndexOfAny('\\', '/');
            var folder = span[(lastSep + 1)..].Trim();
            if (!folder.IsEmpty)
            {
                var folderStr = folder.ToString();
                if (StripExtension(folder).Length == folder.Length)
                    return new TitleParseResult(folderStr, null);
            }
        }

        // 8. Tokenize command line and strip runner prefixes (bun, npx, pnpm, python, ...)
        var completeTitle = span;
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
        if (!isCommandLine && !span.IsEmpty && !AppIconCatalog.IsKnownApp(progName) &&
            firstToken.IndexOfAny('\\', '/') < 0 && StripExtension(firstToken).Length == firstToken.Length)
        {
            return new TitleParseResult(completeTitle.ToString(), null);
        }
        if (progName.Equals("opencode", StringComparison.OrdinalIgnoreCase) ||
            progName.Equals("open-code", StringComparison.OrdinalIgnoreCase) ||
            progName.Equals("oc", StringComparison.OrdinalIgnoreCase))
        {
            return new TitleParseResult("OpenCode", "opencode");
        }
        return new TitleParseResult(progName, progName);
    }

    private static bool TryParseDirectoryTitle(ReadOnlySpan<char> span, out string title)
    {
        title = "";
        var path = span.Trim("\"'".AsSpan());
        var rooted = path.StartsWith("/") || path.StartsWith("\\\\") ||
            path.StartsWith("~/") || path.StartsWith("~\\") ||
            (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/');
        if (!rooted)
            return false;

        // Executable paths and editor titles carry application identity;
        // a directory named after an app must not impersonate that app.
        foreach (var extension in (ReadOnlySpan<string>)[".exe", ".cmd", ".bat", ".ps1", ".js", ".mjs", ".cjs", ".ts", ".py", ".sh"])
        {
            var index = path.IndexOf(extension.AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (index + extension.Length == path.Length || char.IsWhiteSpace(path[index + extension.Length])))
                return false;
        }
        if (TryExtractSeparatedTitle(path, out var separated) &&
            separated.AppName is { } app && AppIconCatalog.IsKnownApp(app))
            return false;

        var trimmed = path.TrimEnd("\\/".AsSpan());
        var lastSeparator = trimmed.LastIndexOfAny('\\', '/');
        title = trimmed.IsEmpty || (trimmed.Length == 2 && trimmed[1] == ':')
            ? path.ToString()
            : trimmed[(lastSeparator + 1)..].ToString();
        return true;
    }


    private static bool TryExtractSeparatedTitle(ReadOnlySpan<char> span, out TitleParseResult result)
    {
        result = default;

        // 1. Check for separator from the right first (e.g. "<file> [modified] - <App>")
        foreach (var sep in (ReadOnlySpan<string>)[" - ", " : ", " | ", ": ", "| "])
        {
            var rIdx = span.LastIndexOf(sep.AsSpan(), StringComparison.Ordinal);
            if (rIdx > 0)
            {
                var left = span[..rIdx].Trim();
                var right = span[(rIdx + sep.Length)..].Trim();
                if (!right.IsEmpty)
                {
                    var isRightKnown = AppIconCatalog.IsKnownApp(right);
                    var isLeftMod = HasModifiedIndicator(left);
                    var isLeftFilename = LooksLikeFilename(left) && !LooksLikeFilename(right);

                    if (isRightKnown || isLeftMod || isLeftFilename)
                    {
                        var canonicalApp = AppIconCatalog.CanonicalAppName(right);
                        CleanFileTitleAndExtractModified(left, canonicalApp, out var tabTitle, out var isMod);
                        result = new TitleParseResult(tabTitle, canonicalApp, isMod);
                        return true;
                    }
                }
            }
        }

        // 2. Check for separator from the left (e.g. "<App> - <session/file>")
        foreach (var sep in (ReadOnlySpan<string>)[" - ", " : ", " | ", ": ", "| "])
        {
            var idx = span.IndexOf(sep.AsSpan(), StringComparison.Ordinal);
            if (idx > 0)
            {
                var left = span[..idx].Trim();
                var right = span[(idx + sep.Length)..].Trim();
                if (!left.IsEmpty)
                {
                    var isLeftKnown = AppIconCatalog.IsKnownApp(left);
                    var isRightMod = HasModifiedIndicator(right);
                    var isRightFilename = LooksLikeFilename(right) && !LooksLikeFilename(left);

                    if (isLeftKnown || isRightMod || isRightFilename)
                    {
                        var canonicalApp = AppIconCatalog.CanonicalAppName(left);
                        CleanFileTitleAndExtractModified(right, canonicalApp, out var tabTitle, out var isMod);
                        var appDisplayName = left.Equals("OpenCode", StringComparison.OrdinalIgnoreCase) ? "OpenCode" : canonicalApp;
                        result = new TitleParseResult(tabTitle, appDisplayName, isMod);
                        return true;
                    }
                }
            }
        }

        // 3. Neither side is a known app or filename, but left looks like a single-word application name (e.g. "CustomApp - Task 1")
        foreach (var sep in (ReadOnlySpan<string>)[" - ", " : ", " | ", ": ", "| "])
        {
            var idx = span.IndexOf(sep.AsSpan(), StringComparison.Ordinal);
            if (idx > 0)
            {
                var left = span[..idx].Trim();
                var right = span[(idx + sep.Length)..].Trim();
                if (!left.IsEmpty && left.IndexOfAny(' ', '\t') < 0 && !right.IsEmpty)
                {
                    var leftStr = left.ToString();
                    CleanFileTitleAndExtractModified(right, leftStr, out var tabTitle, out var isMod);
                    result = new TitleParseResult(tabTitle, leftStr, isMod);
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasModifiedIndicator(ReadOnlySpan<char> span)
    {
        if (span.IndexOf("[+]".AsSpan(), StringComparison.Ordinal) >= 0 ||
            span.IndexOf("[*]".AsSpan(), StringComparison.Ordinal) >= 0 ||
            span.IndexOf("[modified]".AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0 ||
            span.IndexOf("(modified)".AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0 ||
            span.IndexOfAny('●', '•') >= 0 ||
            span.EndsWith(" *") ||
            span.EndsWith(" +"))
        {
            return true;
        }

        if (span.EndsWith("*") && span.Length > 1 && !span.StartsWith("*") && span.IndexOf('.') >= 0)
            return true;

        return false;
    }

    private static bool LooksLikeFilename(ReadOnlySpan<char> span)
    {
        span = span.Trim();
        if (span.EndsWith(")") && span.LastIndexOf('(') is var pIdx && pIdx > 0)
            span = span[..pIdx].TrimEnd();

        var spaceIdx = span.IndexOfAny(' ', '\t');
        var token = spaceIdx > 0 ? span[..spaceIdx] : span;

        var dotIdx = token.LastIndexOf('.');
        if (dotIdx <= 0 || dotIdx == token.Length - 1)
            return false;

        var ext = token[(dotIdx + 1)..];
        if (ext.Length is < 1 or > 6)
            return false;

        foreach (var c in ext)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return false;
        }
        return true;
    }

    private static bool CleanFileTitleAndExtractModified(
        ReadOnlySpan<char> fileSpan,
        string fallbackTitle,
        out string cleanTitle,
        out bool isModified)
    {
        isModified = false;

        // Check for "(modified)" specifically first
        if (fileSpan.EndsWith("(modified)", StringComparison.OrdinalIgnoreCase))
        {
            isModified = true;
            fileSpan = fileSpan[..^10].TrimEnd();
        }
        else if (fileSpan.EndsWith(")") && fileSpan.LastIndexOf('(') is var parenIdx && parenIdx > 0)
        {
            // Strip trailing directory in parentheses: e.g. "file.txt [+] (C:\Users\...)"
            fileSpan = fileSpan[..parenIdx].TrimEnd();
        }

        // 1. "[+]"
        var modPlusIdx = fileSpan.IndexOf("[+]".AsSpan(), StringComparison.Ordinal);
        if (modPlusIdx >= 0)
        {
            isModified = true;
            var before = fileSpan[..modPlusIdx].Trim();
            var after = fileSpan[(modPlusIdx + 3)..].Trim();
            fileSpan = CombineSpan(before, after);
        }

        // 2. "[*]"
        var modStarBracketIdx = fileSpan.IndexOf("[*]".AsSpan(), StringComparison.Ordinal);
        if (modStarBracketIdx >= 0)
        {
            isModified = true;
            var before = fileSpan[..modStarBracketIdx].Trim();
            var after = fileSpan[(modStarBracketIdx + 3)..].Trim();
            fileSpan = CombineSpan(before, after);
        }

        // 3. "[modified]"
        var modBracketIdx = fileSpan.IndexOf("[modified]".AsSpan(), StringComparison.OrdinalIgnoreCase);
        if (modBracketIdx >= 0)
        {
            isModified = true;
            var before = fileSpan[..modBracketIdx].Trim();
            var after = fileSpan[(modBracketIdx + 10)..].Trim();
            fileSpan = CombineSpan(before, after);
        }

        // 4. Bullet marker "●" or "•"
        var bulletIdx = fileSpan.IndexOfAny('●', '•');
        if (bulletIdx >= 0)
        {
            isModified = true;
            var before = fileSpan[..bulletIdx].Trim();
            var after = fileSpan[(bulletIdx + 1)..].Trim();
            fileSpan = CombineSpan(before, after);
        }

        // 5. Trailing " *" or " +"
        if (fileSpan.EndsWith(" *") || fileSpan.EndsWith(" +"))
        {
            isModified = true;
            fileSpan = fileSpan[..^2].TrimEnd();
        }
        else if (fileSpan.EndsWith("*") && fileSpan.Length > 1 && !fileSpan.StartsWith("*") && fileSpan.IndexOf('.') >= 0)
        {
            isModified = true;
            fileSpan = fileSpan[..^1].TrimEnd();
        }

        cleanTitle = fileSpan.IsEmpty ? fallbackTitle : fileSpan.ToString();
        return isModified || !fileSpan.IsEmpty;
    }

    private static ReadOnlySpan<char> CombineSpan(ReadOnlySpan<char> before, ReadOnlySpan<char> after)
    {
        if (before.IsEmpty) return after;
        if (after.IsEmpty) return before;
        return string.Concat(before, " ", after).AsSpan();
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

            // A workspace directory can share a tool's name; only the
            // executable itself identifies the app outside package launchers.
            if (segments.Count > 0 && AppIconCatalog.IsKnownApp(segments[^1]))
                return segments[^1];

            // Package launchers use generic filenames such as cli.js; workspace
            // parent directories have no authority over the executable identity.
            var packageIndex = segments.FindLastIndex(segment => segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
            if (packageIndex >= 0)
            {
                for (var i = segments.Count - 2; i > packageIndex; i--)
                {
                    if (AppIconCatalog.IsKnownApp(segments[i]))
                        return segments[i];
                    if (segments[i].StartsWith("pi-", StringComparison.OrdinalIgnoreCase))
                        return "pi";
                }
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

}
