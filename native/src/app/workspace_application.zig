const std = @import("std");
const binding = @import("../terminal/api.zig");
const platform = @import("../windows/windows_ffi.zig");
const panes = @import("../workspace/pane_tree.zig");
const workspace = @import("../workspace/workspace_model.zig");
const directory = @import("../shell/directory.zig");
const status = @import("../terminal/program_status.zig");
const shell = @import("../shell/shell_launch.zig");
const TerminalSession = @import("terminal_session.zig").TerminalSession;
const wsl_registry = @import("../windows/wsl_registry.zig");
const DividerDrag = @import("../workspace/divider_drag.zig").DividerDrag;

// This application owns sessions at stable addresses; tab/tree nodes only contain IDs.
const WorkspaceApplication = struct {
    allocator: std.mem.Allocator,
    api: binding.Api,
    project_directory: directory.DirectoryPath,
    machine_name: []u8,
    launch: shell.ShellLaunch,
    sessions: std.ArrayList(*TerminalSession) = .empty,
    model: ?workspace.WorkspaceModel = null,
    placements: std.ArrayList(panes.PanePlacement) = .empty,
    metrics: ?platform.WindowMetrics = null,
    divider_drag: DividerDrag = .{},
    next_token: u32 = 1,
    health: union(enum) { running, failed: anyerror } = .running,

    fn init(allocator: std.mem.Allocator) !WorkspaceApplication {
        const api = try binding.Api.load();
        const path = try std.process.getCwdAlloc(allocator);
        errdefer allocator.free(path);
        const parsed = try directory.DirectoryPath.parse(path);
        const machine_name = try platform.machineNameAlloc(allocator);
        errdefer allocator.free(machine_name);
        const argv = try std.process.argsAlloc(allocator);
        defer std.process.argsFree(allocator, argv);
        const parsed_request = try shell.ShellRequest.parseArguments(if (argv.len > 0) argv[1..] else &.{});
        var request = try parsed_request.forProjectDirectory(parsed);
        const default_distribution = if (request == .wsl and request.wsl.distribution == .automatic) try wsl_registry.readDefaultWslDistributionAlloc(allocator) else null;
        defer if (default_distribution) |name| allocator.free(name.bytes);
        if (default_distribution) |name| request.wsl.distribution = .{ .named = name };
        const system_directory = try platform.systemDirectoryAlloc(allocator);
        defer allocator.free(system_directory);
        const executable_directory = try std.fs.selfExeDirPathAlloc(allocator);
        defer allocator.free(executable_directory);
        const integration_directory = try std.fmt.allocPrint(allocator, "{s}\\ShellIntegration", .{executable_directory});
        defer allocator.free(integration_directory);
        var environment = try std.process.getEnvMap(allocator);
        defer environment.deinit();
        var launch = try shell.ShellLaunch.resolve(allocator, request, system_directory, integration_directory, &environment);
        errdefer launch.deinit();
        try platform.setShellName(launch.shellName());
        return .{ .allocator = allocator, .api = api, .project_directory = parsed, .machine_name = machine_name, .launch = launch };
    }
    fn deinit(self: *WorkspaceApplication) void {
        // Window callbacks have stopped before this runs. Native worker teardown precedes VT free.
        for (self.sessions.items) |session| session.destroy(self.allocator);
        self.sessions.deinit(self.allocator);
        if (self.model) |*model| model.deinit();
        self.placements.deinit(self.allocator);
        self.allocator.free(self.project_directory.bytes);
        self.allocator.free(self.machine_name);
        self.launch.deinit();
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
    fn addSession(self: *WorkspaceApplication, path: directory.DirectoryPath) !*TerminalSession {
        // Reserving first prevents an allocation failure from stranding a live process.
        try self.sessions.ensureUnusedCapacity(self.allocator, 1);
        const token = self.next_token;
        self.next_token = try std.math.add(u32, token, 1);
        const session = try TerminalSession.create(self.allocator, &self.api, @enumFromInt(token), path, self.machine_name, &self.launch);
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
        const first = try self.addSession(self.project_directory);
        errdefer self.removeSession(first.id);
        self.model = try workspace.WorkspaceModel.init(self.allocator, first.id);
    }
    fn updateLayout(self: *WorkspaceApplication) !void {
        const model = if (self.model) |*model| model else return;
        const metrics = self.metrics orelse return;
        try model.activeTab().tree.layout(self.allocator, &self.placements, workspaceBounds(metrics));
        for (self.placements.items) |placement| {
            const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
            try session.resize(try metrics.terminalSize(placement.bounds));
        }
        platform.invalidateWindow();
    }
    pub fn onResize(self: *WorkspaceApplication, metrics: platform.WindowMetrics) void {
        if (self.health == .failed) return;
        _ = self.cancelDividerDrag() catch |err| {
            self.fail(err);
            return;
        };
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
        const previous = platform.statusBadgeKey(session.terminal.program_status.programStatus());
        const previous_attention = session.needs_attention;
        session.terminal.feedOutput(bytes) catch |err| {
            self.fail(err);
            return false;
        };
        const model = if (self.model) |*model| model else return false;
        const visible = model.activeTab().tree.containsPane(id);
        const attention = session.terminal.program_status.takeAttention();
        session.needs_attention = !visible and (session.needs_attention or attention);
        return visible or previous != platform.statusBadgeKey(session.terminal.program_status.programStatus()) or previous_attention != session.needs_attention;
    }
    pub fn onPaint(self: *WorkspaceApplication) void {
        if (self.health == .failed) return;
        self.paintWorkspace() catch |err| self.fail(err);
    }
    fn paintWorkspace(self: *WorkspaceApplication) !void {
        const model = if (self.model) |*model| model else return;
        for (model.tabs.items, 0..) |tab, index| {
            const presentation = try self.tabProgramStatus(tab.tree.root);
            platform.drawTab(index, model.tabs.items.len, index == model.selected_tab, presentation.summary, presentation.attention);
        }
        for (self.placements.items) |placement| {
            if (placement.bounds.width < 14 or placement.bounds.height < 38) continue;
            const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
            const focused = model.activeTab().active_pane == placement.id;
            const frame = try session.terminal.readFrame();
            platform.beginPane(placement.bounds, focused, session.terminal.program_status.programStatus());
            defer platform.endPane();
            try platform.drawTerminalFrame(frame, focused);
        }
    }
    const TabStatus = struct { summary: status.ProgramStatusSummary, attention: bool };
    fn tabProgramStatus(self: *WorkspaceApplication, node: anytype) anyerror!TabStatus {
        return switch (node.*) {
            .leaf => |id| blk: {
                const session = self.findSession(id) orelse return error.MissingPaneSession;
                break :blk .{ .summary = session.terminal.program_status.programStatus(), .attention = session.needs_attention };
            },
            .split => |split| blk: {
                const first = try self.tabProgramStatus(split.first);
                const second = try self.tabProgramStatus(split.second);
                break :blk .{ .summary = if (second.summary.state.displayPriority() > first.summary.state.displayPriority()) second.summary else first.summary, .attention = first.attention or second.attention };
            },
        };
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
            session.writeUserInput(bytes) catch |err| self.fail(err);
            return true;
        }
        return false;
    }
    pub fn onText(self: *WorkspaceApplication, bytes: []const u8) void {
        if (self.health == .failed) return;
        const session = self.activeSession() orelse return;
        if (session.state == .running) session.writeUserInput(bytes) catch |err| self.fail(err);
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
    fn cancelDividerDrag(self: *WorkspaceApplication) !bool {
        const model = if (self.model) |*model| model else return false;
        const changed = try self.divider_drag.cancel(&model.activeTab().tree);
        try platform.cancelDividerCapture();
        return changed;
    }
    pub fn onPointer(self: *WorkspaceApplication, event: platform.PointerEvent, x: f32, y: f32) platform.PointerCursor {
        if (self.health == .failed) return .normal;
        return self.handlePointer(event, x, y) catch |err| {
            self.fail(err);
            return .normal;
        };
    }
    fn handlePointer(self: *WorkspaceApplication, event: platform.PointerEvent, x: f32, y: f32) !platform.PointerCursor {
        const model = if (self.model) |*model| model else return .normal;
        const metrics = self.metrics orelse return .normal;
        const tree = &model.activeTab().tree;
        const bounds = workspaceBounds(metrics);
        switch (event) {
            .hover => {},
            .begin => if (!try self.divider_drag.begin(tree, bounds, x, y)) return .normal,
            .move, .end => {
                if (try self.divider_drag.move(tree, bounds, x, y)) try self.updateLayout();
                if (event == .end) {
                    self.divider_drag.finish();
                    return .normal;
                }
            },
            .cancel => {
                if (try self.cancelDividerDrag()) try self.updateLayout();
                return .normal;
            },
        }
        const axis = try self.divider_drag.cursorAxis(tree, bounds, x, y) orelse return .normal;
        return switch (axis) {
            .right => .right,
            .down => .down,
        };
    }
    pub fn onAction(self: *WorkspaceApplication, action: platform.WorkspaceAction, index: u32) void {
        if (self.health == .failed) return;
        self.applyAction(action, index) catch |err| self.fail(err);
    }
    fn applyAction(self: *WorkspaceApplication, action: platform.WorkspaceAction, index: u32) !void {
        if (try self.cancelDividerDrag()) try self.updateLayout();
        const model = if (self.model) |*model| model else return;
        switch (action) {
            .new_tab => {
                if (model.tabs.items.len >= workspace.WorkspaceModel.max_tabs) return;
                const session = try self.addSession(self.project_directory);
                errdefer self.removeSession(session.id);
                try model.newTab(session.id);
            },
            .split_right, .split_down => {
                const active = self.activeSession() orelse return error.MissingPaneSession;
                const session = try self.addSession(active.directory.current());
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
        for (self.sessions.items) |session| {
            if (model.activeTab().tree.containsPane(session.id)) session.needs_attention = false;
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
        session.terminal.program_status.onProgramExited();
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
            34 => return @intFromEnum(self.launch.kind),
            40 => return switch (model.activeTab().tree.root.*) {
                .leaf => 0,
                .split => |split| @intFromFloat(@round(split.ratio * 1000)),
            },
            41 => return @intFromBool(self.divider_drag.active != null),
            42 => return @intFromBool(platform.hasDividerCapture()),
            43 => return if (self.activeSession()) |session| if (session.size) |size| size.columns else 0 else 0,
            44 => return if (self.activeSession()) |session| if (session.size) |size| size.rows else 0 else 0,
            39 => {
                const session = self.activeSession() orelse return 0;
                const reported = session.directory.reported orelse return 0;
                return @intFromBool(!std.mem.eql(u8, reported, session.directory.launch));
            },
            31 => return if (self.activeSession()) |session| @intFromEnum(session.terminal.program_status.programStatus().state) else 0,
            32 => return @intFromEnum((self.tabProgramStatus(model.activeTab().tree.root) catch return 0).summary.state),
            33 => {
                var selected: status.ProgramStatusState = .none;
                for (model.tabs.items) |tab| {
                    const candidate = (self.tabProgramStatus(tab.tree.root) catch return 0).summary.state;
                    if (candidate.displayPriority() > selected.displayPriority()) selected = candidate;
                }
                return @intFromEnum(selected);
            },
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
            8 => "VEX_NATIVE_DIRECTORY_OK",
            9 => "VEX_NATIVE_ROOT_DIRECTORY_OK",
            30 => "VEX_NATIVE_DIRECTORY_REPORTED",
            35 => "VEX_NATIVE_SHELL_READY",
            36 => "VEX_NATIVE_SHELL_DIRECTORY_OK",
            37 => "VEX_NATIVE_SHELL_ENV_OK",
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

fn workspaceBounds(metrics: platform.WindowMetrics) panes.PaneRect {
    return .{ .x = 6, .y = 44, .width = @max(0, metrics.width - 12), .height = @max(0, metrics.height - 50) };
}

/// Retains the stable application/API owner until the window loop returns; teardown joins all session workers.
pub fn runApplication(allocator: std.mem.Allocator) !void {
    var app = try WorkspaceApplication.init(allocator);
    defer app.deinit();
    platform.runWindow(&app) catch |err| {
        if (app.health == .failed) return app.health.failed;
        return err;
    };
    if (app.health == .failed) return app.health.failed;
}
