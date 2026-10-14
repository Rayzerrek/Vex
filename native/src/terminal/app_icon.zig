const std = @import("std");
const terminal_title = @import("terminal_title.zig");

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
    .{ .slug = "nodedotjs", .id = .nodedotjs, .rgb = 0x5FA04E },
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
    .{ .alias = "agy", .canonical = "googlegemini" },
    .{ .alias = "antigravity", .canonical = "googlegemini" },
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
    .{ .alias = "nvim", .canonical = "neovim" },
    .{ .alias = "vim", .canonical = "vim" },
    .{ .alias = "gvim", .canonical = "vim" },
    .{ .alias = "vi", .canonical = "vim" },
    .{ .alias = "lazyvim", .canonical = "lazyvim" },
    .{ .alias = "hx", .canonical = "helix" },
    .{ .alias = "helix", .canonical = "helix" },
    .{ .alias = "kak", .canonical = "micro" },
    .{ .alias = "kakoune", .canonical = "micro" },
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
    .{ .alias = "mosh", .canonical = "ssh" },
    .{ .alias = "btm", .canonical = "btop" },
    .{ .alias = "bottom", .canonical = "btop" },
    .{ .alias = "top", .canonical = "btop" },
    .{ .alias = "glances", .canonical = "btop" },
    .{ .alias = "tig", .canonical = "lazygit" },
    .{ .alias = "gitui", .canonical = "lazygit" },
    .{ .alias = "lazydocker", .canonical = "docker" },
    .{ .alias = "gh", .canonical = "github" },
    .{ .alias = "kubectl", .canonical = "kubernetes" },
    .{ .alias = "k9s", .canonical = "kubernetes" },
    .{ .alias = "helm", .canonical = "kubernetes" },
    .{ .alias = "psql", .canonical = "postgresql" },
    .{ .alias = "sqlite3", .canonical = "sqlite" },
    .{ .alias = "redis-cli", .canonical = "redis" },
    .{ .alias = "lf", .canonical = "yazi" },
    .{ .alias = "ranger", .canonical = "yazi" },
    .{ .alias = "screen", .canonical = "tmux" },
    .{ .alias = "node", .canonical = "nodedotjs" },
    .{ .alias = "nodejs", .canonical = "nodedotjs" },
    .{ .alias = "tsc", .canonical = "typescript" },
    .{ .alias = "tsx", .canonical = "typescript" },
    .{ .alias = "ts-node", .canonical = "typescript" },
    .{ .alias = "deno", .canonical = "typescript" },
    .{ .alias = "nu", .canonical = "nushell" },
    .{ .alias = "pwsh", .canonical = "powershell" },
    .{ .alias = "powershell", .canonical = "powershell" },
    .{ .alias = "bash", .canonical = "gnubash" },
    .{ .alias = "sh", .canonical = "gnubash" },
    .{ .alias = "fish", .canonical = "fishshell" },
};

const SpecialBadgeEntry = struct {
    name: []const u8,
    text: []const u8,
    rgb: u32,
};

const special_badges = [_]SpecialBadgeEntry{};

const shell_icon_names = [_][]const u8{
    "cmd",  "wsl",       "wslhost", "nu",   "nushell", "pwsh", "powershell", "bash", "sh", "gnubash",
    "fish", "fishshell", "zsh",     "dash", "ksh",     "ash",  "gitbash",    "system",
};

const shim_hosts = [_][]const u8{
    "node",    "nodejs", "deno",   "wsl", "wslhost", "bun",        "bunx", "tsx",  "ts-node", "npx", "pnpm", "pnpx", "yarn", "npm",
    "python",  "python3", "py",    "uv",  "uvx",     "pipx",       "cmd",  "pwsh", "powershell", "bash", "sh",
};

const editor_hosts = [_][]const u8{
    "nvim", "neovim", "vim", "gvim", "hx", "helix", "nano", "micro", "emacs", "kak", "kakoune",
};

const command_line_noise = [_][]const u8{
    "npm", "npx", "node_modules", "bin", "lib", "cli", "scripts", "cmd",
};

const whitespace = " \t\r\n";

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
    for (shim_hosts) |shim| {
        if (std.ascii.eqlIgnoreCase(clean, shim)) return true;
    }
    return false;
}

/// True when `name` is an editor (`nvim`, `hx`, `vim`, `emacs`, etc.) whose child LSP workers must not steal the tab icon.
pub fn isEditorHost(name: []const u8) bool {
    const clean = cleanExecutableName(name);
    for (editor_hosts) |editor| {
        if (std.ascii.eqlIgnoreCase(clean, editor)) return true;
    }
    return false;
}

/// Resolves a known shell, special badge, alias, or brand glyph directly by name. Returns `null` if unknown.
pub fn resolveDirect(raw_name: []const u8) ?AppIcon {
    const clean = cleanExecutableName(raw_name);
    if (clean.len == 0) return null;

    for (shell_icon_names) |shell_name| {
        if (std.ascii.eqlIgnoreCase(clean, shell_name)) return AppIcon.terminal_shell;
    }
    for (special_badges) |badge| {
        if (std.ascii.eqlIgnoreCase(clean, badge.name)) return AppIcon.badgeIcon(badge.text, badge.rgb);
    }
    var canonical = clean;
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

/// Resolves a process name to a catalog icon or a deterministic FNV-1a pastel letter badge.
pub fn fromProcess(process_name: []const u8) AppIcon {
    const clean = cleanExecutableName(process_name);
    if (resolveDirect(clean)) |known| return known;
    const initial: u8 = if (clean.len > 0) std.ascii.toUpper(clean[0]) else '?';
    const badge = [1]u8{initial};
    return AppIcon.badgeIcon(&badge, hashColor(clean));
}

/// Scans a shim host's command line for the actual tool being executed (e.g. `node .../claude-code/cli.js`).
pub fn fromCommandLine(command_line: []const u8, shim_name: []const u8) ?AppIcon {
    const trimmed = std.mem.trim(u8, command_line, whitespace);
    if (trimmed.len == 0) return null;
    const clean_shim = cleanExecutableName(shim_name);

    if (terminal_title.parseCommandLineApp(trimmed)) |app| {
        if (!std.ascii.eqlIgnoreCase(app, clean_shim)) {
            if (resolveDirect(app)) |icon| {
                if (!icon.is_shell) return icon;
            } else if (isShellIconName(clean_shim) and app.len > 0 and std.ascii.isAlphabetic(app[0])) {
                return fromProcess(app);
            }
        }
    }

    const needle = "node_modules";
    var search_from: usize = 0;
    var nm_idx: ?usize = null;
    while (search_from + needle.len <= trimmed.len) : (search_from += 1) {
        if (std.ascii.eqlIgnoreCase(trimmed[search_from..][0..needle.len], needle)) {
            nm_idx = search_from;
            break;
        }
    }
    const idx = nm_idx orelse return null;
    const after_nm = trimmed[idx + needle.len ..];

    var token_iter = std.mem.tokenizeAny(u8, after_nm, " \t\"'/\\");
    while (token_iter.next()) |raw_seg| {
        const seg = stripExtension(raw_seg);
        if (seg.len == 0 or std.ascii.eqlIgnoreCase(seg, clean_shim) or isCommandLineNoise(seg)) continue;
        if (resolveDirect(seg)) |icon| {
            if (!icon.is_shell) return icon;
        }
        if (seg.len >= 3 and std.ascii.eqlIgnoreCase(seg[0..3], "pi-")) return resolveDirect("pi");
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

/// Resolves a pane's icon using the .NET `AppIconTracker` + `AppIconCatalog.Resolve` priority hierarchy:
/// 1. Active non-shell foreground process (shim command line -> OSC title -> catalog -> letter badge).
/// 2. OSC 777 `status_app` report when no foreground child process overrides it.
/// 3. OSC 0 / OSC 2 `osc_title` application name (for WSL, remote SSH, or shell builtins).
/// 4. Default terminal shell prompt icon (`>_`).
pub fn resolve(
    process_name: ?[]const u8,
    command_line: ?[]const u8,
    osc_title: ?[]const u8,
    status_app: ?[]const u8,
) AppIcon {
    if (process_name) |proc_raw| {
        const proc_name = std.mem.trim(u8, proc_raw, whitespace);
        if (proc_name.len > 0) {
            const is_shim = isShimHost(proc_name);
            if (is_shim) {
                if (command_line) |cmd| {
                    if (fromCommandLine(cmd, proc_name)) |shim_icon| return shim_icon;
                }
                if (osc_title) |title| {
                    if (fromTitle(title)) |shim_title_icon| return shim_title_icon;
                }
                if (status_app) |status_name| {
                    if (resolveDirect(status_name)) |status_icon| {
                        if (!status_icon.is_shell) return status_icon;
                    }
                }
            }
            if (resolveDirect(proc_name)) |known| {
                if (!known.is_shell) return known;
            }
            if (!is_shim) {
                if (osc_title) |title| {
                    if (fromTitle(title)) |title_icon| return title_icon;
                }
                return fromProcess(proc_name);
            }
            if (resolveDirect(proc_name)) |known_shim| return known_shim;
            return AppIcon.terminal_shell;
        }
    }

    if (status_app) |status_raw| {
        const status_trimmed = std.mem.trim(u8, status_raw, whitespace);
        if (status_trimmed.len > 0) {
            if (resolveDirect(status_trimmed)) |icon| {
                if (!icon.is_shell) return icon;
            }
            return fromProcess(status_trimmed);
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

fn isCommandLineNoise(segment: []const u8) bool {
    for (command_line_noise) |noise| {
        if (std.ascii.eqlIgnoreCase(segment, noise)) return true;
    }
    return false;
}

fn isShellIconName(clean: []const u8) bool {
    for (shell_icon_names) |shell_name| {
        if (std.ascii.eqlIgnoreCase(clean, shell_name)) return true;
    }
    return false;
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
    try std.testing.expectEqual(GlyphId.googlegemini, agy.glyph);

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

    const btop_icon = resolveDirect("btm").?;
    try std.testing.expectEqual(GlyphId.btop, btop_icon.glyph);

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

    const idle_shell = resolve(null, null, "C:\\Users\\Ziut\\code\\vex", null);
    try std.testing.expectEqual(Kind.shell, idle_shell.kind);
    try std.testing.expect(idle_shell.is_shell);
}

