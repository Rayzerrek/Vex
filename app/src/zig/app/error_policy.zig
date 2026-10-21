const std = @import("std");

/// Only errors whose callers leave valid state may be recovered globally.
/// Parser/renderer failures, allocation failures and broken invariants stay fatal.
pub fn recoverable(err: anyerror) bool {
    return switch (err) {
        error.PtyInputFull,
        error.SessionTransportFailed,
        error.ClipboardWriteFailed,
        error.ShellStartFailed,
        error.InvalidTextInput,
        error.InvalidTextUtf8,
        error.InvalidPasteInput,
        error.InvalidPasteUtf8,
        error.InvalidMouseButton,
        error.InvalidPointerEvent,
        error.InvalidDividerPoint,
        error.InvalidWorkspaceAction,
        error.InvalidDialogInput,
        error.NativeDialogFailure,
        error.InvalidDialogEvent,
        error.InvalidDialogQuery,
        error.InvalidUserSettings,
        error.InvalidDefaultShell,
        error.InvalidShellForm,
        error.InvalidShellSelection,
        error.InvalidThemeSelection,
        error.UnavailableShellSelection,
        => true,
        else => false,
    };
}

/// Repeated rejected keystrokes must not continually rebuild the notification.
pub const NoticeLimiter = struct {
    last_error: ?anyerror = null,
    last_ms: i64 = 0,

    pub fn allow(self: *NoticeLimiter, err: anyerror, now_ms: i64) bool {
        if (self.last_error) |previous| {
            if (previous == err and now_ms >= self.last_ms and now_ms - self.last_ms < 2000) return false;
        }
        self.* = .{ .last_error = err, .last_ms = now_ms };
        return true;
    }
};

test "expected input failures recover but unknown errors and corrupt state do not" {
    for ([_]anyerror{ error.PtyInputFull, error.ClipboardWriteFailed, error.ShellStartFailed, error.InvalidPasteUtf8, error.InvalidDialogInput }) |err| {
        try std.testing.expect(recoverable(err));
    }
    for ([_]anyerror{ error.OutOfMemory, error.MissingPaneSession, error.InvalidPtyOutput, error.PtyReplyWriteFailed, error.Unexpected }) |err| {
        try std.testing.expect(!recoverable(err));
    }
}

test "error notices are bounded without suppressing new errors or clock rollback" {
    var limiter: NoticeLimiter = .{};
    try std.testing.expect(limiter.allow(error.PtyInputFull, 100));
    for (101..2100) |ms| try std.testing.expect(!limiter.allow(error.PtyInputFull, @intCast(ms)));
    try std.testing.expect(limiter.allow(error.PtyInputFull, 2100));
    try std.testing.expect(limiter.allow(error.ClipboardWriteFailed, 2101));
    try std.testing.expect(limiter.allow(error.ClipboardWriteFailed, 0));
}
