const std = @import("std");
const terminal_title = @import("terminal_title.zig");
const command_identity = @import("command_line.zig");

/// Visual presentation kind for a resolved terminal pane/tab application icon.
pub const Kind = enum(u8) {
    shell = 0,
    glyph = 1,
    badge = 2,
};

/// Curated vector brand & Lucide tool glyphs for terminal tabs and pane headers.
pub const GlyphId = enum(u8) {
    none = 0,
    alacritty,
    ansible,
    anthropic,
    antigravity,
    astro,
    bun,
    claude,
    claudecode,
    codex,
    cursor,
    deepseek,
    docker,
    fishshell,
    ghostty,
    git,
    gitforwindows,
    github,
    githubcopilot,
    gitlab,
    gnubash,
    go,
    googlecloud,
    googlegemini,
    helix,
    htop,
    httpie,
    javascript,
    kubernetes,
    lazyvim,
    linux,
    mysql,
    neovim,
    nextdotjs,
    nginx,
    nodedotjs,
    npm,
    nushell,
    opencode,
    pi,
    pnpm,
    postgresql,
    powershell,
    python,
    qwen,
    react,
    redis,
    rust,
    sqlite,
    starship,
    svelte,
    terraform,
    tmux,
    typescript,
    vim,
    vite,
    vitest,
    wezterm,
    zedindustries,
    zsh,
    zig,
    dotnet,
    lazygit,
    btop,
    ssh,
    aider,
    emacs,
    nano,
    micro,
    lua,
    ollama,
    yazi,
    zellij,
    uv,
};

/// Value type describing a pane or tab icon (shell prompt, vector brand glyph, or letter badge).
pub const AppIcon = struct {
    kind: Kind = .shell,
    glyph: GlyphId = .none,
    badge_text: [2]u8 = .{ 0, 0 },
    badge_len: u8 = 0,
    color_rgb: u32 = 0x868D96,
    is_shell: bool = true,

    pub const terminal_shell: AppIcon = .{
        .kind = .shell,
        .glyph = .none,
        .color_rgb = 0x868D96,
        .is_shell = true,
    };

    pub fn glyphIcon(id: GlyphId, raw_rgb: u32, is_shell_glyph: bool) AppIcon {
        return .{
            .kind = .glyph,
            .glyph = id,
            .color_rgb = adjustDarkBrandColor(raw_rgb),
            .is_shell = is_shell_glyph,
        };
    }

    pub fn badgeIcon(text: []const u8, rgb: u32) AppIcon {
        var icon: AppIcon = .{
            .kind = .badge,
            .glyph = .none,
            .color_rgb = rgb,
            .is_shell = false,
        };
        const len: usize = @min(text.len, 2);
        @memcpy(icon.badge_text[0..len], text[0..len]);
        icon.badge_len = @intCast(len);
        return icon;
    }

    pub fn badgeSlice(self: *const AppIcon) []const u8 {
        return self.badge_text[0..self.badge_len];
    }

    pub fn eql(self: AppIcon, other: AppIcon) bool {
        return self.kind == other.kind and
            self.glyph == other.glyph and
            self.badge_len == other.badge_len and
            std.mem.eql(u8, self.badge_text[0..self.badge_len], other.badge_text[0..other.badge_len]) and
            self.color_rgb == other.color_rgb and
            self.is_shell == other.is_shell;
    }
};

const GlyphEntry = struct {
    slug: []const u8,
    id: GlyphId,
    rgb: u32,
    is_shell: bool = false,
};

const glyph_catalog = [_]GlyphEntry{
    .{ .slug = "alacritty", .id = .alacritty, .rgb = 0xF46D01 },
    .{ .slug = "ansible", .id = .ansible, .rgb = 0xEE0000 },
    .{ .slug = "anthropic", .id = .anthropic, .rgb = 0x191919 },
    .{ .slug = "antigravity", .id = .antigravity, .rgb = 0x4285F4 },
    .{ .slug = "astro", .id = .astro, .rgb = 0xBC52EE },
    .{ .slug = "bun", .id = .bun, .rgb = 0x000000 },
    .{ .slug = "claude", .id = .claude, .rgb = 0xD97757 },
    .{ .slug = "claudecode", .id = .claudecode, .rgb = 0xD97757 },
    .{ .slug = "codex", .id = .codex, .rgb = 0x7A9DFF },
    .{ .slug = "cursor", .id = .cursor, .rgb = 0x000000 },
    .{ .slug = "deepseek", .id = .deepseek, .rgb = 0x4D6BFE },
    .{ .slug = "docker", .id = .docker, .rgb = 0x2496ED },
    .{ .slug = "fishshell", .id = .fishshell, .rgb = 0x34C534, .is_shell = true },
    .{ .slug = "ghostty", .id = .ghostty, .rgb = 0x3551F3 },
    .{ .slug = "git", .id = .git, .rgb = 0xF03C2E },
    .{ .slug = "gitforwindows", .id = .gitforwindows, .rgb = 0x80B3FF },
    .{ .slug = "github", .id = .github, .rgb = 0x181717 },
    .{ .slug = "githubcopilot", .id = .githubcopilot, .rgb = 0x000000 },
    .{ .slug = "gitlab", .id = .gitlab, .rgb = 0xFC6D26 },
    .{ .slug = "gnubash", .id = .gnubash, .rgb = 0x4EAA25, .is_shell = true },
    .{ .slug = "go", .id = .go, .rgb = 0x00ADD8 },
    .{ .slug = "googlecloud", .id = .googlecloud, .rgb = 0x4285F4 },
    .{ .slug = "googlegemini", .id = .googlegemini, .rgb = 0x8E75B2 },
    .{ .slug = "helix", .id = .helix, .rgb = 0x281733 },
    .{ .slug = "htop", .id = .htop, .rgb = 0x009020 },
    .{ .slug = "httpie", .id = .httpie, .rgb = 0x73DC8C },
    .{ .slug = "javascript", .id = .javascript, .rgb = 0xF7DF1E },
    .{ .slug = "kubernetes", .id = .kubernetes, .rgb = 0x326CE5 },
    .{ .slug = "lazyvim", .id = .lazyvim, .rgb = 0x2E7DE9 },
    .{ .slug = "linux", .id = .linux, .rgb = 0xFCC624 },
    .{ .slug = "mysql", .id = .mysql, .rgb = 0x4479A1 },
    .{ .slug = "neovim", .id = .neovim, .rgb = 0x57A143 },
    .{ .slug = "nextdotjs", .id = .nextdotjs, .rgb = 0x000000 },
    .{ .slug = "nginx", .id = .nginx, .rgb = 0x009639 },
    .{ .slug = "nodedotjs", .id = .nodedotjs, .rgb = 0xB4BAC4 },
    .{ .slug = "npm", .id = .npm, .rgb = 0xCB3837 },
    .{ .slug = "nushell", .id = .nushell, .rgb = 0x4E9A06, .is_shell = true },
    .{ .slug = "opencode", .id = .opencode, .rgb = 0xF1ECEC },
    .{ .slug = "pi", .id = .pi, .rgb = 0xF0EFEF },
    .{ .slug = "pnpm", .id = .pnpm, .rgb = 0xF69220 },
    .{ .slug = "postgresql", .id = .postgresql, .rgb = 0x4169E1 },
    .{ .slug = "powershell", .id = .powershell, .rgb = 0x5391FE, .is_shell = true },
    .{ .slug = "python", .id = .python, .rgb = 0x3776AB },
    .{ .slug = "qwen", .id = .qwen, .rgb = 0x6950EF },
    .{ .slug = "react", .id = .react, .rgb = 0x61DAFB },
    .{ .slug = "redis", .id = .redis, .rgb = 0xFF4438 },
    .{ .slug = "rust", .id = .rust, .rgb = 0x000000 },
    .{ .slug = "sqlite", .id = .sqlite, .rgb = 0x003B57 },
    .{ .slug = "starship", .id = .starship, .rgb = 0xDD0B78 },
    .{ .slug = "svelte", .id = .svelte, .rgb = 0xFF3E00 },
    .{ .slug = "terraform", .id = .terraform, .rgb = 0x844FBA },
    .{ .slug = "tmux", .id = .tmux, .rgb = 0x1BB91F },
    .{ .slug = "typescript", .id = .typescript, .rgb = 0x3178C6 },
    .{ .slug = "vim", .id = .vim, .rgb = 0x019733 },
    .{ .slug = "vite", .id = .vite, .rgb = 0x9135FF },
    .{ .slug = "vitest", .id = .vitest, .rgb = 0x00FF74 },
    .{ .slug = "wezterm", .id = .wezterm, .rgb = 0x4E49EE },
    .{ .slug = "zedindustries", .id = .zedindustries, .rgb = 0x084CCF },
    .{ .slug = "zsh", .id = .zsh, .rgb = 0xF15A24, .is_shell = true },
    .{ .slug = "zig", .id = .zig, .rgb = 0xF7A41D },
    .{ .slug = "dotnet", .id = .dotnet, .rgb = 0x8B6CEF },
    .{ .slug = "lazygit", .id = .lazygit, .rgb = 0x00AADD },
    .{ .slug = "btop", .id = .btop, .rgb = 0x00A896 },
    .{ .slug = "ssh", .id = .ssh, .rgb = 0x2B8CBE },
    .{ .slug = "aider", .id = .aider, .rgb = 0x8B5CF6 },
    .{ .slug = "emacs", .id = .emacs, .rgb = 0x9B72E5 },
    .{ .slug = "nano", .id = .nano, .rgb = 0x4A90E2 },
    .{ .slug = "micro", .id = .micro, .rgb = 0x50E3C2 },
    .{ .slug = "lua", .id = .lua, .rgb = 0x5C8CFF },
    .{ .slug = "ollama", .id = .ollama, .rgb = 0xF0EFEF },
    .{ .slug = "yazi", .id = .yazi, .rgb = 0xF59E0B },
    .{ .slug = "zellij", .id = .zellij, .rgb = 0x10B981 },
    .{ .slug = "uv", .id = .uv, .rgb = 0xDE5FE9 },
};

const AliasEntry = struct {
    alias: []const u8,
    canonical: []const u8,
};

const aliases = [_]AliasEntry{
    .{ .alias = "agy", .canonical = "antigravity" },
    .{ .alias = "gemini", .canonical = "googlegemini" },
    .{ .alias = "gemini-cli", .canonical = "googlegemini" },
    .{ .alias = "oc", .canonical = "opencode" },
    .{ .alias = "open-code", .canonical = "opencode" },
    .{ .alias = "opencode-ai", .canonical = "opencode" },
    .{ .alias = "claude", .canonical = "claudecode" },
    .{ .alias = "claude-code", .canonical = "claudecode" },
    .{ .alias = "codex", .canonical = "codex" },
    .{ .alias = "codex-cli", .canonical = "codex" },
    .{ .alias = "qwen-code", .canonical = "qwen" },
    .{ .alias = "aider-chat", .canonical = "aider" },
    .{ .alias = "pi-coding-agent", .canonical = "pi" },
    .{ .alias = "π", .canonical = "pi" },
    .{ .alias = "nvim", .canonical = "neovim" },
    .{ .alias = "vim", .canonical = "vim" },
    .{ .alias = "gvim", .canonical = "vim" },
    .{ .alias = "vi", .canonical = "vim" },
    .{ .alias = "lazyvim", .canonical = "lazyvim" },
    .{ .alias = "hx", .canonical = "helix" },
    .{ .alias = "helix", .canonical = "helix" },
    .{ .alias = "copilot", .canonical = "githubcopilot" },
    .{ .alias = "zed", .canonical = "zedindustries" },
    .{ .alias = "next", .canonical = "nextdotjs" },
    .{ .alias = "nextjs", .canonical = "nextdotjs" },
    .{ .alias = "http", .canonical = "httpie" },
    .{ .alias = "cargo", .canonical = "rust" },
    .{ .alias = "rustc", .canonical = "rust" },
    .{ .alias = "rustup", .canonical = "rust" },
    .{ .alias = "rustfmt", .canonical = "rust" },
    .{ .alias = "cargo-clippy", .canonical = "rust" },
    .{ .alias = "cargo-nextest", .canonical = "rust" },
    .{ .alias = "py", .canonical = "python" },
    .{ .alias = "python3", .canonical = "python" },
    .{ .alias = "pytest", .canonical = "python" },
    .{ .alias = "ruff", .canonical = "python" },
    .{ .alias = "poetry", .canonical = "python" },
    .{ .alias = "pip", .canonical = "python" },
    .{ .alias = "pip3", .canonical = "python" },
    .{ .alias = "ipython", .canonical = "python" },
    .{ .alias = "uvicorn", .canonical = "python" },
    .{ .alias = "uvx", .canonical = "uv" },
    .{ .alias = "pipx", .canonical = "python" },
    .{ .alias = "luajit", .canonical = "lua" },
    .{ .alias = "lazydocker", .canonical = "docker" },
    .{ .alias = "gh", .canonical = "github" },
    .{ .alias = "kubectl", .canonical = "kubernetes" },
    .{ .alias = "k9s", .canonical = "kubernetes" },
    .{ .alias = "helm", .canonical = "kubernetes" },
    .{ .alias = "psql", .canonical = "postgresql" },
    .{ .alias = "sqlite3", .canonical = "sqlite" },
    .{ .alias = "redis-cli", .canonical = "redis" },
    .{ .alias = "node", .canonical = "nodedotjs" },
    .{ .alias = "nodejs", .canonical = "nodedotjs" },
    .{ .alias = "tsc", .canonical = "typescript" },
    .{ .alias = "tsx", .canonical = "typescript" },
    .{ .alias = "ts-node", .canonical = "typescript" },
    .{ .alias = "nu", .canonical = "nushell" },
    .{ .alias = "pwsh", .canonical = "powershell" },
    .{ .alias = "powershell", .canonical = "powershell" },
    .{ .alias = "bash", .canonical = "gnubash" },
    .{ .alias = "sh", .canonical = "gnubash" },
    .{ .alias = "fish", .canonical = "fishshell" },
};

const shell_icon_names = [_][]const u8{
    "cmd",  "wsl",       "wslhost", "nu",   "nushell", "pwsh", "powershell", "bash",   "sh",     "gnubash",
    "fish", "fishshell", "zsh",     "dash", "ksh",     "ash",  "gitbash",    "system", "elvish", "tcsh",
    "csh",
};

const whitespace = " \t\r\n";

test "shell classification shares the icon catalog and rejects unknown applications" {
    for (shell_icon_names) |name| {
        try std.testing.expect(isShellName(name));
        try std.testing.expect(resolveDirect(name).?.is_shell);
    }
    try std.testing.expect(isShellName("C:\\Program Files\\PowerShell\\7\\PWSH.EXE"));
    try std.testing.expect(!isShellName(""));
    try std.testing.expect(!isShellName("unknown-tool.exe"));
    try std.testing.expect(!isShellName("powershell-editor.exe"));
}

/// Near-black brand colors (rust, bun, github, cursor) vanish on dark chrome; lift to `#B8B8BE`.
pub fn adjustDarkBrandColor(rgb: u32) u32 {
    const r: f64 = @floatFromInt((rgb >> 16) & 0xFF);
    const g: f64 = @floatFromInt((rgb >> 8) & 0xFF);
    const b: f64 = @floatFromInt(rgb & 0xFF);
    const luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
    if (luminance < 0.25) return 0xB8B8BE;
    return rgb;
}

/// True when `name` is a script/VM host (`node`, `bun`, `wsl`, `deno`, etc.) whose command line or title identifies the app.
pub fn isShimHost(name: []const u8) bool {
    const clean = cleanExecutableName(name);
    return command_identity.isCommandHost(clean) or std.ascii.eqlIgnoreCase(clean, "wsl") or std.ascii.eqlIgnoreCase(clean, "wslhost");
}

/// Classifies shells without resolving glyphs or hashing unknown application names.
pub fn isShellName(raw_name: []const u8) bool {
    const clean = cleanExecutableName(raw_name);
    for (shell_icon_names) |name| {
        if (std.ascii.eqlIgnoreCase(clean, name)) return true;
    }
    return false;
}

/// Resolves a known shell, special badge, alias, or brand glyph directly by name. Returns `null` if unknown.
pub fn resolveDirect(raw_name: []const u8) ?AppIcon {
    const clean = cleanExecutableName(raw_name);
    if (clean.len == 0) return null;

    if (isShellName(clean)) return AppIcon.terminal_shell;
    if (packageRuntimeIcon(clean)) |icon| return icon;
    var canonical = if (command_identity.isPythonExecutable(clean)) "python" else clean;
    for (aliases) |entry| {
        if (std.ascii.eqlIgnoreCase(clean, entry.alias)) {
            canonical = entry.canonical;
            break;
        }
    }
    for (glyph_catalog) |entry| {
        if (std.ascii.eqlIgnoreCase(canonical, entry.slug)) {
            return AppIcon.glyphIcon(entry.id, entry.rgb, entry.is_shell);
        }
    }
    return null;
}

/// Runtime fallbacks apply only to known launchers and web tools, never arbitrary executable names.
fn packageRuntimeIcon(name: []const u8) ?AppIcon {
    for ([_][]const u8{ "npm", "pnpm", "yarn", "bun", "npx", "pnpx", "bunx", "corepack", "vite", "next", "nextjs", "nuxt", "astro", "vue-cli-service", "react-scripts" }) |manager| {
        if (std.ascii.eqlIgnoreCase(name, manager)) {
            return AppIcon.glyphIcon(.nodedotjs, 0xB4BAC4, false);
        }
    }
    for ([_][]const u8{ "uv", "uvx", "pip", "pip3", "pipx", "poetry", "pdm" }) |manager| {
        if (std.ascii.eqlIgnoreCase(name, manager)) return AppIcon.glyphIcon(.python, 0x3776AB, false);
    }
    return null;
}

/// Resolves a process name to a catalog icon or a deterministic FNV-1a pastel letter badge.
pub fn fromProcess(process_name: []const u8) AppIcon {
    const clean = cleanExecutableName(process_name);
    if (resolveDirect(clean)) |known| return known;
    const initial: u8 = if (clean.len > 0 and std.ascii.isAlphanumeric(clean[0])) std.ascii.toUpper(clean[0]) else '?';
    const badge = [1]u8{initial};
    return AppIcon.badgeIcon(&badge, hashColor(clean));
}

/// Scans a shim host's command line for the actual tool being executed (e.g. `node .../claude-code/cli.js`).
pub fn fromCommandLine(command_line: []const u8, shim_name: []const u8) ?AppIcon {
    const trimmed = std.mem.trim(u8, command_line, whitespace);
    if (trimmed.len == 0) return null;
    const clean_shim = cleanExecutableName(shim_name);

    if (command_identity.parseCommandLineIdentity(trimmed)) |identity| {
        const app = identity.name;
        if (!std.ascii.eqlIgnoreCase(app, clean_shim)) {
            if (resolveDirect(app)) |icon| {
                if (!icon.is_shell) return icon;
            } else if (app.len > 0) {
                // A user's script is not a new brand; keep the runtime visible like its REPL.
                // Package and module identities still receive their own deterministic badges.
                if (identity.is_local_script) {
                    if (resolveDirect(clean_shim)) |runtime| {
                        if (!runtime.is_shell) return runtime;
                    }
                }
                if (packageRuntimeIcon(clean_shim)) |runtime| return runtime;
                return fromProcess(app);
            }
        }
        if (packageRuntimeIcon(app)) |runtime| return runtime;
    }

    return null;
}

/// Resolves an application icon from a raw or formatted OSC 0 / OSC 2 terminal title.
pub fn fromTitle(raw_title: []const u8) ?AppIcon {
    const trimmed = std.mem.trim(u8, raw_title, whitespace);
    if (trimmed.len == 0) return null;

    var sanitized_buf: [terminal_title.TerminalTitle.max_raw_bytes]u8 = undefined;
    const parsed = terminal_title.parseTitle(trimmed, &sanitized_buf) catch return null;
    if (parsed.app_name) |app| {
        if (resolveDirect(app)) |icon| return icon;
        if (std.mem.indexOfAny(u8, trimmed, " \t")) |space_idx| {
            if (space_idx > 0) {
                if (resolveDirect(trimmed[0..space_idx])) |runner_icon| return runner_icon;
            }
        }
    }
    return null;
}

/// Explicit current OSC 7501 identity wins for any executable, followed by process/launcher metadata.
/// Callers exclude completed stale reports while another foreground program is running.
pub fn resolve(
    process_name: ?[]const u8,
    command_line: ?[]const u8,
    osc_title: ?[]const u8,
    status_app: ?[]const u8,
) AppIcon {
    if (status_app) |raw| {
        const name = std.mem.trim(u8, raw, whitespace);
        if (name.len > 0) return fromProcess(name);
    }
    if (process_name) |proc_raw| {
        const proc_name = std.mem.trim(u8, proc_raw, whitespace);
        if (proc_name.len > 0) {
            // WSL already supplies the decoded app name; retain the launcher for runtime fallbacks.
            const launcher = if (command_line) |cmd| command_identity.commandExecutable(cmd) else proc_name;
            const shim_name = if (isShimHost(launcher)) launcher else proc_name;
            const is_shim = isShimHost(shim_name);
            if (is_shim) {
                if (command_line) |cmd| {
                    if (fromCommandLine(cmd, shim_name)) |shim_icon| return shim_icon;
                }
                if (packageRuntimeIcon(shim_name)) |runtime| return runtime;
                if (osc_title) |title| {
                    if (fromTitle(title)) |shim_title_icon| return shim_title_icon;
                }
            }
            return fromProcess(proc_name);
        }
    }

    if (osc_title) |title| {
        if (fromTitle(title)) |title_icon| return title_icon;
    }

    return AppIcon.terminal_shell;
}

/// Stable pastel color from a process name: 32-bit FNV-1a hash mapped to HSV(hash % 360, 0.55, 0.75).
pub fn hashColor(seed: []const u8) u32 {
    var hash: u32 = 2166136261;
    for (seed) |ch| {
        hash ^= ch;
        hash *%= 16777619;
    }
    return fromHsv(@floatFromInt(hash % 360), 0.55, 0.75);
}

fn fromHsv(hue: f64, saturation: f64, value: f64) u32 {
    const c = value * saturation;
    const h_div = hue / 60.0;
    const h_mod = @mod(h_div, 2.0);
    const x = c * (1.0 - @abs(h_mod - 1.0));
    const m = value - c;
    const rgb = if (hue < 60.0)
        .{ c, x, 0.0 }
    else if (hue < 120.0)
        .{ x, c, 0.0 }
    else if (hue < 180.0)
        .{ 0.0, c, x }
    else if (hue < 240.0)
        .{ 0.0, x, c }
    else if (hue < 300.0)
        .{ x, 0.0, c }
    else
        .{ c, 0.0, x };

    const r: u32 = @intFromFloat((rgb[0] + m) * 255.0);
    const g: u32 = @intFromFloat((rgb[1] + m) * 255.0);
    const b: u32 = @intFromFloat((rgb[2] + m) * 255.0);
    return (r << 16) | (g << 8) | b;
}

fn cleanExecutableName(raw: []const u8) []const u8 {
    const trimmed = std.mem.trim(u8, raw, " \t\r\n\"'");
    if (trimmed.len == 0) return "";
    const leaf = if (lastPathSeparator(trimmed)) |sep|
        (if (sep + 1 < trimmed.len) trimmed[sep + 1 ..] else trimmed)
    else
        trimmed;
    return stripExtension(leaf);
}

fn stripExtension(leaf: []const u8) []const u8 {
    const extensions = [_][]const u8{ ".exe", ".cmd", ".bat", ".ps1", ".mjs", ".cjs", ".js", ".ts", ".py", ".sh" };
    for (extensions) |ext| {
        if (leaf.len > ext.len and std.ascii.eqlIgnoreCase(leaf[leaf.len - ext.len ..], ext)) {
            return leaf[0 .. leaf.len - ext.len];
        }
    }
    return leaf;
}

fn lastPathSeparator(bytes: []const u8) ?usize {
    const slash = std.mem.lastIndexOfScalar(u8, bytes, '/');
    const backslash = std.mem.lastIndexOfScalar(u8, bytes, '\\');
    if (slash != null and backslash != null) return @max(slash.?, backslash.?);
    return slash orelse backslash;
}

test "app icon catalog resolves curated glyphs, aliases, special badges, and dark contrast lift" {
    const nvim = resolveDirect("nvim").?;
    try std.testing.expectEqual(Kind.glyph, nvim.kind);
    try std.testing.expectEqual(GlyphId.neovim, nvim.glyph);
    try std.testing.expectEqual(@as(u32, 0x57A143), nvim.color_rgb);
    try std.testing.expect(!nvim.is_shell);

    const claude = resolveDirect("claude").?;
    try std.testing.expectEqual(GlyphId.claudecode, claude.glyph);
    try std.testing.expectEqual(@as(u32, 0xD97757), claude.color_rgb);

    const agy = resolveDirect("antigravity").?;
    try std.testing.expectEqual(GlyphId.antigravity, agy.glyph);

    const rust = resolveDirect("cargo").?;
    try std.testing.expectEqual(GlyphId.rust, rust.glyph);
    try std.testing.expectEqual(@as(u32, 0xB8B8BE), rust.color_rgb);

    const lazygit = resolveDirect("lazygit").?;
    try std.testing.expectEqual(Kind.glyph, lazygit.kind);
    try std.testing.expectEqual(GlyphId.lazygit, lazygit.glyph);
    try std.testing.expectEqual(@as(u32, 0x00AADD), lazygit.color_rgb);

    const zig_icon = resolveDirect("zig.exe").?;
    try std.testing.expectEqual(Kind.glyph, zig_icon.kind);
    try std.testing.expectEqual(GlyphId.zig, zig_icon.glyph);
    try std.testing.expectEqual(@as(u32, 0xF7A41D), zig_icon.color_rgb);

    const btop_icon = resolveDirect("btop").?;
    try std.testing.expectEqual(GlyphId.btop, btop_icon.glyph);
    for ([_][]const u8{ "deno", "kakoune", "ranger", "lf", "screen", "gitui", "tig", "btm", "mosh" }) |name| {
        try std.testing.expect(resolveDirect(name) == null);
        try std.testing.expectEqual(Kind.badge, fromProcess(name).kind);
    }

    const pwsh = resolveDirect("pwsh.exe").?;
    try std.testing.expectEqual(Kind.shell, pwsh.kind);
    try std.testing.expect(pwsh.is_shell);
}

test "app icon resolution handles shim command lines, OSC titles, and unknown process badges" {
    const claude_shim = resolve(
        "node.exe",
        "\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\Users\\Ziut\\AppData\\Roaming\\npm\\node_modules\\@anthropic-ai\\claude-code\\cli.js\"",
        null,
        null,
    );
    try std.testing.expectEqual(GlyphId.claudecode, claude_shim.glyph);

    const gemini_shim = resolve(
        "node.exe",
        "\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\Users\\Ziut\\AppData\\Roaming\\npm\\node_modules\\@google\\gemini-cli\\dist\\index.js\"",
        null,
        null,
    );
    try std.testing.expectEqual(GlyphId.googlegemini, gemini_shim.glyph);

    const cmd_claude = resolve(
        "cmd.exe",
        "cmd.exe /d /s /c \"\"C:\\Users\\Ziut\\AppData\\Roaming\\npm\\claude.cmd\"\"",
        null,
        null,
    );
    try std.testing.expectEqual(GlyphId.claudecode, cmd_claude.glyph);

    const python_pytest = resolve(
        "python.exe",
        "\"C:\\Python312\\python.exe\" -m pytest tests/",
        null,
        null,
    );
    try std.testing.expectEqual(GlyphId.python, python_pytest.glyph);

    const pi_shim = resolve(
        "node",
        "node C:\\Users\\Ziut\\AppData\\Roaming\\npm\\node_modules\\@mariozechner\\pi-coding-agent\\dist\\cli.js",
        null,
        null,
    );
    try std.testing.expectEqual(GlyphId.pi, pi_shim.glyph);

    const wsl_nvim = resolve("wsl.exe", "wsl.exe -d Ubuntu", "main.zig [+] - nvim", null);
    try std.testing.expectEqual(GlyphId.neovim, wsl_nvim.glyph);

    const bare_node = resolve("node.exe", "node.exe", null, null);
    try std.testing.expectEqual(GlyphId.nodedotjs, bare_node.glyph);

    const unknown_proc = resolve("ripgrep.exe", null, null, null);
    try std.testing.expectEqual(Kind.badge, unknown_proc.kind);
    try std.testing.expectEqualStrings("R", unknown_proc.badgeSlice());
    try std.testing.expect(!unknown_proc.is_shell);
    const unicode_proc = fromProcess("音.exe");
    try std.testing.expectEqualStrings("?", unicode_proc.badgeSlice());

    const idle_shell = resolve(null, null, "C:\\Users\\Ziut\\code\\vex", null);
    try std.testing.expectEqual(Kind.shell, idle_shell.kind);
    try std.testing.expect(idle_shell.is_shell);
    // refreshAppIcon hands the parsed application token back on its own, so the bare glyph must resolve too.
    try std.testing.expectEqual(GlyphId.pi, resolve(null, null, "π - Vex", null).glyph);
    try std.testing.expectEqual(GlyphId.pi, resolve(null, null, "π", null).glyph);
}

test "package commands use runtime icons without becoming idle shells" {
    for ([_][]const u8{
        "npm run dev",           "npm start",                "pnpm dev",                                 "pnpm run dev:web",                                           "yarn serve",                                               "bun run dev",                        "bun start",
        "pnpm --filter web dev", "npm --prefix web run dev", "yarn --cwd web start",                     "cmd /d /s /c npm run dev",                                   "node C:\\npm\\node_modules\\npm\\bin\\npm-cli.js run dev", "node node_modules/vite/bin/vite.js", "npx vite dev",
        "pnpm exec next dev",    "bunx astro dev",           "node node_modules/next/dist/bin/next dev", "node node_modules/react-scripts/bin/react-scripts.js start",
    }) |command| {
        const icon = resolve(command_identity.commandExecutable(command), command, "nvim", null);
        errdefer std.debug.print("dev server command: {s}\n", .{command});
        try std.testing.expectEqual(Kind.glyph, icon.kind);
        try std.testing.expectEqual(GlyphId.nodedotjs, icon.glyph);
        try std.testing.expect(!icon.is_shell);
    }
    for ([_][]const u8{ "npm install", "npm run build", "pnpm test", "yarn lint", "bun run server.ts", "npx next build", "corepack pnpm run build", "pnpx custom-tool" }) |command| {
        const icon = resolve(command_identity.commandExecutable(command), command, "nvim", null);
        try std.testing.expectEqual(Kind.glyph, icon.kind);
        try std.testing.expectEqual(GlyphId.nodedotjs, icon.glyph);
        try std.testing.expect(!icon.is_shell);
    }
    for ([_][]const u8{ "uv run server.py", "uv run uvicorn backend:app", "uvx custom-tool", "poetry run python server.py", "pdm run backend", "pipx run custom-tool" }) |command| {
        try std.testing.expectEqual(GlyphId.python, resolve(command_identity.commandExecutable(command), command, "nvim", null).glyph);
    }
    try std.testing.expectEqual(GlyphId.python, resolve("uvicorn", "uv run uvicorn backend:app", null, null).glyph);
    try std.testing.expectEqual(GlyphId.claudecode, resolve("npx", "npx claude", null, null).glyph);
    try std.testing.expectEqual(GlyphId.neovim, resolve("npm", "npm run dev", null, "nvim").glyph);
    for ([_][]const u8{ "npm", "npm.cmd", "pnpm.exe", "bun.exe", "yarn", "corepack", "vite", "next" }) |process| {
        const icon = resolve(process, null, null, null);
        try std.testing.expectEqual(GlyphId.nodedotjs, icon.glyph);
        try std.testing.expect(!icon.is_shell);
    }
    try std.testing.expectEqual(GlyphId.nodedotjs, resolve("dev", "pnpm run dev", null, null).glyph);
}

test "runtime icons survive local scripts and versioned Python executables" {
    const cases = [_]struct { process: []const u8, command: []const u8, glyph: GlyphId }{
        .{ .process = "python3", .command = "python3 kitty-live-check.py --pause", .glyph = .python },
        .{ .process = "python3.13", .command = "/usr/bin/python3.13 project/server.py", .glyph = .python },
        .{ .process = "python3.13t.exe", .command = "python3.13t.exe -u server.py", .glyph = .python },
        .{ .process = "python.exe", .command = "python.exe -m pytest tests/", .glyph = .python },
        .{ .process = "node.exe", .command = "node.exe \"C:\\My Project\\server.js\"", .glyph = .nodedotjs },
        .{ .process = "nodejs", .command = "nodejs --enable-source-maps server.mjs", .glyph = .nodedotjs },
        .{ .process = "tsx", .command = "tsx server.ts", .glyph = .typescript },
    };
    for (cases) |case| {
        try std.testing.expectEqual(case.glyph, resolve(case.process, case.command, "nvim", null).glyph);
    }
    for ([_][]const u8{ "python", "pythonw.exe", "python3.12", "python3.14t", "C:\\Python313\\PYTHON.EXE" }) |name| {
        try std.testing.expectEqual(GlyphId.python, fromProcess(name).glyph);
    }
    for ([_][]const u8{ "python-editor", "python3.foo", "python3.", "python..3", "pythont" }) |name| {
        try std.testing.expect(resolveDirect(name) == null);
    }

    const module = resolve("python3.13", "python3.13 -m custom_module", null, null);
    try std.testing.expectEqual(Kind.badge, module.kind);
    const package = resolve("node", "node node_modules/@future/custom-agent/dist/cli.js", null, null);
    try std.testing.expectEqual(Kind.badge, package.kind);
    try std.testing.expectEqualStrings("C", package.badgeSlice());
    try std.testing.expectEqual(GlyphId.neovim, resolve("node", "node server.js", null, "nvim").glyph);
}

test "explicit shim app beats stale OSC title and native processes keep their own identity" {
    try std.testing.expectEqual(GlyphId.claudecode, resolve("node.exe", "node.exe", "nvim", "claude-code").glyph);
    try std.testing.expectEqual(GlyphId.neovim, resolve("nvim.exe", null, "claude", null).glyph);
    try std.testing.expectEqual(GlyphId.claudecode, resolve("future-runtime.exe", null, "nvim", "claude-code").glyph);
    const unknown = resolve("custom-tool.exe", null, "nvim", null);
    try std.testing.expectEqual(Kind.badge, unknown.kind);
    try std.testing.expectEqualStrings("C", unknown.badgeSlice());
    try std.testing.expectEqual(GlyphId.antigravity, resolveDirect("antigravity").?.glyph);
    try std.testing.expectEqual(GlyphId.antigravity, resolveDirect("agy").?.glyph);
}
