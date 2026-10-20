const std = @import("std");
const app_icon = @import("../terminal/app_icon.zig");

/// Whether the close operation addresses one split or an entire tab.
pub const CloseTargetKind = enum(u8) {
    pane,
    tab,
    pub fn noun(self: CloseTargetKind) []const u8 {
        return if (self == .pane) "pane" else "tab";
    }
    pub fn closeButtonText(self: CloseTargetKind) []const u8 {
        return if (self == .pane) "Close pane" else "Close tab";
    }
};
/// Warnings rank by observed data-loss risk, never by application brand.
pub const ConfirmationReason = enum(u8) { foreground_process = 1, reported_activity = 2, dirty_buffer = 3 };
/// Borrowed process/protocol facts; a title alone does not prove that a program is running.
pub const PaneCloseSnapshot = struct {
    is_running: bool,
    title: []const u8 = "",
    status_app: []const u8 = "",
    status_active: bool = false,
    has_foreground_process: bool = false,
    foreground_shell_command: bool = false,
    foreground_is_shell: bool = false,
    foreground_is_root: bool = false,
    foreground_is_transport: bool = false,
    active_process_title: []const u8 = "",
    icon: app_icon.AppIcon = app_icon.AppIcon.terminal_shell,
};
/// Owns bounded confirmation text; no borrowed title survives a session mutation.
pub const CloseConfirmation = struct {
    reason: ConfirmationReason,
    target_kind: CloseTargetKind,
    program_buf: [64]u8 = undefined,
    program_len: usize = 0,
    title_buf: [96]u8 = undefined,
    title_len: usize = 0,
    message_buf: [224]u8 = undefined,
    message_len: usize = 0,
    confirm_buf: [32]u8 = undefined,
    confirm_len: usize = 0,
    icon: app_icon.AppIcon = app_icon.AppIcon.terminal_shell,
    pub fn programSlice(self: *const CloseConfirmation) []const u8 {
        return self.program_buf[0..self.program_len];
    }
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

/// Applies identical protection to unknown executables and explicit OSC 7501 activity.
pub fn evaluatePane(snapshot: PaneCloseSnapshot, target: CloseTargetKind) ?CloseConfirmation {
    if (!snapshot.is_running) return null;
    const title = std.mem.trim(u8, snapshot.title, " \t\r\n");
    const dirty = std.mem.endsWith(u8, title, "*");
    // A parent shell can report work while waiting for a nested interactive shell.
    // Keep explicit application activity and dirty-buffer warnings authoritative.
    const foreground_shell = (snapshot.has_foreground_process or snapshot.active_process_title.len > 0) and !snapshot.foreground_shell_command and
        snapshot.foreground_is_shell;
    const foreground_work = snapshot.has_foreground_process and !foreground_shell;
    const reported_work = snapshot.status_active and (!foreground_shell or snapshot.status_app.len > 0 or snapshot.foreground_is_root);
    // An unavailable Linux probe is unknown, not evidence that the transport's session is idle.
    if (!dirty and !foreground_work and !reported_work and !snapshot.foreground_is_transport) return null;
    // Captions can be a distribution or a task description, never an executable identity.
    const metadata_name = if (snapshot.status_active and snapshot.status_app.len > 0) snapshot.status_app else "";
    const name = if (metadata_name.len > 0) metadata_name else if (!snapshot.foreground_is_transport and snapshot.active_process_title.len > 0) snapshot.active_process_title else "A program";
    var result: CloseConfirmation = .{
        .reason = if (dirty) .dirty_buffer else if (snapshot.status_active) .reported_activity else .foreground_process,
        .target_kind = target,
        .icon = if (metadata_name.len > 0 or snapshot.icon.is_shell) app_icon.fromProcess(name) else snapshot.icon,
    };
    setFormatted(&result.program_buf, &result.program_len, "{s}", .{name});
    if (dirty) {
        setFormatted(&result.title_buf, &result.title_len, "Close {s}?", .{if (std.mem.eql(u8, name, "A program")) target.noun() else name});
        setFormatted(&result.message_buf, &result.message_len, "This {s} may have unsaved changes. Closing it will stop its programs and discard that work.", .{target.noun()});
        setFormatted(&result.confirm_buf, &result.confirm_len, "Discard & close", .{});
    } else {
        setFormatted(&result.title_buf, &result.title_len, "Close {s}?", .{if (std.mem.eql(u8, name, "A program")) target.noun() else name});
        if (snapshot.foreground_is_transport and metadata_name.len == 0) {
            setFormatted(&result.message_buf, &result.message_len, "Running programs could not be verified. Closing this {s} will stop its programs. Unsaved work may be lost.", .{target.noun()});
        } else {
            setFormatted(&result.message_buf, &result.message_len, "{s} is still running. Closing this {s} will stop it. Unsaved work may be lost.", .{ name, target.noun() });
        }
        setFormatted(&result.confirm_buf, &result.confirm_len, "{s}", .{target.closeButtonText()});
    }
    return result;
}
/// Streaming highest-risk decision; retains owned text, not snapshots or a capped pane array.
pub const TabCloseReview = struct {
    confirmation: ?CloseConfirmation = null,

    /// Includes one pane without allocating; equal-risk warnings preserve traversal order.
    pub fn includePane(self: *TabCloseReview, snapshot: PaneCloseSnapshot) void {
        if (self.isComplete()) return;
        const candidate = evaluatePane(snapshot, .tab) orelse return;
        if (self.confirmation) |current| {
            if (@intFromEnum(candidate.reason) <= @intFromEnum(current.reason)) return;
        }
        self.confirmation = candidate;
    }

    /// Only the highest possible risk permits an early end to tree traversal.
    pub fn isComplete(self: *const TabCloseReview) bool {
        return if (self.confirmation) |current| switch (current.reason) {
            .dirty_buffer => true,
            .foreground_process, .reported_activity => false,
        } else false;
    }
};
/// One row per protected pane keeps duplicate applications distinguishable by their workspace location.
pub const ExitProcessRow = struct {
    name: [64]u8 = undefined,
    name_len: usize = 0,
    detail: [224]u8 = undefined,
    detail_len: usize = 0,
    icon: app_icon.AppIcon,
    pub fn nameSlice(self: *const ExitProcessRow) []const u8 {
        return self.name[0..self.name_len];
    }
    pub fn detailSlice(self: *const ExitProcessRow) []const u8 {
        return self.detail[0..self.detail_len];
    }
};

pub const ExitReview = struct {
    rows: std.ArrayList(ExitProcessRow) = .empty,
    pub fn deinit(self: *ExitReview, allocator: std.mem.Allocator) void {
        self.rows.deinit(allocator);
    }
    pub fn includePane(self: *ExitReview, allocator: std.mem.Allocator, snapshot: PaneCloseSnapshot, tab_index: usize, pane_index: usize, tab_title: []const u8) !void {
        const confirmation = evaluatePane(snapshot, .pane) orelse return;
        var row: ExitProcessRow = .{ .icon = confirmation.icon };
        const name = confirmation.programSlice();
        setFormatted(&row.name, &row.name_len, "{s}", .{if (std.mem.eql(u8, name, "A program")) "Unverified session" else name});
        const risk = if (confirmation.reason == .dirty_buffer) " · Unsaved changes" else "";
        if (tab_title.len > 0) {
            setFormatted(&row.detail, &row.detail_len, "Tab {d}: {s} · Pane {d}{s}", .{ tab_index + 1, tab_title, pane_index + 1, risk });
        } else {
            setFormatted(&row.detail, &row.detail_len, "Tab {d} · Pane {d}{s}", .{ tab_index + 1, pane_index + 1, risk });
        }
        try self.rows.append(allocator, row);
    }
};

fn checkExitReviewAllocation(allocator: std.mem.Allocator) !void {
    var review: ExitReview = .{};
    defer review.deinit(allocator);
    try review.includePane(allocator, .{ .is_running = true }, 0, 0, "Idle");
    try std.testing.expectEqual(@as(usize, 0), review.rows.items.len);
    for (0..130) |i| try review.includePane(allocator, .{
        .is_running = true,
        .has_foreground_process = true,
        .active_process_title = "codex",
    }, i / 2, i % 2, "Development");
    try std.testing.expectEqual(@as(usize, 130), review.rows.items.len);
    try std.testing.expectEqualStrings("codex", review.rows.items[0].nameSlice());
    try std.testing.expectEqualStrings("Tab 1: Development · Pane 1", review.rows.items[0].detailSlice());
    try std.testing.expectEqualStrings("Tab 1: Development · Pane 2", review.rows.items[1].detailSlice());
    try std.testing.expectEqual(app_icon.GlyphId.codex, review.rows.items[0].icon.glyph);
}

test "exit review includes every protected pane without a 64-item cap and survives allocation failures" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkExitReviewAllocation, .{});
}

test "exit rows own names and locations, preserve UTF-8 and distinguish dirty or unverified sessions" {
    var review: ExitReview = .{};
    defer review.deinit(std.testing.allocator);
    var name = "agy".*;
    try review.includePane(std.testing.allocator, .{ .is_running = true, .title = "file*", .active_process_title = &name }, 0, 1, "Editor");
    name[0] = 'x';
    try std.testing.expectEqualStrings("agy", review.rows.items[0].nameSlice());
    try std.testing.expectEqualStrings("Tab 1: Editor · Pane 2 · Unsaved changes", review.rows.items[0].detailSlice());
    try review.includePane(std.testing.allocator, .{ .is_running = true, .foreground_is_transport = true }, 1, 0, "");
    try std.testing.expectEqualStrings("Unverified session", review.rows.items[1].nameSlice());
    try review.includePane(std.testing.allocator, .{ .is_running = false, .title = "file*", .has_foreground_process = true }, 1, 1, "Exited");
    try std.testing.expectEqual(@as(usize, 2), review.rows.items.len);
    var unicode: [256]u8 = undefined;
    for (0..64) |i| @memcpy(unicode[i * 4 ..][0..4], "😀");
    try review.includePane(std.testing.allocator, .{ .is_running = true, .has_foreground_process = true, .active_process_title = &unicode }, 2, 0, &unicode);
    const row = &review.rows.items[2];
    try std.testing.expect(std.unicode.utf8ValidateSlice(row.nameSlice()));
    try std.testing.expect(std.unicode.utf8ValidateSlice(row.detailSlice()));
}

fn setFormatted(buffer: []u8, len: *usize, comptime fmt: []const u8, args: anytype) void {
    var stream = std.io.fixedBufferStream(buffer);
    stream.writer().print(fmt, args) catch {};
    var end = stream.pos;
    if (end == buffer.len) {
        // The final codepoint can have been cut while the bounded writer filled up.
        while (end > 0 and !std.unicode.utf8ValidateSlice(buffer[0..end])) : (end -= 1) {}
    }
    len.* = end;
}

test "unknown and familiar processes have identical close protection; titles do not imply liveness" {
    for ([_][]const u8{ "future-agent", "claude", "unreleased-editor", "nvim" }) |name| {
        try std.testing.expect(evaluatePane(.{ .is_running = true, .title = name, .status_app = name }, .pane) == null);
        const running = evaluatePane(.{ .is_running = true, .active_process_title = name, .has_foreground_process = true }, .pane) orelse return error.MissingCloseWarning;
        try std.testing.expectEqual(ConfirmationReason.foreground_process, running.reason);
        const reported = evaluatePane(.{ .is_running = true, .status_app = name, .status_active = true }, .tab) orelse return error.MissingCloseWarning;
        try std.testing.expectEqual(ConfirmationReason.reported_activity, reported.reason);
        try std.testing.expect(evaluatePane(.{ .is_running = false, .status_active = true, .title = "file*" }, .pane) == null);
    }
    try std.testing.expect(evaluatePane(.{ .is_running = true, .status_active = true }, .pane) != null);
    try std.testing.expect(evaluatePane(.{ .is_running = true, .has_foreground_process = true }, .pane) != null);
    var review: TabCloseReview = .{};
    review.includePane(.{ .is_running = true, .has_foreground_process = true });
    review.includePane(.{ .is_running = true, .title = "work*" });
    const dirty = review.confirmation orelse return error.MissingCloseWarning;
    try std.testing.expectEqual(ConfirmationReason.dirty_buffer, dirty.reason);
    var text: [256]u8 = undefined;
    for (0..64) |i| @memcpy(text[i * 4 ..][0..4], "😀");
    const unicode = evaluatePane(.{ .is_running = true, .active_process_title = &text, .has_foreground_process = true }, .pane) orelse return error.MissingCloseWarning;
    try std.testing.expect(std.unicode.utf8ValidateSlice(unicode.titleSlice()));
}

test "nested shells are not foreground work but their applications remain protected" {
    for ([_][]const u8{ "pwsh", "powershell.exe", "cmd", "bash", "zsh", "fish", "nu", "elvish", "sh", "dash", "ash", "ksh", "tcsh", "csh", "never-seen-shell" }) |name| {
        const snapshot: PaneCloseSnapshot = .{ .is_running = true, .has_foreground_process = true, .foreground_is_shell = true, .active_process_title = name, .status_active = true };
        try std.testing.expect(evaluatePane(snapshot, .pane) == null);
        var dirty = snapshot;
        dirty.title = "file*";
        try std.testing.expect(evaluatePane(dirty, .pane) != null);
        var command = snapshot;
        command.foreground_shell_command = true;
        try std.testing.expect(evaluatePane(command, .pane) != null);
        var active = snapshot;
        active.status_app = "editor";
        try std.testing.expect(evaluatePane(active, .pane) != null);
    }
    try std.testing.expect(evaluatePane(.{ .is_running = true, .has_foreground_process = true, .active_process_title = "unknown-tool" }, .pane) != null);
}

test "close warnings retain executable classification and explicit program identity" {
    const script = evaluatePane(.{
        .is_running = true,
        .has_foreground_process = true,
        .foreground_is_shell = false,
        .active_process_title = "powershell",
    }, .tab) orelse return error.MissingCloseWarning;
    try std.testing.expectEqualStrings("Close powershell?", script.titleSlice());
    const reported = evaluatePane(.{
        .is_running = true,
        .foreground_is_shell = true,
        .active_process_title = "pwsh",
        .status_active = true,
        .status_app = "nvim",
    }, .pane) orelse return error.MissingCloseWarning;
    try std.testing.expectEqualStrings("Close nvim?", reported.titleSlice());
    try std.testing.expect(!reported.icon.is_shell);
    try std.testing.expect(std.mem.indexOf(u8, reported.messageSlice(), "nvim") != null);
    var idle: PaneCloseSnapshot = .{
        .is_running = true,
        .foreground_is_shell = true,
        .foreground_is_root = true,
        .active_process_title = "my-own-shell",
    };
    try std.testing.expect(evaluatePane(idle, .pane) == null);
    idle.status_active = true;
    try std.testing.expect(evaluatePane(idle, .pane) != null);
    idle.foreground_is_root = false;
    try std.testing.expect(evaluatePane(idle, .pane) == null);
    idle.status_app = "nvim";
    idle.title = "notes*";
    const dirty = evaluatePane(idle, .pane) orelse return error.MissingCloseWarning;
    try std.testing.expectEqualStrings("Close nvim?", dirty.titleSlice());
    try std.testing.expectEqual(ConfirmationReason.dirty_buffer, dirty.reason);
}

test "close prompts use process identity across backends and never mistake a distribution caption for an app" {
    for ([_][]const u8{ "codex", "agy", "never-seen-agent" }) |name| {
        const result = evaluatePane(.{
            .is_running = true,
            .title = "NixOS",
            .has_foreground_process = true,
            .active_process_title = name,
        }, .tab) orelse return error.MissingCloseWarning;
        var expected: [96]u8 = undefined;
        try std.testing.expectEqualStrings(try std.fmt.bufPrint(&expected, "Close {s}?", .{name}), result.titleSlice());
        try std.testing.expect(result.icon.eql(app_icon.fromProcess(name)));
    }
    var transport: PaneCloseSnapshot = .{
        .is_running = true,
        .title = "NixOS",
        .foreground_is_shell = true,
        .foreground_is_root = true,
        .foreground_is_transport = true,
        .active_process_title = "wsl",
    };
    const unknown = evaluatePane(transport, .tab) orelse return error.MissingCloseWarning;
    try std.testing.expectEqualStrings("Close tab?", unknown.titleSlice());
    try std.testing.expect(std.mem.indexOf(u8, unknown.messageSlice(), "could not be verified") != null);
    transport.status_active = true;
    transport.status_app = "codex";
    const reported = evaluatePane(transport, .pane) orelse return error.MissingCloseWarning;
    try std.testing.expectEqualStrings("Close codex?", reported.titleSlice());
    try std.testing.expectEqual(app_icon.GlyphId.codex, reported.icon.glyph);
}

test "tab close review has no pane-count cap and preserves highest-risk ordering" {
    var review: TabCloseReview = .{};
    for (0..17) |_| review.includePane(.{ .is_running = true });
    try std.testing.expect(review.confirmation == null);
    review.includePane(.{ .is_running = true, .status_active = true, .status_app = "future-program" });
    const active = review.confirmation orelse return error.MissingCloseWarning;
    try std.testing.expectEqual(ConfirmationReason.reported_activity, active.reason);
    try std.testing.expectEqual(CloseTargetKind.tab, active.target_kind);
    try std.testing.expect(!review.isComplete());
    review.includePane(.{ .is_running = true, .has_foreground_process = true });
    const retained = review.confirmation orelse return error.MissingCloseWarning;
    try std.testing.expectEqualStrings(active.titleSlice(), retained.titleSlice());
    review.includePane(.{ .is_running = true, .title = "future-document*" });
    try std.testing.expect(review.isComplete());
    review.includePane(.{ .is_running = true, .status_active = true });
    const dirty = review.confirmation orelse return error.MissingCloseWarning;
    try std.testing.expectEqual(ConfirmationReason.dirty_buffer, dirty.reason);
}
