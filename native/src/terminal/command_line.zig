const std = @import("std");

const whitespace = " \t\r\n";
const Mode = enum { shell, powershell, script, python, package, subcommand };
const Host = struct { name: []const u8, mode: Mode };
// These are command syntaxes, not a list of supported target applications.
const hosts = [_]Host{
    .{ .name = "cmd", .mode = .shell },
    .{ .name = "bash", .mode = .shell },
    .{ .name = "sh", .mode = .shell },
    .{ .name = "pwsh", .mode = .powershell },
    .{ .name = "powershell", .mode = .powershell },
    .{ .name = "node", .mode = .script },
    .{ .name = "nodejs", .mode = .script },
    .{ .name = "bun", .mode = .subcommand },
    .{ .name = "deno", .mode = .subcommand },
    .{ .name = "tsx", .mode = .script },
    .{ .name = "ts-node", .mode = .script },
    .{ .name = "python", .mode = .python },
    .{ .name = "python3", .mode = .python },
    .{ .name = "py", .mode = .python },
    .{ .name = "npx", .mode = .package },
    .{ .name = "bunx", .mode = .package },
    .{ .name = "pnpx", .mode = .package },
    .{ .name = "uvx", .mode = .package },
    .{ .name = "pnpm", .mode = .subcommand },
    .{ .name = "npm", .mode = .subcommand },
    .{ .name = "yarn", .mode = .subcommand },
    .{ .name = "uv", .mode = .subcommand },
    .{ .name = "pipx", .mode = .subcommand },
};

/// True only for launchers whose argument syntax this module can decode, regardless of target name.
pub fn isCommandHost(name: []const u8) bool {
    return hostMode(executableStem(name)) != null;
}

/// Borrows an executable/script stem; does not canonicalize application or package names.
pub fn executableStem(raw: []const u8) []const u8 {
    const value = std.mem.trim(u8, raw, " \t\r\n\"'");
    var start: usize = 0;
    for (value, 0..) |byte, index| if (byte == '/' or byte == '\\') {
        start = index + 1;
    };
    const leaf = value[start..];
    for ([_][]const u8{ ".exe", ".com", ".cmd", ".bat", ".ps1", ".mjs", ".cjs", ".js", ".ts", ".py", ".sh" }) |ext| {
        if (leaf.len > ext.len and std.ascii.endsWithIgnoreCase(leaf, ext)) return leaf[0 .. leaf.len - ext.len];
    }
    return leaf;
}

/// Decodes launcher structure without application allowlists or allocations; ambiguous options retain the host.
pub fn parseCommandLineApp(raw: []const u8) ?[]const u8 {
    if (raw.len == 0 or raw.len > 32768 or !std.unicode.utf8ValidateSlice(raw)) return null;
    var remaining = std.mem.trim(u8, raw, whitespace);
    for (0..8) |_| {
        const first = nextToken(remaining);
        const name = programName(first.token);
        if (name.len == 0) return null;
        const mode = hostMode(name) orelse return name;
        var rest = first.rest;
        while (rest.len > 0) {
            // cmd /s /c wraps the entire quoted command in one extra quote pair.
            if (mode == .shell and std.mem.startsWith(u8, rest, "\"\"")) {
                rest = rest[1..];
                if (std.mem.endsWith(u8, rest, "\"")) rest = rest[0 .. rest.len - 1];
            }
            const arg = nextToken(rest);
            if (arg.token.len == 0) return name;
            if (std.mem.eql(u8, arg.token, "--")) {
                rest = arg.rest;
                break;
            }
            if (mode == .powershell) {
                if (equals(arg.token, "-File") or equals(arg.token, "-f")) return programName(nextToken(arg.rest).token);
                // Inline shell source is not an application identity.
                if (equals(arg.token, "-Command") or equals(arg.token, "-c") or equals(arg.token, "-EncodedCommand")) return name;
            }
            if (mode == .python and std.mem.eql(u8, arg.token, "-m")) {
                const module = nextToken(arg.rest).token;
                return if (module.len > 0) module else name;
            }
            if (mode == .shell and (equals(arg.token, "/c") or equals(arg.token, "/k") or equals(arg.token, "-c"))) {
                rest = std.mem.trim(u8, arg.rest, whitespace);
                if (rest.len >= 2 and rest[0] == '\'' and rest[rest.len - 1] == '\'') rest = rest[1 .. rest.len - 1];
                break;
            }
            if (mode == .subcommand and (equals(arg.token, "exec") or equals(arg.token, "dlx") or equals(arg.token, "x") or equals(arg.token, "tool"))) {
                rest = arg.rest;
                continue;
            }
            if (mode == .subcommand and equals(arg.token, "run")) {
                // npm scripts are arbitrary labels, not executable identities.
                if (equals(name, "npm") or equals(name, "pnpm") or equals(name, "yarn")) return name;
                rest = arg.rest;
                continue;
            }
            if (arg.token[0] == '-' or (mode == .shell and arg.token[0] == '/')) {
                if (takesValue(mode, arg.token)) {
                    const value = nextToken(arg.rest);
                    if (value.token.len == 0) return name;
                    rest = value.rest;
                    continue;
                }
                if (!isFlag(mode, arg.token)) return name;
                rest = arg.rest;
                continue;
            }
            break;
        }
        if (rest.len == 0) return name;
        if (mode == .shell and std.mem.startsWith(u8, rest, "\"\"")) {
            rest = rest[1..];
            if (std.mem.endsWith(u8, rest, "\"")) rest = rest[0 .. rest.len - 1];
        }
        remaining = rest;
    }
    return null;
}

/// Only known interactive switches prove a shell is idle; source/options we cannot decode retain close protection.
pub fn shellHasCommand(raw: []const u8) bool {
    if (raw.len == 0 or !std.unicode.utf8ValidateSlice(raw)) return true;
    const first = nextToken(raw);
    if (first.token.len == 0) return true;
    const name = executableStem(first.token);
    const powershell = equals(name, "pwsh") or equals(name, "powershell");
    const cmd = equals(name, "cmd");
    var rest = first.rest;
    while (rest.len > 0) {
        const argument = nextToken(rest);
        const flag = argument.token;
        if (flag.len == 0) return true;
        if (cmd) {
            if (equals(flag, "/k")) return argument.rest.len > 0;
            if (!equals(flag, "/d") and !equals(flag, "/q") and !equals(flag, "/a") and !equals(flag, "/u") and
                !equals(flag, "/s") and !equals(flag, "/e:on") and !equals(flag, "/e:off") and
                !equals(flag, "/v:on") and !equals(flag, "/v:off")) return true;
        } else if (powershell) {
            if (equals(flag, "-ExecutionPolicy") or equals(flag, "-ep") or equals(flag, "-WorkingDirectory") or equals(flag, "-wd")) {
                const value = nextToken(argument.rest);
                if (value.token.len == 0) return true;
                rest = value.rest;
                continue;
            }
            if (!equals(flag, "-NoLogo") and !equals(flag, "-NoProfile") and !equals(flag, "-NoExit") and !equals(flag, "-Interactive")) return true;
        } else {
            if (equals(flag, "--login") or equals(flag, "--interactive") or equals(flag, "--noprofile") or equals(flag, "--norc") or equals(flag, "--no-config-file")) {
                rest = argument.rest;
                continue;
            }
            // Combined login/interactive flags are common in Git Bash and nested Unix shells.
            if (flag.len < 2 or flag[0] != '-') return true;
            for (flag[1..]) |byte| if (byte != 'l' and byte != 'i') return true;
        }
        rest = argument.rest;
    }
    return false;
}

fn hostMode(name: []const u8) ?Mode {
    for (hosts) |host| if (equals(name, host.name)) return host.mode;
    return null;
}
fn equals(a: []const u8, b: []const u8) bool {
    return std.ascii.eqlIgnoreCase(a, b);
}
fn takesValue(mode: Mode, value: []const u8) bool {
    return (mode == .package and (equals(value, "-p") or equals(value, "--package"))) or
        (mode == .python and (equals(value, "-X") or equals(value, "-W")));
}
fn isFlag(mode: Mode, value: []const u8) bool {
    const flags: []const []const u8 = switch (mode) {
        .shell => &.{ "/d", "/s", "/q", "--login", "-l" },
        .powershell => &.{ "-NoProfile", "-NonInteractive", "-NoLogo" },
        .python => &.{ "-u", "-B", "-I", "-S", "-O", "-OO", "-E", "-s" },
        .package, .subcommand => &.{ "--yes", "-y", "--no-install", "--silent" },
        .script => &.{ "--enable-source-maps", "--no-warnings" },
    };
    for (flags) |flag| if (equals(value, flag)) return true;
    return false;
}
const Token = struct { token: []const u8, rest: []const u8 };
fn nextToken(raw: []const u8) Token {
    const text = std.mem.trimLeft(u8, raw, whitespace);
    if (text.len == 0) return .{ .token = "", .rest = "" };
    if (text[0] == '"' or text[0] == '\'') {
        const end = std.mem.indexOfScalarPos(u8, text, 1, text[0]) orelse return .{ .token = "", .rest = "" };
        return .{ .token = text[1..end], .rest = std.mem.trimLeft(u8, text[end + 1 ..], whitespace) };
    }
    const end = std.mem.indexOfAny(u8, text, " \t\r\n|&<>") orelse text.len;
    return .{ .token = text[0..end], .rest = std.mem.trimLeft(u8, text[end..], whitespace) };
}
fn programName(raw: []const u8) []const u8 {
    // The owning package, not cli.js/index.js or a recognized brand, identifies npm scripts.
    var parts = std.mem.tokenizeAny(u8, raw, "/\\");
    var package: ?[]const u8 = null;
    while (parts.next()) |part| {
        if (!equals(part, "node_modules")) continue;
        const next = parts.next() orelse break;
        package = if (equals(next, ".bin")) null else if (next[0] == '@') parts.next() else next;
    }
    return if (package) |name| name else executableStem(raw);
}

test "interactive shell switches do not hide scripts or inline shell commands" {
    for ([_][]const u8{ "pwsh", "powershell.exe -NoLogo -NoProfile", "cmd /d /q /k", "bash --login -i", "fish -i", "nu", "zsh -il", "bash --noprofile --norc -il", "pwsh -NoLogo -ExecutionPolicy Bypass -WorkingDirectory 'C:\\My Projects'", "cmd /d /v:on /k" }) |raw| {
        try std.testing.expect(!shellHasCommand(raw));
    }
    for ([_][]const u8{ "", "pwsh -Command Start-Sleep", "powershell -NoExit -EncodedCommand AAAA", "bash -c 'sleep 100'", "fish script.fish", "cmd /k task.cmd", "nu --commands sleep", "zsh --unknown-option", "pwsh -File powershell.ps1", "pwsh -c sleep", "cmd -i", "bash -NoProfile", "pwsh -ExecutionPolicy", "zsh -lic 'sleep 100'" }) |raw| {
        try std.testing.expect(shellHasCommand(raw));
    }
}

test "command identity decodes unknown targets and keeps full package names" {
    const cases = [_][2][]const u8{
        .{ "new-future-tool.exe --agent claude", "new-future-tool" },
        .{ "node C:\\npm\\node_modules\\@future\\unreleased-tool\\dist\\cli.js", "unreleased-tool" },
        .{ "node C:\\npm\\node_modules\\@mariozechner\\pi-coding-agent\\dist\\cli.js", "pi-coding-agent" },
        .{ "python.exe -u -m never_seen_before", "never_seen_before" },
        .{ "npx --yes future-agent", "future-agent" },
        .{ "node project/node_modules/.bin/future-agent.js", "future-agent" },
        .{ "uv tool run future-linter check .", "future-linter" },
        .{ "cmd /d /s /c \"\"C:\\My Tools\\future.cmd\" --help\"", "future" },
        .{ "cargo run --release", "cargo" },
        .{ "powershell.exe -NoProfile -Command \"nvim\"", "powershell" },
        .{ "node --eval \"require('claude')\"", "node" },
        .{ "node --unknown-option misleading.py tool.js", "node" },
        .{ "npm run future-agent", "npm" },
    };
    for (cases) |case| try std.testing.expectEqualStrings(case[1], parseCommandLineApp(case[0]) orelse return error.MissingCommandIdentity);
    try std.testing.expectEqual(@as(?[]const u8, null), parseCommandLineApp("\xff"));
}
