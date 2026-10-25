//! Host-side correctness tests; independent of Windows DLLs and desktop tools.
test {
    _ = @import("app/copy_feedback.zig");
    _ = @import("terminal/terminal_feed.zig");
}
