const std = @import("std");
const application = @import("app/workspace_application.zig");
const platform = @import("windows/windows_ffi.zig");

/// Main owns the allocator until application/session teardown has joined every native worker.
pub fn main() void {
    platform.beginStartup();
    var allocator = std.heap.DebugAllocator(.{}){};
    defer {
        if (allocator.deinit() == .leak) platform.showFailure(error.NativeAllocationLeak);
    }
    application.runApplication(allocator.allocator()) catch |err| {
        platform.showFailure(err);
        std.process.exit(1);
    };
}

test {
    _ = application;
    _ = @import("terminal/terminal.zig");
    _ = @import("shell/directory.zig");
    _ = @import("shell/wsl_paths.zig");
    _ = @import("shell/wsl_launch.zig");
    _ = @import("shell/windows_arguments.zig");
    _ = @import("windows/wsl_registry.zig");
    _ = @import("workspace/pane_tree.zig");
    _ = @import("workspace/divider_drag.zig");
    _ = @import("workspace/tab_strip.zig");
    _ = @import("workspace/tab_drag.zig");
    _ = @import("workspace/workspace_model.zig");
    _ = platform;
    _ = @import("shell/shell_launch.zig");
}
