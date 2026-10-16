const std = @import("std");
const status = @import("program_status.zig");

/// Plain activity text for tab subtitles; missing reports never imply an idle process.
pub fn statusText(summary: status.ProgramStatusSummary) []const u8 {
    return switch (summary.state) {
        .none => "",
        .idle => "Idle",
        .working => "Working",
        .done => "Done",
        .failed => "Failed",
        .blocked => switch (summary.kind orelse .question) {
            .permission => "Waiting for approval",
            .auth => "Waiting for sign-in",
            .question => "Waiting for input",
        },
    };
}
/// Agent task names take precedence over app names; callers retain custom tab-title precedence.
pub fn statusTitle(summary: status.ProgramStatusSummary, fallback: []const u8) []const u8 {
    if (summary.title) |title| if (title.len > 0) return boundedStatusText(title);
    if (summary.app) |app| if (app.len > 0) return boundedStatusText(app);
    return boundedStatusText(fallback);
}
/// The second line exposes actual reported activity, falling back to a truthful state label.
pub fn statusDetail(summary: status.ProgramStatusSummary) []const u8 {
    if (summary.state == .none) return "";
    if (summary.message) |message| if (message.len > 0) return boundedStatusText(message);
    return statusText(summary);
}
/// Native label input is bounded to 256 bytes without splitting a parsed UTF-8 codepoint.
pub fn boundedStatusText(text: []const u8) []const u8 {
    var len = @min(text.len, 256);
    if (len < text.len) while (len > 0 and text[len] & 0xc0 == 0x80) : (len -= 1) {};
    return text[0..len];
}
/// Repaint keys include visible task/message text, not borrowed addresses or terminal-grid contents.
pub fn statusPresentationKey(summary: status.ProgramStatusSummary) u64 {
    var hash = std.hash.Wyhash.init(0);
    const fields = [_]u32{ @intFromEnum(summary.state), if (summary.kind) |kind| @as(u32, @intFromEnum(kind)) + 1 else 0, if (summary.progress) |progress| progress else 101 };
    hash.update(std.mem.asBytes(&fields));
    for ([_]?[]const u8{ summary.app, summary.title, summary.message }) |field| {
        const text = boundedStatusText(field orelse "");
        const len: u64 = text.len;
        hash.update(std.mem.asBytes(&len));
        hash.update(text);
    }
    return hash.final();
}

test "status presentation uses reported tasks, app fallback and truthful activity labels" {
    try std.testing.expectEqualStrings("", statusText(.{}));
    try std.testing.expectEqualStrings("Idle", statusText(.{ .state = .idle }));
    try std.testing.expectEqualStrings("Working", statusText(.{ .state = .working }));
    try std.testing.expectEqualStrings("Done", statusText(.{ .state = .done }));
    try std.testing.expectEqualStrings("Failed", statusText(.{ .state = .failed }));
    try std.testing.expectEqualStrings("Waiting for approval", statusText(.{ .state = .blocked, .kind = .permission }));
    try std.testing.expectEqualStrings("Waiting for sign-in", statusText(.{ .state = .blocked, .kind = .auth }));
    try std.testing.expectEqualStrings("Waiting for input", statusText(.{ .state = .blocked }));
    const summary = status.ProgramStatusSummary{ .state = .working, .app = "claude", .title = "Review renderer", .message = "Inspecting atlas ownership" };
    try std.testing.expectEqualStrings("Review renderer", statusTitle(summary, "pwsh"));
    try std.testing.expectEqualStrings("Inspecting atlas ownership", statusDetail(summary));
    try std.testing.expectEqualStrings("codex", statusTitle(.{ .app = "codex", .title = "" }, "cmd"));
    try std.testing.expectEqualStrings("cmd", statusTitle(.{}, "cmd"));
}

test "status text boundaries preserve UTF-8 and repaint keys observe text-only changes" {
    var text: [260]u8 = @splat('a');
    @memcpy(text[255..259], "😀");
    try std.testing.expectEqual(@as(usize, 255), boundedStatusText(text[0..259]).len);
    try std.testing.expect(std.unicode.utf8ValidateSlice(boundedStatusText(text[0..259])));
    const first = status.ProgramStatusSummary{ .state = .working, .message = "one" };
    const second = status.ProgramStatusSummary{ .state = .working, .message = "two" };
    try std.testing.expect(statusPresentationKey(first) != statusPresentationKey(second));
    const copy = try std.testing.allocator.dupe(u8, "one");
    defer std.testing.allocator.free(copy);
    try std.testing.expectEqual(statusPresentationKey(first), statusPresentationKey(.{ .state = .working, .message = copy }));
}
