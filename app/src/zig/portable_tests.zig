//! Host-side correctness tests; independent of Windows DLLs and desktop tools.
test {
    _ = @import("app/copy_feedback.zig");
    _ = @import("terminal/terminal_feed.zig");
    _ = @import("terminal/path_walker.zig");
    _ = @import("workspace/pane_navigation.zig");
    _ = @import("workspace/workspace_model.zig");
    _ = @import("workspace/layout_motion.zig");
}
