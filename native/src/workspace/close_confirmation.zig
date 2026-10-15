const std = @import("std");
const app_icon = @import("../terminal/app_icon.zig");

/// Whether the close action targets a single split pane or an entire tab.
pub const CloseTargetKind = enum(u8) {
    pane = 0,
    tab = 1,

    pub fn noun(self: CloseTargetKind) []const u8 {
        return switch (self) {
            .pane => "pane",
            .tab => "tab",
        };
    }

    pub fn closeButtonText(self: CloseTargetKind) []const u8 {
        return switch (self) {
            .pane => "Close Pane",
            .tab => "Close Tab",
        };
    }
};

/// Severity classification used to prioritize warnings across multi-pane tabs.
pub const ConfirmationReason = enum(u8) {
    foreground_process = 1,
    agent_session = 2,
    dirty_buffer = 3,
};

/// Snapshot of a single terminal pane's state inspected before closing.
pub const PaneCloseSnapshot = struct {
    is_running: bool,
    title: []const u8 = "",
    app_name: []const u8 = "",
    status_app: []const u8 = "",
    has_foreground_process: bool = false,
    active_process_title: []const u8 = "",
    icon: app_icon.AppIcon = app_icon.AppIcon.terminal_shell,
};

/// Bounded value type describing a rendered close-confirmation modal dialog.
pub const CloseConfirmation = struct {
    reason: ConfirmationReason,
    target_kind: CloseTargetKind,
    title_buf: [96]u8 = undefined,
    title_len: usize = 0,
    message_buf: [224]u8 = undefined,
    message_len: usize = 0,
    confirm_buf: [32]u8 = undefined,
    confirm_len: usize = 0,
    icon: app_icon.AppIcon = app_icon.AppIcon.terminal_shell,

    pub fn titleSlice(self: *const CloseConfirmation) []const u8 {
        return self.title_buf[0..self.title_len];
    }

    pub fn messageSlice(self: *const CloseConfirmation) []const u8 {
        return self.message_buf[0..self.message_len];
    }

    pub fn confirmSlice(self: *const CloseConfirmation) []const u8 {
        return self.confirm_buf[0..self.confirm_len];
    }
};

const AgentEntry = struct {
    alias: []const u8,
    display_name: []const u8,
};

const known_agents = [_]AgentEntry{
    .{ .alias = "pi", .display_name = "Pi" },
    .{ .alias = "claude", .display_name = "Claude Code" },
    .{ .alias = "claudecode", .display_name = "Claude Code" },
    .{ .alias = "claude-code", .display_name = "Claude Code" },
    .{ .alias = "agy", .display_name = "Antigravity" },
    .{ .alias = "antigravity", .display_name = "Antigravity" },
    .{ .alias = "gemini", .display_name = "Gemini" },
    .{ .alias = "gemini-cli", .display_name = "Gemini" },
    .{ .alias = "googlegemini", .display_name = "Gemini" },
    .{ .alias = "oc", .display_name = "OpenCode" },
    .{ .alias = "opencode", .display_name = "OpenCode" },
    .{ .alias = "open-code", .display_name = "OpenCode" },
    .{ .alias = "opencode-ai", .display_name = "OpenCode" },
    .{ .alias = "codex", .display_name = "Codex" },
    .{ .alias = "codex-cli", .display_name = "Codex" },
    .{ .alias = "aider", .display_name = "Aider" },
    .{ .alias = "aider-chat", .display_name = "Aider" },
    .{ .alias = "cursor", .display_name = "Cursor" },
    .{ .alias = "copilot", .display_name = "GitHub Copilot" },
    .{ .alias = "githubcopilot", .display_name = "GitHub Copilot" },
    .{ .alias = "deepseek", .display_name = "DeepSeek" },
    .{ .alias = "qwen", .display_name = "Qwen Code" },
    .{ .alias = "qwen-code", .display_name = "Qwen Code" },
    .{ .alias = "goose", .display_name = "Goose" },
};

const EditorEntry = struct {
    alias: []const u8,
    display_name: []const u8,
};

const known_editors = [_]EditorEntry{
    .{ .alias = "nvim", .display_name = "Neovim" },
    .{ .alias = "neovim", .display_name = "Neovim" },
    .{ .alias = "vim", .display_name = "Vim" },
    .{ .alias = "gvim", .display_name = "Vim" },
    .{ .alias = "vi", .display_name = "Vim" },
    .{ .alias = "lazyvim", .display_name = "LazyVim" },
    .{ .alias = "hx", .display_name = "Helix" },
    .{ .alias = "helix", .display_name = "Helix" },
    .{ .alias = "nano", .display_name = "Nano" },
    .{ .alias = "micro", .display_name = "Micro" },
    .{ .alias = "kak", .display_name = "Kakoune" },
    .{ .alias = "kakoune", .display_name = "Kakoune" },
    .{ .alias = "emacs", .display_name = "Emacs" },
    .{ .alias = "opencode", .display_name = "OpenCode" },
    .{ .alias = "open-code", .display_name = "OpenCode" },
    .{ .alias = "oc", .display_name = "OpenCode" },
};

/// Returns true when `raw_name` identifies a known coding agent CLI.
pub fn isAgent(raw_name: []const u8) bool {
    return resolveAgentDisplayName(raw_name) != null;
}

/// Maps a known coding agent executable/title slug to its user-facing display name.
pub fn resolveAgentDisplayName(raw_name: []const u8) ?[]const u8 {
    const clean = std.mem.trim(u8, raw_name, " \t\r\n");
    if (clean.len == 0) return null;
    for (known_agents) |entry| {
        if (std.ascii.eqlIgnoreCase(clean, entry.alias)) return entry.display_name;
    }
    if (clean.len >= 3 and std.ascii.eqlIgnoreCase(clean[0..3], "pi-")) return "Pi";
    return null;
}

/// Evaluates whether closing a single pane requires user confirmation.
pub fn evaluatePane(snapshot: PaneCloseSnapshot, target_kind: CloseTargetKind) ?CloseConfirmation {
    if (!snapshot.is_running) return null;

    // 1. Unsaved editor buffer (highest priority: data loss risk)
    const trimmed_title = std.mem.trim(u8, snapshot.title, " \t\r\n");
    if (trimmed_title.len > 0 and trimmed_title[trimmed_title.len - 1] == '*') {
        const clean_file = std.mem.trimRight(u8, trimmed_title[0 .. trimmed_title.len - 1], " \t*");
        const display_file = if (clean_file.len > 0) clean_file else "buffer";
        const raw_editor = if (snapshot.app_name.len > 0) snapshot.app_name else snapshot.active_process_title;
        var editor_buf: [48]u8 = undefined;
        const editor_name = formatEditorDisplayName(raw_editor, &editor_buf);

        var result: CloseConfirmation = .{
            .reason = .dirty_buffer,
            .target_kind = target_kind,
            .icon = resolveConfirmationIcon(snapshot, raw_editor),
        };
        if (editor_name.len > 0) {
            setFormatted(&result.title_buf, &result.title_len, "Unsaved Changes in {s}", .{editor_name});
        } else {
            setFormatted(&result.title_buf, &result.title_len, "Unsaved Changes", .{});
        }
        setFormatted(
            &result.message_buf,
            &result.message_len,
            "\"{s}\" has unsaved buffer changes. Closing this {s} will discard them.",
            .{ display_file, target_kind.noun() },
        );
        setFormatted(&result.confirm_buf, &result.confirm_len, "Discard & Close", .{});
        return result;
    }

    // 2. Active foreground process (agent or general tool)
    const fg_name = std.mem.trim(u8, snapshot.active_process_title, " \t\r\n");
    if (snapshot.has_foreground_process and fg_name.len > 0) {
        if (resolveAgentDisplayName(fg_name) orelse
            resolveAgentDisplayName(snapshot.app_name) orelse
            resolveAgentDisplayName(snapshot.status_app)) |agent_display|
        {
            const icon_seed = if (resolveAgentDisplayName(fg_name) != null) fg_name else if (snapshot.app_name.len > 0) snapshot.app_name else snapshot.status_app;
            return makeAgentConfirmation(snapshot, target_kind, agent_display, icon_seed);
        }

        var result: CloseConfirmation = .{
            .reason = .foreground_process,
            .target_kind = target_kind,
            .icon = resolveConfirmationIcon(snapshot, fg_name),
        };
        setFormatted(&result.title_buf, &result.title_len, "{s} is running", .{fg_name});
        setFormatted(
            &result.message_buf,
            &result.message_len,
            "Process \"{s}\" is actively running in this {s}. Closing will terminate it.",
            .{ fg_name, target_kind.noun() },
        );
        setFormatted(&result.confirm_buf, &result.confirm_len, "{s}", .{target_kind.closeButtonText()});
        return result;
    }

    // 3. Title-based or OSC 777 status-based agent detection (e.g. WSL / remote SSH agent)
    if (resolveAgentDisplayName(snapshot.app_name)) |agent_display| {
        return makeAgentConfirmation(snapshot, target_kind, agent_display, snapshot.app_name);
    }
    if (resolveAgentDisplayName(snapshot.status_app)) |agent_display| {
        return makeAgentConfirmation(snapshot, target_kind, agent_display, snapshot.status_app);
    }

    return null;
}

/// Evaluates a slice of pane snapshots in a tab and returns the highest-severity confirmation needed.
pub fn evaluateTab(snapshots: []const PaneCloseSnapshot) ?CloseConfirmation {
    var best: ?CloseConfirmation = null;
    for (snapshots) |snap| {
        const candidate = evaluatePane(snap, .tab) orelse continue;
        if (best == null or @intFromEnum(candidate.reason) > @intFromEnum(best.?.reason)) {
            best = candidate;
            if (candidate.reason == .dirty_buffer) break;
        }
    }
    return best;
}

fn makeAgentConfirmation(
    snapshot: PaneCloseSnapshot,
    target_kind: CloseTargetKind,
    agent_display: []const u8,
    icon_seed: []const u8,
) CloseConfirmation {
    var result: CloseConfirmation = .{
        .reason = .agent_session,
        .target_kind = target_kind,
        .icon = resolveConfirmationIcon(snapshot, icon_seed),
    };
    setFormatted(&result.title_buf, &result.title_len, "{s} is running", .{agent_display});
    setFormatted(
        &result.message_buf,
        &result.message_len,
        "An agent session ({s}) is currently active. Closing this {s} will terminate it.",
        .{ agent_display, target_kind.noun() },
    );
    setFormatted(&result.confirm_buf, &result.confirm_len, "Terminate", .{});
    return result;
}

fn resolveConfirmationIcon(snapshot: PaneCloseSnapshot, fallback_name: []const u8) app_icon.AppIcon {
    if (!snapshot.icon.is_shell) return snapshot.icon;
    const trimmed = std.mem.trim(u8, fallback_name, " \t\r\n");
    if (trimmed.len > 0) {
        if (app_icon.resolveDirect(trimmed)) |direct| {
            if (!direct.is_shell) return direct;
        }
        return app_icon.fromProcess(trimmed);
    }
    return snapshot.icon;
}

fn formatEditorDisplayName(raw: []const u8, buf: *[48]u8) []const u8 {
    const clean = std.mem.trim(u8, raw, " \t\r\n");
    if (clean.len == 0) return "";
    for (known_editors) |entry| {
        if (std.ascii.eqlIgnoreCase(clean, entry.alias)) return entry.display_name;
    }
    const len = @min(clean.len, buf.len);
    @memcpy(buf[0..len], clean[0..len]);
    buf[0] = std.ascii.toUpper(buf[0]);
    return buf[0..len];
}

fn setFormatted(buf: []u8, out_len: *usize, comptime fmt: []const u8, args: anytype) void {
    var stream = std.io.fixedBufferStream(buf);
    stream.writer().print(fmt, args) catch {};
    out_len.* = stream.pos;
}

test "close confirmation skips idle shell and exited panes" {
    const idle = PaneCloseSnapshot{
        .is_running = true,
        .title = "vex",
        .has_foreground_process = false,
    };
    try std.testing.expect(evaluatePane(idle, .pane) == null);
    try std.testing.expect(evaluatePane(idle, .tab) == null);

    const exited_dirty = PaneCloseSnapshot{
        .is_running = false,
        .title = "main.zig*",
        .app_name = "nvim",
        .has_foreground_process = true,
        .active_process_title = "nvim",
    };
    try std.testing.expect(evaluatePane(exited_dirty, .pane) == null);
}

test "close confirmation detects dirty editor buffers, active agents, and foreground processes" {
    const dirty_nvim = PaneCloseSnapshot{
        .is_running = true,
        .title = "App.xaml.cs*",
        .app_name = "nvim",
        .has_foreground_process = true,
        .active_process_title = "nvim",
    };
    const dirty_confirm = evaluatePane(dirty_nvim, .tab).?;
    try std.testing.expectEqual(ConfirmationReason.dirty_buffer, dirty_confirm.reason);
    try std.testing.expectEqualStrings("Unsaved Changes in Neovim", dirty_confirm.titleSlice());
    try std.testing.expectEqualStrings("\"App.xaml.cs\" has unsaved buffer changes. Closing this tab will discard them.", dirty_confirm.messageSlice());
    try std.testing.expectEqualStrings("Discard & Close", dirty_confirm.confirmSlice());
    try std.testing.expectEqual(app_icon.GlyphId.neovim, dirty_confirm.icon.glyph);

    const claude_fg = PaneCloseSnapshot{
        .is_running = true,
        .title = "Fix rendering bug",
        .app_name = "claude",
        .has_foreground_process = true,
        .active_process_title = "claude",
    };
    const agent_confirm = evaluatePane(claude_fg, .pane).?;
    try std.testing.expectEqual(ConfirmationReason.agent_session, agent_confirm.reason);
    try std.testing.expectEqualStrings("Claude Code is running", agent_confirm.titleSlice());
    try std.testing.expectEqualStrings("An agent session (Claude Code) is currently active. Closing this pane will terminate it.", agent_confirm.messageSlice());
    try std.testing.expectEqualStrings("Terminate", agent_confirm.confirmSlice());
    try std.testing.expectEqual(app_icon.GlyphId.claudecode, agent_confirm.icon.glyph);

    const cargo_fg = PaneCloseSnapshot{
        .is_running = true,
        .title = "cargo",
        .has_foreground_process = true,
        .active_process_title = "cargo",
    };
    const proc_confirm = evaluatePane(cargo_fg, .tab).?;
    try std.testing.expectEqual(ConfirmationReason.foreground_process, proc_confirm.reason);
    try std.testing.expectEqualStrings("cargo is running", proc_confirm.titleSlice());
    try std.testing.expectEqualStrings("Process \"cargo\" is actively running in this tab. Closing will terminate it.", proc_confirm.messageSlice());
    try std.testing.expectEqualStrings("Close Tab", proc_confirm.confirmSlice());

    // Multi-pane tab evaluation picks highest severity (dirty_buffer > agent_session > foreground_process)
    const tab_confirm = evaluateTab(&.{ cargo_fg, claude_fg, dirty_nvim }).?;
    try std.testing.expectEqual(ConfirmationReason.dirty_buffer, tab_confirm.reason);
}
