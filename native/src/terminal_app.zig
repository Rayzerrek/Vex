const std = @import("std");
const binding = @import("api.zig");
const core = @import("terminal.zig");
const platform = @import("windows_ffi.zig");
const panes = @import("pane_tree.zig");
const workspace = @import("workspace_model.zig");

const SessionState = union(enum) { running, exited: u32 };
const TerminalSession = struct {
    id: panes.PaneId,
    terminal: core.Terminal,
    pty: platform.PtyOwner,
    size: ?platform.TerminalSize = null,
    state: SessionState = .running,

    fn create(allocator: std.mem.Allocator, api: *const binding.Api, id: panes.PaneId) !*TerminalSession {
        const session = try allocator.create(TerminalSession);
        errdefer allocator.destroy(session);
        var terminal = try core.Terminal.init(allocator, api);
        errdefer terminal.deinit();
        var pty = try platform.PtyOwner.init(id);
        errdefer pty.deinit();
        session.* = .{ .id = id, .terminal = terminal, .pty = pty };
        try session.terminal.attachResponseSink(session.pty.responseSink());
        return session;
    }
    fn destroy(self: *TerminalSession, allocator: std.mem.Allocator) void {
        self.pty.deinit();
        self.terminal.deinit();
        allocator.destroy(self);
    }
    fn resize(self: *TerminalSession, size: platform.TerminalSize) !void {
        if (self.size) |previous| if (std.meta.eql(previous, size)) return;
        if (self.state == .running) try self.pty.resize(size);
        try self.terminal.resize(size.columns, size.rows, size.cell_width_px, size.cell_height_px);
        self.size = size;
    }
};

// This application owns sessions at stable addresses; tab/tree nodes only contain IDs.
const WorkspaceApplication = struct {
    allocator: std.mem.Allocator,
    api: binding.Api,
    sessions: std.ArrayList(*TerminalSession) = .empty,
    model: ?workspace.WorkspaceModel = null,
    placements: std.ArrayList(panes.PanePlacement) = .empty,
    metrics: ?platform.WindowMetrics = null,
    next_token: u32 = 1,
    health: union(enum) { running, failed: anyerror } = .running,

    fn init(allocator: std.mem.Allocator) !WorkspaceApplication {
        return .{ .allocator = allocator, .api = try binding.Api.load() };
    }
    fn deinit(self: *WorkspaceApplication) void {
        // Window callbacks have stopped before this runs. Native worker teardown precedes VT free.
        for (self.sessions.items) |session| session.destroy(self.allocator);
        self.sessions.deinit(self.allocator);
        if (self.model) |*model| model.deinit();
        self.placements.deinit(self.allocator);
    }
    pub fn fail(self: *WorkspaceApplication, err: anyerror) void {
        if (self.health == .failed) return;
        self.health = .{ .failed = err };
        platform.closeWindow();
    }
    fn findSession(self: *WorkspaceApplication, id: panes.PaneId) ?*TerminalSession {
        for (self.sessions.items) |session| if (session.id == id) return session;
        return null;
    }
    fn activeSession(self: *WorkspaceApplication) ?*TerminalSession {
        const model = if (self.model) |*model| model else return null;
        return self.findSession(model.activeTab().active_pane);
    }
    fn addSession(self: *WorkspaceApplication) !*TerminalSession {
        // Reserving first prevents an allocation failure from stranding a live process.
        try self.sessions.ensureUnusedCapacity(self.allocator, 1);
        const token = self.next_token;
        self.next_token = try std.math.add(u32, token, 1);
        const session = try TerminalSession.create(self.allocator, &self.api, @enumFromInt(token));
        self.sessions.appendAssumeCapacity(session);
        return session;
    }
    fn removeSession(self: *WorkspaceApplication, id: panes.PaneId) void {
        for (self.sessions.items, 0..) |session, index| if (session.id == id) {
            _ = self.sessions.swapRemove(index);
            session.destroy(self.allocator);
            return;
        };
    }
    fn initializeWorkspace(self: *WorkspaceApplication) !void {
        const first = try self.addSession();
        errdefer self.removeSession(first.id);
        self.model = try workspace.WorkspaceModel.init(self.allocator, first.id);
    }
    fn updateLayout(self: *WorkspaceApplication) !void {
        const model = if (self.model) |*model| model else return;
        const metrics = self.metrics orelse return;
        try model.activeTab().tree.layout(self.allocator, &self.placements, .{
            .x = 6,
            .y = 44,
            .width = @max(0, metrics.width - 12),
            .height = @max(0, metrics.height - 50),
        });
        for (self.placements.items) |placement| {
            const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
            try session.resize(try metrics.terminalSize(placement.bounds));
        }
        platform.invalidateWindow();
    }
    pub fn onResize(self: *WorkspaceApplication, metrics: platform.WindowMetrics) void {
        if (self.health == .failed) return;
        self.metrics = metrics;
        if (self.model == null) self.initializeWorkspace() catch |err| {
            self.fail(err);
            return;
        };
        self.updateLayout() catch |err| self.fail(err);
    }
    pub fn onOutput(self: *WorkspaceApplication, id: panes.PaneId, bytes: []const u8) bool {
        if (self.health == .failed) return false;
        // A queued event from a removed pane cannot target another pane, even after many opens/closes.
        const session = self.findSession(id) orelse return false;
        session.terminal.feedOutput(bytes) catch |err| {
            self.fail(err);
            return false;
        };
        const model = if (self.model) |*model| model else return false;
        return model.activeTab().tree.containsPane(id);
    }
    pub fn onPaint(self: *WorkspaceApplication) void {
        if (self.health == .failed) return;
        self.paintWorkspace() catch |err| self.fail(err);
    }
    fn paintWorkspace(self: *WorkspaceApplication) !void {
        const model = if (self.model) |*model| model else return;
        for (0..model.tabs.items.len) |index| platform.drawTab(index, model.tabs.items.len, index == model.selected_tab);
        for (self.placements.items) |placement| {
            if (placement.bounds.width < 14 or placement.bounds.height < 38) continue;
            const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
            const focused = model.activeTab().active_pane == placement.id;
            const frame = try session.terminal.readFrame();
            platform.beginPane(placement.bounds, focused);
            defer platform.endPane();
            try platform.drawTerminalFrame(frame, focused);
        }
    }
    pub fn onKey(self: *WorkspaceApplication, key: u32, modifiers: u32) bool {
        if (self.health == .failed) return true;
        if (platform.workspaceShortcut(key, modifiers)) |action| {
            self.onAction(action, 0);
            return true;
        }
        const session = self.activeSession() orelse return false;
        if (session.state == .exited) return true;
        var buffer: [256]u8 = undefined;
        const encoded = session.terminal.encodeKey(key, modifiers, &buffer) catch |err| {
            self.fail(err);
            return true;
        };
        if (encoded) |bytes| {
            session.pty.write(bytes) catch |err| self.fail(err);
            return true;
        }
        return false;
    }
    pub fn onText(self: *WorkspaceApplication, bytes: []const u8) void {
        if (self.health == .failed) return;
        const session = self.activeSession() orelse return;
        if (session.state == .running) session.pty.write(bytes) catch |err| self.fail(err);
    }
    pub fn onScroll(self: *WorkspaceApplication, delta: i32) void {
        if (self.health == .failed) return;
        const session = self.activeSession() orelse return;
        session.terminal.scroll(delta);
    }
    pub fn onClick(self: *WorkspaceApplication, x: f32, y: f32) void {
        if (self.health == .failed) return;
        const model = if (self.model) |*model| model else return;
        for (self.placements.items) |placement| if (placement.bounds.contains(x, y)) {
            _ = model.focusPane(placement.id);
            platform.invalidateWindow();
            return;
        };
    }
    pub fn onAction(self: *WorkspaceApplication, action: platform.WorkspaceAction, index: u32) void {
        if (self.health == .failed) return;
        self.applyAction(action, index) catch |err| self.fail(err);
    }
    fn applyAction(self: *WorkspaceApplication, action: platform.WorkspaceAction, index: u32) !void {
        const model = if (self.model) |*model| model else return;
        switch (action) {
            .new_tab => {
                if (model.tabs.items.len >= workspace.WorkspaceModel.max_tabs) return;
                const session = try self.addSession();
                errdefer self.removeSession(session.id);
                try model.newTab(session.id);
            },
            .split_right, .split_down => {
                const session = try self.addSession();
                errdefer self.removeSession(session.id);
                try model.splitActivePane(session.id, if (action == .split_right) .right else .down);
            },
            .next_tab => model.cycleTab(true),
            .previous_tab => model.cycleTab(false),
            .select_tab => {
                if (!model.selectTab(index)) return;
            },
            .close_active => {
                if (model.closeActiveSplit()) |id| self.removeSession(id) else try self.closeTab(model.selected_tab);
            },
            .close_tab => try self.closeTab(index),
        }
        try self.updateLayout();
    }
    fn closeTab(self: *WorkspaceApplication, index: usize) !void {
        const model = if (self.model) |*model| model else return;
        if (index >= model.tabs.items.len or model.tabs.items.len <= 1) return;
        var closing: std.ArrayList(panes.PanePlacement) = .empty;
        defer closing.deinit(self.allocator);
        try model.tabs.items[index].tree.layout(self.allocator, &closing, .{ .x = 0, .y = 0, .width = 1, .height = 1 });
        for (closing.items) |placement| self.removeSession(placement.id);
        _ = model.closeTab(index);
    }
    pub fn onExited(self: *WorkspaceApplication, id: panes.PaneId, code: u32) void {
        if (self.health == .failed) return;
        const session = self.findSession(id) orelse return;
        session.state = .{ .exited = code };
        // Automatic exit-close and agent/dirty confirmation need additional parity fixtures.
        platform.invalidateWindow();
    }
    pub fn onProbe(self: *WorkspaceApplication, query: u32) u64 {
        if (self.health == .failed) return 0;
        const model = if (self.model) |*model| model else return 0;
        switch (query) {
            10 => return model.tabs.items.len,
            11 => return model.activeTab().tree.paneCount(),
            12 => return @intFromEnum(model.activeTab().active_pane),
            13 => return self.sessions.items.len,
            14 => return if (self.activeSession()) |session| session.pty.processId() else 0,
            20 => return platform.paintCount(),
            else => {},
        }
        const marker = switch (query) {
            0 => "VEX_NATIVE_SMOKE_OK",
            1 => "VEX_NATIVE_FLOOD_OK",
            2 => "VEX_NATIVE_PANE_A",
            3 => "VEX_NATIVE_PANE_B",
            4 => "VEX_NATIVE_TAB_A",
            5 => "VEX_NATIVE_TAB_B",
            6 => "VEX_NATIVE_PANE_C",
            7 => "VEX_NATIVE_BACKGROUND_OK",
            else => return 0,
        };
        const session = self.activeSession() orelse return 0;
        const found = session.terminal.containsViewportText(marker) catch |err| {
            self.fail(err);
            return 0;
        };
        return @intFromBool(found);
    }
};

/// Main keeps the allocator/API/app owners alive until all window callbacks and pipe workers stop.
pub fn main() void {
    platform.beginStartup();
    var allocator = std.heap.DebugAllocator(.{}){};
    defer {
        if (allocator.deinit() == .leak) platform.showFailure(error.NativeAllocationLeak);
    }
    runApplication(allocator.allocator()) catch |err| {
        platform.showFailure(err);
        std.process.exit(1);
    };
}
fn runApplication(allocator: std.mem.Allocator) !void {
    var app = try WorkspaceApplication.init(allocator);
    defer app.deinit();
    platform.runWindow(&app) catch |err| {
        if (app.health == .failed) return app.health.failed;
        return err;
    };
    if (app.health == .failed) return app.health.failed;
}

test {
    _ = @import("terminal.zig");
    _ = @import("pane_tree.zig");
    _ = @import("workspace_model.zig");
    _ = @import("windows_ffi.zig");
}
