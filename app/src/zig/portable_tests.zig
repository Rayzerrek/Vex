//! Host-side correctness tests; independent of Windows DLLs and desktop tools.
test {
    _ = @import("app/copy_feedback.zig");
    _ = @import("terminal/terminal_feed.zig");
    _ = @import("terminal/path_walker.zig");
    _ = @import("terminal/url_detector.zig");
    _ = @import("terminal/app_icon.zig");
    _ = @import("workspace/pane_navigation.zig");
    _ = @import("workspace/workspace_model.zig");
    _ = @import("workspace/layout_motion.zig");
    _ = @import("workspace/smooth_scroll.zig");
    _ = @import("workspace/pane_drag_preview.zig");
}
