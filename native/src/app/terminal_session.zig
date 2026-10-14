const std = @import("std");
const binding = @import("../terminal/api.zig");
const core = @import("../terminal/terminal.zig");
const platform = @import("../windows/windows_ffi.zig");
const panes = @import("../workspace/pane_tree.zig");
const directory = @import("../shell/directory.zig");
const shell = @import("../shell/shell_launch.zig");

const SessionState = union(enum) { running, exited: u32 };
/// Stable-address owner of one emulator, ConPTY process and directory; borrows the application API/hostname.
pub const TerminalSession = struct {
    id: panes.PaneId,
    terminal: core.Terminal,
    pty: platform.PtyOwner,
    directory: directory.PaneDirectory,
    machine_name: []const u8,
    size: ?platform.TerminalSize = null,
    state: SessionState = .running,
    needs_attention: bool = false,

    /// Allocates at the final address before registering callbacks; startup failure releases every partial owner.
    pub fn create(allocator: std.mem.Allocator, api: *const binding.Api, id: panes.PaneId, path: directory.DirectoryPath, machine_name: []const u8, launch: *const shell.ShellLaunch) !*TerminalSession {
        const session = try allocator.create(TerminalSession);
        errdefer allocator.destroy(session);
        var pane_directory = try directory.PaneDirectory.initForContext(allocator, path, launch.directoryContext());
        errdefer pane_directory.deinit();
        var terminal = try core.Terminal.init(allocator, api);
        errdefer terminal.deinit();
        var pty = try platform.PtyOwner.init(allocator, id, pane_directory.current(), launch);
        errdefer pty.deinit();
        session.* = .{ .id = id, .terminal = terminal, .pty = pty, .directory = pane_directory, .machine_name = machine_name };
        try session.terminal.attachResponseSink(session.pty.responseSink());
        try session.terminal.attachDirectorySink(session);
        return session;
    }
    /// Synchronously parses each borrowed PWD report before a later report can replace it.
    pub fn onDirectoryReport(self: *TerminalSession, bytes: []const u8) !void {
        try self.directory.applyReport(bytes, self.machine_name);
    }
    /// Joins native workers before releasing the emulator, directory and stable session address.
    pub fn destroy(self: *TerminalSession, allocator: std.mem.Allocator) void {
        self.pty.deinit();
        self.terminal.deinit();
        self.directory.deinit();
        allocator.destroy(self);
    }
    /// User input acknowledges completed results; emulator replies deliberately bypass this path.
    pub fn writeUserInput(self: *TerminalSession, bytes: []const u8) !void {
        const previous = platform.statusBadgeKey(self.terminal.program_status.programStatus());
        self.terminal.program_status.acknowledgeProgramStatus();
        if (previous != platform.statusBadgeKey(self.terminal.program_status.programStatus())) platform.invalidateWindow();
        try self.pty.write(bytes);
    }
    /// Avoids redundant ConPTY/emulator resizes; exited sessions retain a resizable terminal viewport.
    pub fn resize(self: *TerminalSession, size: platform.TerminalSize) !void {
        if (self.size) |previous| if (std.meta.eql(previous, size)) return;
        if (self.state == .running) try self.pty.resize(size);
        try self.terminal.resize(size.columns, size.rows, size.cell_width_px, size.cell_height_px);
        self.size = size;
    }
};
