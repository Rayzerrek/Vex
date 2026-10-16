const std = @import("std");
const application = @import("app/terminal_application.zig");
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
    _ = @import("app/terminal_commands.zig");
    _ = @import("terminal/terminal.zig");
    _ = @import("terminal/terminal_title.zig");
    _ = @import("terminal/status_presentation.zig");
    _ = @import("terminal/app_icon.zig");
    _ = @import("terminal/url_detector.zig");
    _ = @import("terminal/path_completion.zig");
    _ = @import("terminal/path_index.zig");
    _ = @import("shell/directory.zig");
    _ = @import("shell/wsl_paths.zig");
    _ = @import("shell/wsl_launch.zig");
    _ = @import("shell/windows_arguments.zig");
    _ = @import("windows/wsl_registry.zig");
    _ = @import("workspace/pane_tree.zig");
    _ = @import("workspace/divider_drag.zig");
    _ = @import("workspace/cursor_blink.zig");
    _ = @import("workspace/status_pulse.zig");
    _ = @import("workspace/tab_strip.zig");
    _ = @import("workspace/tab_drag.zig");
    _ = @import("workspace/terminal_theme.zig");
    _ = @import("workspace/close_confirmation.zig");
    _ = @import("workspace/command_palette.zig");
    _ = @import("app/user_settings.zig");
    _ = @import("windows/windows_dialog.zig");
    _ = @import("workspace/workspace_model.zig");
    _ = platform;
    _ = @import("shell/shell_launch.zig");
}
