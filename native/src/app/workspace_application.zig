const std = @import("std");
const binding = @import("../terminal/api.zig");
const platform = @import("../windows/windows_ffi.zig");
const panes = @import("../workspace/pane_tree.zig");
const workspace = @import("../workspace/workspace_model.zig");
const directory = @import("../shell/directory.zig");
const status = @import("../terminal/program_status.zig");
const shell = @import("../shell/shell_launch.zig");
const terminal = @import("../terminal/terminal.zig");
const TerminalSession = @import("terminal_session.zig").TerminalSession;
const wsl_registry = @import("../windows/wsl_registry.zig");
const DividerDrag = @import("../workspace/divider_drag.zig").DividerDrag;
const TabDrag = @import("../workspace/tab_drag.zig").TabDrag;
const tab_strip = @import("../workspace/tab_strip.zig");

const SelectionDrag = struct {
    pane_id: panes.PaneId,
    click_count: u8,
    dragged: bool = false,
    start_column: u16,
    start_row: u16,
    start_x_px: f64,
    start_y_px: f64,
};

const MouseDrag = struct {
    pane_id: panes.PaneId,
    button: terminal.MouseButton,
};

const PointerDrag = union(enum) { none, divider: DividerDrag, tab: TabDrag, selection: SelectionDrag, mouse: MouseDrag };

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
    pointer_drag: PointerDrag = .none,
    hovered_pane: ?panes.PaneId = null,
    hovered_header_action: ?panes.PaneHeaderAction = null,
    reported_mouse_pane: ?panes.PaneId = null,
    reported_mouse_buttons: u8 = 0,
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
    fn findPlacement(self: *WorkspaceApplication, id: panes.PaneId) ?panes.PanePlacement {
        for (self.placements.items) |placement| if (placement.id == id) return placement;
        return null;
    }
    fn clearOtherSelections(self: *WorkspaceApplication, keep: panes.PaneId) !void {
        for (self.sessions.items) |session| {
            if (session.id != keep and try session.terminal.hasSelection()) {
                try session.terminal.clearSelection(true);
            }
        }
    }
    fn reportMouseEvent(
        self: *WorkspaceApplication,
        session: *TerminalSession,
        action: terminal.MouseAction,
        button: terminal.MouseButton,
        modifiers: u32,
        vp: platform.ViewportPoint,
        any_button_pressed: bool,
    ) !void {
        _ = self;
        if (session.state != .running) return;
        var buffer: [128]u8 = undefined;
        const bytes = try session.terminal.encodeMouse(
            action,
            button,
            modifiers,
            @floatCast(vp.x_px),
            @floatCast(vp.y_px),
            any_button_pressed,
            &buffer,
        );
        if (bytes.len > 0) {
            try session.pty.write(bytes);
        }
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
        if (self.reported_mouse_pane == id) {
            self.reported_mouse_pane = null;
            self.reported_mouse_buttons = 0;
        }
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
        try platform.setTabStripLayout(try tab_strip.TabStripLayout.init(metrics.width, model.tabs.items.len));
        const tab = model.activeTab();
        const bounds = workspaceBounds(metrics);
        if (tab.is_focus_mode and tab.tree.paneCount() > 1) {
            try bounds.validate();
            try self.placements.ensureTotalCapacity(self.allocator, 1);
            self.placements.clearRetainingCapacity();
            self.placements.appendAssumeCapacity(.{ .id = tab.active_pane, .bounds = bounds });
        } else {
            tab.is_focus_mode = false;
            try tab.tree.layout(self.allocator, &self.placements, bounds);
        }
        for (self.placements.items) |placement| {
            const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
            try session.resize(try metrics.terminalSize(placement.bounds));
        }
        platform.invalidateWindow();
    }
    pub fn onResize(self: *WorkspaceApplication, metrics: platform.WindowMetrics) void {
        if (self.health == .failed) return;
        _ = self.cancelPointerDrag() catch |err| {
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
        const previous_title = session.terminal.title.generation;
        const previous_attention = session.needs_attention;
        session.terminal.feedOutput(bytes) catch |err| {
            self.fail(err);
            return false;
        };
        const model = if (self.model) |*model| model else return false;
        const visible = model.activeTab().tree.containsPane(id);
        const attention = session.terminal.program_status.takeAttention();
        session.needs_attention = !visible and (session.needs_attention or attention);
        const title_changed = previous_title != session.terminal.title.generation;
        return visible or title_changed or previous != platform.statusBadgeKey(session.terminal.program_status.programStatus()) or previous_attention != session.needs_attention;
    }
    pub fn onPaint(self: *WorkspaceApplication) void {
        if (self.health == .failed) return;
        self.paintWorkspace() catch |err| self.fail(err);
    }
    fn paintWorkspace(self: *WorkspaceApplication) !void {
        const model = if (self.model) |*model| model else return;
        for (model.tabs.items, 0..) |tab, index| {
            const presentation = try self.tabProgramStatus(tab.tree.root);
            const active_session = self.findSession(tab.active_pane) orelse return error.MissingPaneSession;
            platform.drawTab(index, model.tabs.items.len, index == model.selected_tab, active_session.terminal.title.slice(), presentation.summary, presentation.attention);
        }
        if (self.pointer_drag == .tab) {
            const metrics = self.metrics orelse return;
            if (try self.pointer_drag.tab.markerX(model, try tab_strip.TabStripLayout.init(metrics.width, model.tabs.items.len))) |x| try platform.drawTabInsertionMarker(x);
        }
        const active_tab = model.activeTab();
        const can_focus = active_tab.tree.paneCount() > 1;
        for (self.placements.items) |placement| {
            if (placement.bounds.width < 14 or placement.bounds.height < 38) continue;
            const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
            const focused = active_tab.active_pane == placement.id;
            const hovered = self.hovered_pane == placement.id;
            const hovered_action = if (hovered) self.hovered_header_action else null;
            const frame = try session.terminal.readFrame();
            platform.beginPane(placement.bounds, focused, hovered, can_focus, active_tab.is_focus_mode, hovered_action, session.terminal.title.slice(), session.terminal.program_status.programStatus());
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
        if (isModifierKey(key)) return false;
        if (platform.workspaceShortcut(key, modifiers)) |action| {
            self.onAction(action, 0);
            return true;
        }
        const session = self.activeSession() orelse return false;
        return self.handleSessionKey(session, key, modifiers) catch |err| {
            self.fail(err);
            return true;
        };
    }
    fn handleSessionKey(self: *WorkspaceApplication, session: *TerminalSession, key: u32, modifiers: u32) !bool {
        if (try session.terminal.scrollViewportShortcut(key, modifiers)) {
            platform.invalidateWindow();
            return true;
        }
        if (modifiers == 3 and key == 'C') {
            if (try session.copySelectionToClipboard(self.allocator)) {
                try session.terminal.clearSelection(true);
                platform.invalidateWindow();
            }
            return true;
        }
        if (modifiers == 2 and key == 'C' and try session.terminal.hasSelection()) {
            _ = try session.copySelectionToClipboard(self.allocator);
            try session.terminal.clearSelection(true);
            platform.invalidateWindow();
            return true;
        }
        if (modifiers == 3 and key == 'X') {
            if (try session.terminal.hasSelection()) {
                _ = try session.copySelectionToClipboard(self.allocator);
                if (!try session.deleteKeyboardSelection()) try session.terminal.clearSelection(true);
                platform.invalidateWindow();
            }
            return true;
        }
        if (session.state == .exited) return true;
        if (key == 0x08 and (modifiers == 0 or modifiers == 1)) {
            if (try session.deleteKeyboardSelection()) {
                platform.invalidateWindow();
                return true;
            }
        }
        if (try session.terminal.extendKeyboardSelection(key, modifiers)) {
            platform.invalidateWindow();
            return true;
        }
        var buffer: [256]u8 = undefined;
        const encoded = try session.terminal.encodeKey(key, modifiers, &buffer);
        if (encoded) |bytes| {
            if (try session.terminal.hasSelection()) {
                try session.terminal.clearSelection(true);
                platform.invalidateWindow();
            }
            try session.writeUserInput(bytes);
            return true;
        }
        return false;
    }
    pub fn onText(self: *WorkspaceApplication, bytes: []const u8) void {
        if (self.health == .failed) return;
        const session = self.activeSession() orelse return;
        if (session.state == .running) {
            if (session.terminal.hasSelection() catch |err| {
                self.fail(err);
                return;
            }) {
                session.terminal.clearSelection(true) catch |err| {
                    self.fail(err);
                    return;
                };
                platform.invalidateWindow();
            }
            session.writeUserInput(bytes) catch |err| self.fail(err);
        }
    }
    pub fn onPaste(self: *WorkspaceApplication, bytes: []const u8) void {
        if (self.health == .failed) return;
        const session = self.activeSession() orelse return;
        if (session.state == .running) {
            if (session.terminal.hasSelection() catch |err| {
                self.fail(err);
                return;
            }) {
                session.terminal.clearSelection(true) catch |err| {
                    self.fail(err);
                    return;
                };
                platform.invalidateWindow();
            }
            session.paste(self.allocator, bytes) catch |err| self.fail(err);
        }
    }
    pub fn onScroll(self: *WorkspaceApplication, delta: i32, x: f32, y: f32, modifiers: u32) void {
        if (self.health == .failed or delta == 0) return;
        self.handleScroll(delta, x, y, modifiers) catch |err| self.fail(err);
    }
    fn handleScroll(self: *WorkspaceApplication, delta: i32, x: f32, y: f32, modifiers: u32) !void {
        const metrics = self.metrics orelse return;
        for (self.placements.items) |placement| if (placement.bounds.contains(x, y)) {
            const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
            if ((modifiers & 1) == 0 and try session.terminal.isMouseTracking()) {
                if (try metrics.viewportPoint(placement.bounds, x, y, true)) |vp| {
                    const wheel_button: terminal.MouseButton = if (delta < 0) .wheel_up else .wheel_down;
                    const steps: usize = @max(1, @divTrunc(@abs(delta), 3));
                    const any_pressed = (self.pointer_drag == .mouse and self.pointer_drag.mouse.pane_id == placement.id) or
                        (self.reported_mouse_pane == placement.id and self.reported_mouse_buttons != 0);
                    for (0..steps) |_| {
                        try self.reportMouseEvent(session, .press, wheel_button, modifiers, vp, any_pressed);
                        try self.reportMouseEvent(session, .release, wheel_button, modifiers, vp, any_pressed);
                    }
                    return;
                }
            }
            session.terminal.scroll(delta);
            platform.invalidateWindow();
            return;
        };
        const session = self.activeSession() orelse return;
        session.terminal.scroll(delta);
        platform.invalidateWindow();
    }
    pub fn onMouseButton(self: *WorkspaceApplication, button: terminal.MouseButton, pressed: bool, x: f32, y: f32, modifiers: u32) void {
        if (self.health == .failed) return;
        self.handleMouseButton(button, pressed, x, y, modifiers) catch |err| self.fail(err);
    }
    fn handleMouseButton(self: *WorkspaceApplication, button: terminal.MouseButton, pressed: bool, x: f32, y: f32, modifiers: u32) !void {
        const model = if (self.model) |*model| model else return;
        const metrics = self.metrics orelse return;
        const can_focus = model.activeTab().tree.paneCount() > 1;
        const bit: u8 = if (button == .middle) 2 else if (button == .right) 4 else 1;
        if (pressed) {
            for (self.placements.items) |placement| if (placement.bounds.contains(x, y)) {
                if (placement.bounds.hitHeaderAction(can_focus, x, y) != null) return;
                const vp = (try metrics.viewportPoint(placement.bounds, x, y, false)) orelse return;
                const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
                _ = model.focusPane(placement.id);
                try self.clearOtherSelections(placement.id);
                platform.invalidateWindow();
                if ((modifiers & 1) == 0 and try session.terminal.isMouseTracking()) {
                    self.reported_mouse_pane = placement.id;
                    self.reported_mouse_buttons |= bit;
                    try self.reportMouseEvent(session, .press, button, modifiers, vp, true);
                } else if (button == .right) {
                    platform.pasteClipboard();
                }
                return;
            };
        } else {
            const pane_id = self.reported_mouse_pane orelse return;
            if ((self.reported_mouse_buttons & bit) == 0) return;
            self.reported_mouse_buttons &= ~bit;
            const any_remaining = self.reported_mouse_buttons != 0;
            if (!any_remaining) self.reported_mouse_pane = null;
            const placement = self.findPlacement(pane_id) orelse return;
            const session = self.findSession(pane_id) orelse return;
            if (try session.terminal.isMouseTracking()) {
                if (try metrics.viewportPoint(placement.bounds, x, y, true)) |vp| {
                    try self.reportMouseEvent(session, .release, button, modifiers, vp, any_remaining);
                }
            }
        }
    }
    pub fn onClick(self: *WorkspaceApplication, x: f32, y: f32) void {
        if (self.health == .failed) return;
        self.handleClick(x, y) catch |err| self.fail(err);
    }
    fn handleClick(self: *WorkspaceApplication, x: f32, y: f32) !void {
        const model = if (self.model) |*model| model else return;
        const can_focus = model.activeTab().tree.paneCount() > 1;
        for (self.placements.items) |placement| if (placement.bounds.contains(x, y)) {
            if (placement.bounds.hitHeaderAction(can_focus, x, y)) |header_action| {
                switch (header_action) {
                    .focus_toggle => {
                        _ = model.focusPane(placement.id);
                        try self.clearOtherSelections(placement.id);
                        if (model.toggleFocusMode()) try self.updateLayout() else platform.invalidateWindow();
                    },
                    .split_right, .split_down => {
                        const target_session = self.findSession(placement.id) orelse return error.MissingPaneSession;
                        const session = try self.addSession(target_session.directory.current());
                        errdefer self.removeSession(session.id);
                        try model.splitPane(placement.id, session.id, if (header_action == .split_right) .right else .down);
                        try self.updateLayout();
                    },
                    .close => {
                        if (model.closePaneSplit(placement.id)) |id| {
                            self.removeSession(id);
                            try self.updateLayout();
                        } else {
                            try self.closeTab(model.selected_tab);
                            try self.updateLayout();
                        }
                    },
                }
                return;
            }
            _ = model.focusPane(placement.id);
            try self.clearOtherSelections(placement.id);
            platform.invalidateWindow();
            return;
        };
    }
    fn updateHoverState(self: *WorkspaceApplication, can_focus: bool, x: f32, y: f32) void {
        var next_pane: ?panes.PaneId = null;
        var next_action: ?panes.PaneHeaderAction = null;
        if (self.pointer_drag == .none) {
            for (self.placements.items) |placement| if (placement.bounds.contains(x, y)) {
                next_pane = placement.id;
                next_action = placement.bounds.hitHeaderAction(can_focus, x, y);
                break;
            };
        }
        if (self.hovered_pane != next_pane or self.hovered_header_action != next_action) {
            self.hovered_pane = next_pane;
            self.hovered_header_action = next_action;
            platform.invalidateWindow();
        }
    }
    fn cancelPointerDrag(self: *WorkspaceApplication) !bool {
        const model = if (self.model) |*model| model else return false;
        const clear_preview = self.pointer_drag == .tab or self.pointer_drag == .selection;
        const changed = switch (self.pointer_drag) {
            .none, .tab, .mouse => false,
            .selection => |sel| blk: {
                if (self.findSession(sel.pane_id)) |session| {
                    try session.terminal.selectionRelease(sel.start_column, sel.start_row);
                    if (!sel.dragged and sel.click_count <= 1) {
                        try session.terminal.clearSelection(false);
                    }
                }
                break :blk false;
            },
            .divider => |*drag| try drag.cancel(&model.activeTab().tree),
        };
        self.pointer_drag = .none;
        self.reported_mouse_pane = null;
        self.reported_mouse_buttons = 0;
        try platform.cancelPointerCapture();
        if (clear_preview) platform.invalidateWindow();
        return changed;
    }
    pub fn onPointer(self: *WorkspaceApplication, event: platform.PointerEvent, x: f32, y: f32, modifiers: u32) platform.PointerResponse {
        if (self.health == .failed) return .normal;
        return self.handlePointer(event, x, y, modifiers) catch |err| {
            self.fail(err);
            return .normal;
        };
    }
    fn handlePointer(self: *WorkspaceApplication, event: platform.PointerEvent, x: f32, y: f32, modifiers: u32) !platform.PointerResponse {
        const model = if (self.model) |*model| model else return .normal;
        const metrics = self.metrics orelse return .normal;
        const layout = try tab_strip.TabStripLayout.init(metrics.width, model.tabs.items.len);
        const point: tab_strip.TabPointer = .{ .x = x, .y = y };
        const bounds = workspaceBounds(metrics);
        const can_focus = model.activeTab().tree.paneCount() > 1;
        switch (event) {
            .hover => {
                self.updateHoverState(can_focus, x, y);
                if (self.pointer_drag == .none and (modifiers & 1) == 0) {
                    for (self.placements.items) |placement| if (placement.bounds.contains(x, y)) {
                        const session = self.findSession(placement.id) orelse break;
                        if (try session.terminal.isMouseTracking()) {
                            if (try metrics.viewportPoint(placement.bounds, x, y, false)) |vp| {
                                const any_pressed = self.reported_mouse_pane == placement.id and self.reported_mouse_buttons != 0;
                                const button: terminal.MouseButton = if ((self.reported_mouse_buttons & 2) != 0) .middle else if ((self.reported_mouse_buttons & 4) != 0) .right else .none;
                                try self.reportMouseEvent(session, .motion, button, modifiers, vp, any_pressed);
                            }
                        }
                        break;
                    };
                }
            },
            .begin => {
                if (try layout.hitTab(point)) |hit| {
                    if (hit.part == .close) return .normal;
                    try self.applyAction(.select_tab, @intCast(hit.index));
                    var drag: TabDrag = .none;
                    if (try drag.begin(model, layout, model.activeTab().id, point)) self.pointer_drag = .{ .tab = drag };
                } else {
                    if (!model.activeTab().is_focus_mode) {
                        var drag: DividerDrag = .{};
                        if (try drag.begin(&model.activeTab().tree, bounds, x, y)) self.pointer_drag = .{ .divider = drag };
                    }
                    if (self.pointer_drag == .none) {
                        for (self.placements.items) |placement| if (placement.bounds.contains(x, y)) {
                            if (placement.bounds.hitHeaderAction(can_focus, x, y) != null) break;
                            if (try metrics.viewportPoint(placement.bounds, x, y, false)) |vp| {
                                _ = model.focusPane(placement.id);
                                try self.clearOtherSelections(placement.id);
                                const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
                                if ((modifiers & 1) == 0 and try session.terminal.isMouseTracking()) {
                                    if (try session.terminal.hasSelection()) {
                                        try session.terminal.clearSelection(true);
                                    }
                                    try self.reportMouseEvent(session, .press, .left, modifiers, vp, true);
                                    self.pointer_drag = .{ .mouse = .{ .pane_id = placement.id, .button = .left } };
                                } else {
                                    const click_count = try session.terminal.selectionPress(vp.column, vp.row, vp.x_px, vp.y_px);
                                    self.pointer_drag = .{ .selection = .{
                                        .pane_id = placement.id,
                                        .click_count = click_count,
                                        .start_column = vp.column,
                                        .start_row = vp.row,
                                        .start_x_px = vp.x_px,
                                        .start_y_px = vp.y_px,
                                    } };
                                }
                                platform.invalidateWindow();
                            }
                            break;
                        };
                    }
                }
            },
            .move, .end => {
                switch (self.pointer_drag) {
                    .none => self.updateHoverState(can_focus, x, y),
                    .divider => |*drag| if (try drag.move(&model.activeTab().tree, bounds, x, y)) try self.updateLayout(),
                    .tab => |*drag| {
                        if (event == .end) {
                            _ = try drag.finish(model, layout, point);
                            platform.invalidateWindow();
                        } else if (try drag.move(model, layout, point)) platform.invalidateWindow();
                    },
                    .selection => |*sel| {
                        if (self.findPlacement(sel.pane_id)) |placement| {
                            if (self.findSession(sel.pane_id)) |session| {
                                if (try metrics.viewportPoint(placement.bounds, x, y, true)) |vp| {
                                    if (event == .end) {
                                        try session.terminal.selectionRelease(vp.column, vp.row);
                                        if (!sel.dragged and sel.click_count <= 1) {
                                            // A single click without drag clears the 1-cell anchor while keeping multi-click history.
                                            try session.terminal.clearSelection(false);
                                        } else {
                                            _ = try session.copySelectionToClipboard(self.allocator);
                                        }
                                        platform.invalidateWindow();
                                    } else {
                                        const moved_cell = vp.column != sel.start_column or vp.row != sel.start_row;
                                        const moved_px = @abs(vp.x_px - sel.start_x_px) >= 3.0 or @abs(vp.y_px - sel.start_y_px) >= 3.0;
                                        if (sel.dragged or moved_cell or moved_px or sel.click_count > 1) {
                                            if (moved_cell or moved_px) sel.dragged = true;
                                            try session.terminal.selectionDrag(vp.column, vp.row, vp.x_px, vp.y_px);
                                            platform.invalidateWindow();
                                        }
                                    }
                                }
                            }
                        }
                    },
                    .mouse => |mouse_drag| {
                        if (self.findPlacement(mouse_drag.pane_id)) |placement| {
                            if (self.findSession(mouse_drag.pane_id)) |session| {
                                if (try metrics.viewportPoint(placement.bounds, x, y, true)) |vp| {
                                    if (event == .end) {
                                        try self.reportMouseEvent(session, .release, mouse_drag.button, modifiers, vp, false);
                                    } else {
                                        try self.reportMouseEvent(session, .motion, mouse_drag.button, modifiers, vp, true);
                                    }
                                }
                            }
                        }
                    },
                }
                if (event == .end) {
                    self.pointer_drag = .none;
                    self.updateHoverState(model.activeTab().tree.paneCount() > 1, x, y);
                    return .normal;
                }
            },
            .cancel => {
                if (try self.cancelPointerDrag()) try self.updateLayout();
                return .normal;
            },
        }
        if (self.pointer_drag == .tab) return switch (self.pointer_drag.tab) {
            .none => .normal,
            .armed => .tab_armed,
            .dragging => .tab_dragging,
        };
        if (self.pointer_drag == .selection or self.pointer_drag == .mouse) return .selection;
        if (model.activeTab().is_focus_mode) return .normal;
        const drag: DividerDrag = if (self.pointer_drag == .divider) self.pointer_drag.divider else .{};
        const axis = try drag.cursorAxis(&model.activeTab().tree, bounds, x, y) orelse return .normal;
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
        if (try self.cancelPointerDrag()) try self.updateLayout();
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
            41 => return @intFromBool(self.pointer_drag == .divider and self.pointer_drag.divider.active != null),
            42 => return @intFromBool(platform.hasPointerCapture()),
            45 => return if (self.pointer_drag == .tab) switch (self.pointer_drag.tab) {
                .none => 0,
                .armed => 1,
                .dragging => 2,
            } else 0,
            46 => return @intFromEnum(model.activeTab().id),
            47 => return model.selected_tab,
            48 => {
                const metrics = self.metrics orelse return 0;
                const layout = tab_strip.TabStripLayout.init(metrics.width, model.tabs.items.len) catch return 0;
                return @intFromFloat(@round(layout.tab_width * 1000));
            },
            49 => return if (self.pointer_drag == .tab and self.pointer_drag.tab == .dragging) if (self.pointer_drag.tab.dragging.destination) |index| index + 1 else 0 else 0,
            50 => return @intFromBool(model.activeTab().is_focus_mode),
            60...67 => return if (query - 60 < model.tabs.items.len) @intFromEnum(model.tabs.items[query - 60].id) else 0,
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

fn isModifierKey(key: u32) bool {
    return switch (key) {
        0x10, 0x11, 0x12, 0x14, 0x5B, 0x5C, 0x90, 0xA0...0xA5 => true,
        else => false,
    };
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
