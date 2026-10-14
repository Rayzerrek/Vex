const std = @import("std");

/// Parsed components of a sanitized OSC 0 / OSC 2 terminal title.
pub const ParsedTitle = struct {
    title: []const u8 = "",
    app_name: ?[]const u8 = null,
    is_modified: bool = false,
};

/// Inline bounded owner for a pane's sanitized display title; empty means fall back to the shell title.
pub const TerminalTitle = struct {
    bytes: [max_title_bytes]u8 = undefined,
    len: usize = 0,
    app_bytes: [max_app_bytes]u8 = undefined,
    app_len: usize = 0,
    generation: u64 = 0,
    pub const max_raw_bytes = 512;
    pub const max_title_bytes = 192;
    pub const max_app_bytes = 64;

    /// Borrows the current formatted display title, or an empty slice when no custom title is active.
    pub fn slice(self: *const TerminalTitle) []const u8 {
        return self.bytes[0..self.len];
    }
    /// Borrows the application name extracted from the active OSC 0 / OSC 2 title, if any.
    pub fn appSlice(self: *const TerminalTitle) []const u8 {
        return self.app_bytes[0..self.app_len];
    }
    /// Clears any custom title on RIS or when the shell restores a generic shell caption.
    pub fn reset(self: *TerminalTitle) void {
        if (self.len != 0 or self.app_len != 0) {
            self.len = 0;
            self.app_len = 0;
            self.generation +%= 1;
        }
    }
    /// Sanitizes and formats a raw OSC 0 / OSC 2 report; malformed UTF-8 or oversized input is rejected.
    pub fn applyReport(self: *TerminalTitle, raw: []const u8) !void {
        if (raw.len > max_raw_bytes or !std.unicode.utf8ValidateSlice(raw)) return error.InvalidTerminalTitle;
        var sanitized_buf: [max_raw_bytes]u8 = undefined;
        const sanitized = sanitizeSlice(raw, &sanitized_buf);
        const parsed = parseSanitizedTitle(sanitized);
        if (parsed.title.len == 0) {
            self.reset();
            return;
        }
        const append_star = parsed.is_modified and !std.mem.endsWith(u8, parsed.title, "*");
        const max_base: usize = if (append_star) max_title_bytes - 1 else max_title_bytes;
        const base = truncateUtf8(parsed.title, max_base);
        var next_buf: [max_title_bytes]u8 = undefined;
        @memcpy(next_buf[0..base.len], base);
        var next_len = base.len;
        if (append_star) {
            next_buf[next_len] = '*';
            next_len += 1;
        }
        const next_app = if (parsed.app_name) |app| truncateUtf8(app, max_app_bytes) else "";
        if (!std.mem.eql(u8, self.slice(), next_buf[0..next_len]) or !std.mem.eql(u8, self.appSlice(), next_app)) {
            @memcpy(self.bytes[0..next_len], next_buf[0..next_len]);
            self.len = next_len;
            @memcpy(self.app_bytes[0..next_app.len], next_app);
            self.app_len = next_app.len;
            self.generation +%= 1;
        }
    }
};

/// Sanitizes `raw` into `sanitized_buf` and parses the structured title, app name, and modified state.
pub fn parseTitle(raw: []const u8, sanitized_buf: *[TerminalTitle.max_raw_bytes]u8) !ParsedTitle {
    if (raw.len > TerminalTitle.max_raw_bytes or !std.unicode.utf8ValidateSlice(raw)) return error.InvalidTerminalTitle;
    return parseSanitizedTitle(sanitizeSlice(raw, sanitized_buf));
}

/// Extracts the target tool/program name from a raw process command line, stripping runner shims and flags.
pub fn parseCommandLineApp(raw: []const u8) ?[]const u8 {
    const trimmed = std.mem.trim(u8, truncateRedirectionAndPipes(raw), whitespace);
    if (trimmed.len == 0) return null;
    const cleaned = stripCommandRunners(trimmed);
    if (cleaned.len == 0) return null;
    return matchKnownApp(cleaned) orelse cleaned;
}

const whitespace = " \t\r\n\x0b\x0c";
const separators = [_][]const u8{ " - ", " : ", " | ", ": ", "| " };
const shell_prefixes = [_][]const u8{
    "pwsh",    "powershell", "cmd",     "bash",    "zsh",                "fish",           "sh", "nu",
    "nushell", "wsl",        "wslhost", "conhost", "Windows PowerShell", "Command Prompt",
};
const known_apps = [_][]const u8{
    "nvim",   "neovim",      "vim",    "vi",     "nano",   "emacs",       "hx",       "helix",     "micro",     "kak",     "amp",    "lapce",   "code", "cursor", "zed",
    "claude", "claude-code", "codex",  "gemini", "agy",    "antigravity", "opencode", "open-code", "oc",        "pi",      "aider",  "goose",   "qwen", "git",    "lazygit",
    "tig",    "gh",          "ssh",    "mosh",   "docker", "kubectl",     "k9s",      "htop",      "btop",      "btm",     "top",    "glances", "tmux", "zellij", "screen",
    "yazi",   "lf",          "ranger", "nnn",    "mc",     "broot",       "less",     "more",      "bat",       "man",     "cargo",  "rustc",   "go",   "dotnet", "make",
    "cmake",  "just",        "pytest", "jest",   "vitest", "psql",        "sqlite3",  "mysql",     "redis-cli", "mongosh", "ollama", "llm",     "mods", "glow",   "fzf",
    "rg",
};
const runner_prefixes = [_][]const u8{
    "bun", "bunx", "npx", "pnpm", "pnpx", "npm", "yarn", "deno", "node", "python", "python3", "py", "sudo",
};
const runner_subcommands = [_][]const u8{ "run", "exec", "dlx", "x" };
const modified_markers = [_][]const u8{ "[+]", "[*]", "[modified]", "(modified)", "●", "•" };
const script_or_exe_extensions = [_][]const u8{
    ".exe", ".cmd", ".bat", ".ps1", ".mjs", ".cjs", ".mts", ".cts", ".js", ".ts", ".py", ".sh",
};

fn sanitizeSlice(raw: []const u8, out: *[TerminalTitle.max_raw_bytes]u8) []const u8 {
    var len: usize = 0;
    var iter = std.unicode.Utf8View.initUnchecked(raw).iterator();
    while (iter.nextCodepointSlice()) |slice| {
        const cp = std.unicode.utf8Decode(slice) catch unreachable;
        if (cp < 32 or (cp >= 127 and cp <= 159) or isFormatScalar(cp)) continue;
        @memcpy(out[len..][0..slice.len], slice);
        len += slice.len;
    }
    return std.mem.trim(u8, out[0..len], whitespace);
}

fn truncateUtf8(bytes: []const u8, max_len: usize) []const u8 {
    if (bytes.len <= max_len) return bytes;
    var end: usize = 0;
    var iter = std.unicode.Utf8View.initUnchecked(bytes).iterator();
    while (iter.nextCodepointSlice()) |slice| {
        if (end + slice.len > max_len) break;
        end += slice.len;
    }
    return bytes[0..end];
}

fn parseSanitizedTitle(input: []const u8) ParsedTitle {
    var text = std.mem.trim(u8, input, whitespace);
    if (text.len == 0) return .{};

    text = stripKnownTerminalPrefixes(text);
    if (text.len == 0) return .{};

    if (isRootedDirectoryTitle(text)) return .{ .title = extractLeafFolder(text) };

    text = stripStatusPrefix(text);
    if (text.len == 0) return .{};

    if (tryExtractSeparatedTitle(text)) |separated| return separated;

    const standalone_mod = stripModifiedIndicator(text);
    if (standalone_mod.is_modified and standalone_mod.cleaned.len > 0) {
        return .{ .title = cleanExtractedSegment(standalone_mod.cleaned), .is_modified = true };
    }

    text = truncateRedirectionAndPipes(text);
    if (text.len == 0) return .{};

    if (std.mem.indexOfAny(u8, text, " \t") == null and
        (std.mem.indexOfScalar(u8, text, '\\') != null or std.mem.indexOfScalar(u8, text, '/') != null) and
        !hasScriptOrExeExtension(text))
    {
        return .{ .title = extractLeafFolder(text) };
    }

    const first_token = extractFirstToken(text);
    const first_leaf = getFileNameWithoutExe(first_token);
    const first_has_path_or_ext = std.mem.indexOfScalar(u8, first_token, '\\') != null or
        std.mem.indexOfScalar(u8, first_token, '/') != null or
        hasScriptOrExeExtension(first_token);
    if (!first_has_path_or_ext and matchKnownApp(first_leaf) == null and
        std.mem.indexOfAny(u8, text, " \t") != null and !isRunnerPrefix(first_leaf))
    {
        return .{ .title = text };
    }

    const cmd_cleaned = stripCommandRunners(text);
    if (cmd_cleaned.len == 0) return .{};
    const known_from_cmd = matchKnownApp(cmd_cleaned);
    const final_title = known_from_cmd orelse cmd_cleaned;
    return .{ .title = final_title, .app_name = known_from_cmd orelse final_title };
}

fn stripKnownTerminalPrefixes(input: []const u8) []const u8 {
    var text = input;
    const admin_prefix = "Administrator: ";
    if (startsWithIgnoreCase(text, admin_prefix)) {
        text = std.mem.trim(u8, text[admin_prefix.len..], whitespace);
    }
    if (text.len == 0) return "";
    if (std.mem.indexOf(u8, text, " - ")) |dash_idx| {
        const prefix = std.mem.trim(u8, text[0..dash_idx], whitespace);
        if (isShellTitlePrefix(prefix)) {
            text = std.mem.trim(u8, text[dash_idx + 3 ..], whitespace);
            if (isShellTitlePrefix(text)) return "";
            return text;
        }
    }
    if (isShellTitlePrefix(text)) return "";
    return text;
}

fn isShellTitlePrefix(value: []const u8) bool {
    const trimmed = std.mem.trim(u8, value, whitespace);
    if (trimmed.len == 0) return false;
    const leaf = getFileNameWithoutExe(trimmed);
    for (shell_prefixes) |shell| {
        if (std.ascii.eqlIgnoreCase(leaf, shell)) return true;
    }
    return false;
}

fn isRootedDirectoryTitle(text: []const u8) bool {
    const rooted = std.mem.startsWith(u8, text, "/") or
        std.mem.startsWith(u8, text, "\\\\") or
        std.mem.startsWith(u8, text, "~/") or
        std.mem.startsWith(u8, text, "~\\") or
        (text.len >= 3 and std.ascii.isAlphabetic(text[0]) and text[1] == ':' and (text[2] == '\\' or text[2] == '/'));
    if (!rooted or hasScriptOrExeExtension(text)) return false;
    for (separators) |sep| {
        if (std.mem.lastIndexOf(u8, text, sep)) |idx| {
            const right = std.mem.trim(u8, text[idx + sep.len ..], whitespace);
            if (matchKnownApp(right) != null) return false;
        }
    }
    return true;
}

fn stripStatusPrefix(text: []const u8) []const u8 {
    if (text.len > 2 and text[0] == '[') {
        const limit = @min(text.len, 6);
        if (std.mem.indexOfScalarPos(u8, text[0..limit], 1, ']')) |close_bracket| {
            const after = std.mem.trimLeft(u8, text[close_bracket + 1 ..], whitespace);
            const candidates = [_][]const u8{ " | ", " - ", ": " };
            var best_pos: ?usize = null;
            var best_len: usize = 0;
            for (candidates) |sep| {
                if (std.mem.indexOf(u8, after, sep)) |pos| {
                    if (pos > 0 and pos <= 25 and (best_pos == null or pos < best_pos.?)) {
                        best_pos = pos;
                        best_len = sep.len;
                    }
                }
            }
            if (best_pos) |pos| {
                const rest = std.mem.trim(u8, after[pos + best_len ..], whitespace);
                if (rest.len > 0) return rest;
            }
        }
    }
    return text;
}

fn tryExtractSeparatedTitle(text: []const u8) ?ParsedTitle {
    for (separators) |sep| {
        const idx = std.mem.lastIndexOf(u8, text, sep) orelse continue;
        if (idx == 0) continue;
        const left = std.mem.trim(u8, text[0..idx], whitespace);
        const right = std.mem.trim(u8, text[idx + sep.len ..], whitespace);
        if (left.len == 0 or right.len == 0) continue;

        const right_app = matchKnownApp(right);
        const left_clean = stripModifiedIndicator(left);
        const right_clean = stripModifiedIndicator(right);

        if (right_app) |app| {
            if (left_clean.cleaned.len > 0) {
                return .{
                    .title = cleanExtractedSegment(left_clean.cleaned),
                    .app_name = app,
                    .is_modified = left_clean.is_modified,
                };
            }
        }
        if (left_clean.is_modified and !right_clean.is_modified) {
            return .{
                .title = cleanExtractedSegment(left_clean.cleaned),
                .app_name = right_app orelse cleanAppSegment(right),
                .is_modified = true,
            };
        }
        if (looksLikeFileName(left_clean.cleaned) and !looksLikeFileName(right)) {
            return .{
                .title = cleanExtractedSegment(left_clean.cleaned),
                .app_name = right_app orelse cleanAppSegment(right),
                .is_modified = left_clean.is_modified,
            };
        }
    }

    for (separators) |sep| {
        const idx = std.mem.indexOf(u8, text, sep) orelse continue;
        if (idx == 0) continue;
        const left = std.mem.trim(u8, text[0..idx], whitespace);
        const right = std.mem.trim(u8, text[idx + sep.len ..], whitespace);
        if (left.len == 0 or right.len == 0) continue;

        const left_app = matchKnownApp(left);
        const right_clean = stripModifiedIndicator(right);

        if (left_app) |app| {
            if (right_clean.cleaned.len > 0) {
                return .{
                    .title = cleanExtractedSegment(right_clean.cleaned),
                    .app_name = app,
                    .is_modified = right_clean.is_modified,
                };
            }
        }
        if (right_clean.is_modified) {
            return .{
                .title = cleanExtractedSegment(right_clean.cleaned),
                .app_name = cleanAppSegment(left),
                .is_modified = true,
            };
        }
        if (looksLikeFileName(right_clean.cleaned) and !looksLikeFileName(left)) {
            return .{
                .title = cleanExtractedSegment(right_clean.cleaned),
                .app_name = cleanAppSegment(left),
                .is_modified = right_clean.is_modified,
            };
        }
    }

    for (separators) |sep| {
        const idx = std.mem.indexOf(u8, text, sep) orelse continue;
        if (idx == 0) continue;
        const left = std.mem.trim(u8, text[0..idx], whitespace);
        const right = std.mem.trim(u8, text[idx + sep.len ..], whitespace);
        if (isSingleWordToken(left) and right.len > 0) {
            const right_clean = stripModifiedIndicator(right);
            return .{
                .title = cleanExtractedSegment(right_clean.cleaned),
                .app_name = left,
                .is_modified = right_clean.is_modified,
            };
        }
    }
    return null;
}

const ModifiedStrip = struct { cleaned: []const u8, is_modified: bool };

fn stripModifiedIndicator(segment: []const u8) ModifiedStrip {
    const s = std.mem.trim(u8, segment, whitespace);
    for (modified_markers) |marker| {
        if (endsWithIgnoreCase(s, marker)) {
            return .{ .cleaned = std.mem.trimRight(u8, s[0 .. s.len - marker.len], whitespace), .is_modified = true };
        }
        if (startsWithIgnoreCase(s, marker)) {
            return .{ .cleaned = std.mem.trimLeft(u8, s[marker.len..], whitespace), .is_modified = true };
        }
    }
    if (s.len >= 2 and (std.mem.endsWith(u8, s, " *") or std.mem.endsWith(u8, s, " +"))) {
        return .{ .cleaned = std.mem.trimRight(u8, s[0 .. s.len - 2], whitespace), .is_modified = true };
    }
    if (s.len >= 2 and s[s.len - 1] == '*') {
        const without_star = s[0 .. s.len - 1];
        if (looksLikeFileName(without_star)) {
            return .{ .cleaned = without_star, .is_modified = true };
        }
    }
    return .{ .cleaned = s, .is_modified = false };
}

fn cleanExtractedSegment(segment: []const u8) []const u8 {
    var s = std.mem.trim(u8, segment, whitespace);
    if ((std.mem.indexOfScalar(u8, s, '\\') != null or std.mem.indexOfScalar(u8, s, '/') != null) and
        std.mem.indexOfAny(u8, s, " \t") == null)
    {
        if (lastPathSeparator(s)) |last_sep| {
            if (last_sep > 0 and last_sep < s.len - 1) return s[last_sep + 1 ..];
        }
    }
    if (s.len > 0 and s[s.len - 1] == ')') {
        if (std.mem.lastIndexOfScalar(u8, s, '(')) |open_paren| {
            if (open_paren > 0) {
                var before_paren = std.mem.trim(u8, s[0..open_paren], whitespace);
                const in_paren = s[open_paren + 1 .. s.len - 1];
                if ((std.mem.indexOfScalar(u8, in_paren, '/') != null or
                    std.mem.indexOfScalar(u8, in_paren, '\\') != null or
                    std.mem.startsWith(u8, in_paren, "~")) and looksLikeFileName(before_paren))
                {
                    if (lastPathSeparator(before_paren)) |sep| {
                        if (sep < before_paren.len - 1) before_paren = before_paren[sep + 1 ..];
                    }
                    return before_paren;
                }
            }
        }
    }
    return s;
}

fn cleanAppSegment(segment: []const u8) []const u8 {
    const s = std.mem.trim(u8, segment, whitespace);
    if (matchKnownApp(s)) |known| return known;
    return getFileNameWithoutExe(s);
}

fn truncateRedirectionAndPipes(text: []const u8) []const u8 {
    var in_single = false;
    var in_double = false;
    var cut_pos: ?usize = null;
    for (text, 0..) |ch, i| {
        if (ch == '\'' and !in_double) {
            in_single = !in_single;
            continue;
        }
        if (ch == '"' and !in_single) {
            in_double = !in_double;
            continue;
        }
        if (in_single or in_double) continue;
        if (ch == '>' or ch == '<' or ch == '|' or ch == '&') {
            if ((ch == '>' or ch == '<') and i > 0 and (text[i - 1] == '1' or text[i - 1] == '2') and
                (i == 1 or text[i - 2] == ' ' or text[i - 2] == '\t'))
            {
                cut_pos = i - 1;
            } else {
                cut_pos = i;
            }
            break;
        }
    }
    return if (cut_pos) |pos| std.mem.trim(u8, text[0..pos], whitespace) else text;
}

fn stripCommandRunners(text: []const u8) []const u8 {
    var remaining = text;
    while (remaining.len > 0) {
        const next = popNextToken(remaining);
        if (next.token.len == 0) break;
        const leaf = getFileNameWithoutExe(next.token);
        if (std.ascii.eqlIgnoreCase(leaf, "cargo")) {
            const sub = popNextToken(next.rest);
            if (std.ascii.eqlIgnoreCase(sub.token, "run")) return "cargo run";
            break;
        }
        if (isRunnerPrefix(leaf)) {
            remaining = skipRunnerSubcommandsAndFlags(next.rest);
            continue;
        }
        return cleanProgramName(next.token);
    }
    return cleanProgramName(extractFirstToken(text));
}

fn skipRunnerSubcommandsAndFlags(input: []const u8) []const u8 {
    var remaining = std.mem.trimLeft(u8, input, whitespace);
    const first = popNextToken(remaining);
    for (runner_subcommands) |sub| {
        if (std.ascii.eqlIgnoreCase(first.token, sub)) {
            remaining = std.mem.trimLeft(u8, first.rest, whitespace);
            break;
        }
    }
    while (remaining.len > 0) {
        const next = popNextToken(remaining);
        if (next.token.len == 0) break;
        if (std.mem.eql(u8, next.token, "--")) return std.mem.trimLeft(u8, next.rest, whitespace);
        if (next.token[0] == '-') {
            remaining = std.mem.trimLeft(u8, next.rest, whitespace);
            continue;
        }
        break;
    }
    return remaining;
}

fn cleanProgramName(token: []const u8) []const u8 {
    const unquoted = std.mem.trim(u8, token, "\"'");
    if (extractPackageFromNodeModules(unquoted)) |pkg| return pkg;
    var leaf = if (lastPathSeparator(unquoted)) |sep|
        (if (sep < unquoted.len - 1) unquoted[sep + 1 ..] else unquoted)
    else
        unquoted;
    for (script_or_exe_extensions) |ext| {
        if (leaf.len > ext.len and endsWithIgnoreCase(leaf, ext)) {
            leaf = leaf[0 .. leaf.len - ext.len];
            break;
        }
    }
    return leaf;
}

fn extractPackageFromNodeModules(path: []const u8) ?[]const u8 {
    const needle = "node_modules";
    var search_from: usize = 0;
    var nm_idx: ?usize = null;
    while (search_from + needle.len <= path.len) : (search_from += 1) {
        if (std.ascii.eqlIgnoreCase(path[search_from..][0..needle.len], needle)) {
            nm_idx = search_from;
        }
    }
    const idx = nm_idx orelse return null;
    const after_nm = path[idx + needle.len ..];
    if (after_nm.len < 2 or (after_nm[0] != '/' and after_nm[0] != '\\')) return null;

    var iter = std.mem.tokenizeAny(u8, after_nm[1..], "/\\");
    while (iter.next()) |seg| {
        if (std.ascii.eqlIgnoreCase(seg, ".") or std.ascii.eqlIgnoreCase(seg, "..") or
            std.ascii.eqlIgnoreCase(seg, "bin") or std.ascii.eqlIgnoreCase(seg, ".bin") or
            std.ascii.eqlIgnoreCase(seg, "dist") or std.ascii.eqlIgnoreCase(seg, "build") or
            std.ascii.eqlIgnoreCase(seg, "lib") or std.ascii.eqlIgnoreCase(seg, "src") or
            std.ascii.eqlIgnoreCase(seg, "out") or std.ascii.eqlIgnoreCase(seg, "esm") or
            std.ascii.eqlIgnoreCase(seg, "cjs")) continue;
        if (seg[0] == '@') continue;

        var cleaned = seg;
        for (script_or_exe_extensions) |ext| {
            if (cleaned.len > ext.len and endsWithIgnoreCase(cleaned, ext)) {
                cleaned = cleaned[0 .. cleaned.len - ext.len];
                break;
            }
        }
        if (cleaned.len == 0 or
            std.ascii.eqlIgnoreCase(cleaned, "index") or std.ascii.eqlIgnoreCase(cleaned, "cli") or
            std.ascii.eqlIgnoreCase(cleaned, "main") or std.ascii.eqlIgnoreCase(cleaned, "app") or
            std.ascii.eqlIgnoreCase(cleaned, "bin") or std.ascii.eqlIgnoreCase(cleaned, "run")) continue;

        if (startsWithIgnoreCase(cleaned, "pi-")) return "pi";
        return matchKnownApp(cleaned) orelse cleaned;
    }
    return null;
}

fn matchKnownApp(segment: []const u8) ?[]const u8 {
    const s = std.mem.trim(u8, segment, whitespace);
    if (s.len == 0) return null;
    const first_word = if (std.mem.indexOfAny(u8, s, " \t")) |space_idx| s[0..space_idx] else s;
    const cleaned = getFileNameWithoutExe(first_word);
    if (std.ascii.eqlIgnoreCase(cleaned, "opencode") or
        std.ascii.eqlIgnoreCase(cleaned, "open-code") or
        std.ascii.eqlIgnoreCase(cleaned, "oc"))
    {
        return "OpenCode";
    }
    for (known_apps) |app| {
        if (std.ascii.eqlIgnoreCase(cleaned, app)) return cleaned;
    }
    return null;
}

fn isRunnerPrefix(leaf: []const u8) bool {
    for (runner_prefixes) |runner| {
        if (std.ascii.eqlIgnoreCase(leaf, runner)) return true;
    }
    return false;
}

fn hasScriptOrExeExtension(text: []const u8) bool {
    for (script_or_exe_extensions) |ext| {
        if (endsWithIgnoreCase(text, ext)) return true;
    }
    return false;
}

fn looksLikeFileName(token: []const u8) bool {
    const t = std.mem.trim(u8, token, whitespace);
    if (t.len == 0 or std.mem.indexOfAny(u8, t, " \t") != null) return false;
    const name = if (lastPathSeparator(t)) |sep|
        (if (sep < t.len - 1) t[sep + 1 ..] else return false)
    else
        t;
    const dot = std.mem.lastIndexOfScalar(u8, name, '.') orelse return false;
    if (dot == 0 or dot >= name.len - 1) return false;
    const ext = name[dot + 1 ..];
    if (ext.len < 1 or ext.len > 6) return false;
    for (ext) |ch| {
        if (!std.ascii.isAlphanumeric(ch) and ch != '_') return false;
    }
    return true;
}

fn isSingleWordToken(s: []const u8) bool {
    return s.len > 0 and std.mem.indexOfAny(u8, s, " \t") == null;
}

fn extractLeafFolder(path: []const u8) []const u8 {
    const trimmed = std.mem.trimRight(u8, path, "/\\");
    if (trimmed.len == 0) return path;
    if (trimmed.len == 2 and trimmed[1] == ':') return path;
    if (lastPathSeparator(trimmed)) |sep| {
        if (sep < trimmed.len - 1) return trimmed[sep + 1 ..];
    }
    return trimmed;
}

fn getFileNameWithoutExe(token: []const u8) []const u8 {
    const unquoted = std.mem.trim(u8, token, "\"'");
    var leaf = if (lastPathSeparator(unquoted)) |sep|
        (if (sep < unquoted.len - 1) unquoted[sep + 1 ..] else unquoted)
    else
        unquoted;
    if (leaf.len > 4 and endsWithIgnoreCase(leaf, ".exe")) {
        leaf = leaf[0 .. leaf.len - 4];
    }
    return leaf;
}

fn extractFirstToken(text: []const u8) []const u8 {
    return popNextToken(text).token;
}

const TokenSplit = struct { token: []const u8, rest: []const u8 };

fn popNextToken(text: []const u8) TokenSplit {
    const s = std.mem.trimLeft(u8, text, whitespace);
    if (s.len == 0) return .{ .token = "", .rest = "" };
    if (s[0] == '"' or s[0] == '\'') {
        const quote = s[0];
        if (std.mem.indexOfScalarPos(u8, s, 1, quote)) |close_idx| {
            return .{ .token = s[1..close_idx], .rest = std.mem.trimLeft(u8, s[close_idx + 1 ..], whitespace) };
        }
    }
    if (std.mem.indexOfAny(u8, s, " \t")) |space_idx| {
        return .{ .token = s[0..space_idx], .rest = std.mem.trimLeft(u8, s[space_idx + 1 ..], whitespace) };
    }
    return .{ .token = s, .rest = "" };
}

fn lastPathSeparator(bytes: []const u8) ?usize {
    const slash = std.mem.lastIndexOfScalar(u8, bytes, '/');
    const backslash = std.mem.lastIndexOfScalar(u8, bytes, '\\');
    if (slash != null and backslash != null) return @max(slash.?, backslash.?);
    return slash orelse backslash;
}

fn startsWithIgnoreCase(haystack: []const u8, prefix: []const u8) bool {
    return haystack.len >= prefix.len and std.ascii.eqlIgnoreCase(haystack[0..prefix.len], prefix);
}

fn endsWithIgnoreCase(haystack: []const u8, suffix: []const u8) bool {
    return haystack.len >= suffix.len and std.ascii.eqlIgnoreCase(haystack[haystack.len - suffix.len ..], suffix);
}

fn isFormatScalar(codepoint: u21) bool {
    return codepoint == 0x00ad or codepoint == 0x061c or codepoint == 0x180e or
        (codepoint >= 0x200b and codepoint <= 0x200f) or (codepoint >= 0x202a and codepoint <= 0x202e) or
        (codepoint >= 0x2060 and codepoint <= 0x206f) or codepoint == 0xfeff or
        (codepoint >= 0xfff9 and codepoint <= 0xfffb) or (codepoint >= 0x1bca0 and codepoint <= 0x1bca3) or
        (codepoint >= 0x1d173 and codepoint <= 0x1d17a) or codepoint == 0xe0001 or
        (codepoint >= 0xe0020 and codepoint <= 0xe007f);
}

test "terminal title formatter matches WPF shell, editor, agent, path, and runner rules" {
    var buf: [TerminalTitle.max_raw_bytes]u8 = undefined;
    try std.testing.expectEqualStrings("", (try parseTitle("", &buf)).title);
    try std.testing.expectEqualStrings("", (try parseTitle("   ", &buf)).title);
    try std.testing.expectEqualStrings("", (try parseTitle("Administrator: Windows PowerShell", &buf)).title);
    try std.testing.expectEqualStrings("", (try parseTitle("pwsh", &buf)).title);
    try std.testing.expectEqualStrings("", (try parseTitle("C:\\Program Files\\PowerShell\\7\\pwsh.exe", &buf)).title);

    try std.testing.expectEqualStrings("nvim", (try parseTitle("Administrator: pwsh - nvim", &buf)).title);
    try std.testing.expectEqualStrings("lazygit", (try parseTitle("pwsh - lazygit", &buf)).title);

    const nvim_mod = try parseTitle("App.xaml.cs [+] - nvim", &buf);
    try std.testing.expectEqualStrings("App.xaml.cs", nvim_mod.title);
    try std.testing.expectEqualStrings("nvim", nvim_mod.app_name.?);
    try std.testing.expect(nvim_mod.is_modified);

    const nvim_paren = try parseTitle("src/main.rs (~/code/project) - NVIM", &buf);
    try std.testing.expectEqualStrings("main.rs", nvim_paren.title);
    try std.testing.expectEqualStrings("NVIM", nvim_paren.app_name.?);
    try std.testing.expect(!nvim_paren.is_modified);

    const hx = try parseTitle("hx - Cargo.toml [*]", &buf);
    try std.testing.expectEqualStrings("Cargo.toml", hx.title);
    try std.testing.expectEqualStrings("hx", hx.app_name.?);
    try std.testing.expect(hx.is_modified);

    try std.testing.expectEqualStrings("Refactoring auth module", (try parseTitle("[*] Working | Refactoring auth module", &buf)).title);

    const claude = try parseTitle("claude - Fix terminal rendering bug", &buf);
    try std.testing.expectEqualStrings("Fix terminal rendering bug", claude.title);
    try std.testing.expectEqualStrings("claude", claude.app_name.?);

    const opencode = try parseTitle("opencode - Add dark mode support", &buf);
    try std.testing.expectEqualStrings("Add dark mode support", opencode.title);
    try std.testing.expectEqualStrings("OpenCode", opencode.app_name.?);

    try std.testing.expectEqualStrings("OpenCode", (try parseTitle("C:\\Users\\Ziut\\AppData\\Roaming\\npm\\opencode.cmd", &buf)).title);
    try std.testing.expectEqualStrings("pi", (try parseTitle("node C:\\Users\\Ziut\\AppData\\Roaming\\npm\\node_modules\\@mariozechner\\pi-coding-agent\\dist\\cli.js", &buf)).title);
    try std.testing.expectEqualStrings("server", (try parseTitle("bun run dev/server.ts > output.log 2>&1", &buf)).title);
    try std.testing.expectEqualStrings("vitest", (try parseTitle("npx --yes vitest", &buf)).title);
    try std.testing.expectEqualStrings("cargo", (try parseTitle("cargo run --release", &buf)).title);
    try std.testing.expectEqualStrings("vex", (try parseTitle("C:\\Users\\Ziut\\code\\vex", &buf)).title);
    try std.testing.expectEqualStrings("vex", (try parseTitle("~/code/vex/", &buf)).title);
    try std.testing.expectEqualStrings("C:\\", (try parseTitle("C:\\", &buf)).title);
    try std.testing.expectEqualStrings("Fix authentication flow", (try parseTitle("Fix authentication flow", &buf)).title);

    var title: TerminalTitle = .{};
    try title.applyReport("\x07\x1b\u{200b}App.xaml.cs [+] - nvim\u{202e}");
    try std.testing.expectEqualStrings("App.xaml.cs*", title.slice());
    try std.testing.expectEqualStrings("nvim", title.appSlice());
    try title.applyReport("Administrator: Windows PowerShell");
    try std.testing.expectEqualStrings("", title.slice());
    try std.testing.expectEqualStrings("", title.appSlice());

    try std.testing.expectEqualStrings("vitest", parseCommandLineApp("npx --yes vitest --run").?);
    try std.testing.expectEqualStrings("pi", parseCommandLineApp("node C:\\Users\\Ziut\\AppData\\Roaming\\npm\\node_modules\\@mariozechner\\pi-coding-agent\\dist\\cli.js").?);
}
