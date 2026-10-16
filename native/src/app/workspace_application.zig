const std = @import("std");
const binding = @import("../terminal/api.zig");
const platform = @import("../windows/windows_ffi.zig");
const panes = @import("../workspace/pane_tree.zig");
const workspace = @import("../workspace/workspace_model.zig");
const directory = @import("../shell/directory.zig");
const status = @import("../terminal/program_status.zig");
const status_presentation = @import("../terminal/status_presentation.zig");
const shell = @import("../shell/shell_launch.zig");
const terminal = @import("../terminal/terminal.zig");
const TerminalSession = @import("terminal_session.zig").TerminalSession;
const wsl_registry = @import("../windows/wsl_registry.zig");
const DividerDrag = @import("../workspace/divider_drag.zig").DividerDrag;
const TabDrag = @import("../workspace/tab_drag.zig").TabDrag;
const tab_strip = @import("../workspace/tab_strip.zig");
const app_icon = @import("../terminal/app_icon.zig");
const url_detector = @import("../terminal/url_detector.zig");
const terminal_theme = @import("../workspace/terminal_theme.zig");
const path_completion = @import("../terminal/path_completion.zig");
const close_confirmation = @import("../workspace/close_confirmation.zig");
const cursor_blink = @import("../workspace/cursor_blink.zig");
const status_pulse = @import("../workspace/status_pulse.zig");

const HoveredLink = struct {
    pane_id: panes.PaneId,
    row: u16,
    start_col: u16,
    end_col: u16,
};

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

const ScrollbarDrag = struct {
    pane_id: panes.PaneId,
    grab_offset: f32,
};

const PaneDrag = struct {
    pane_id: panes.PaneId,
    start_x: f32,
    start_y: f32,
    dragging: bool = false,
    zone: panes.DropZone = .none,
};

const TabRename = struct {
    tab_id: workspace.TabId,
    buf: [64]u8 = undefined,
    len: u8 = 0,
    select_all: bool = true,
};

const ThemePickerOverlay = struct {
    dark_mode: bool,
    selected_slot: usize,
    hovered_slot: ?usize = null,
    initial_theme_index: usize,
};

const CommandPaletteOverlay = struct {
    query_buf: [64]u8 = undefined,
    query_len: u8 = 0,
    selected_index: usize = 0,
    hovered_index: ?usize = null,
};

const TabPeekOverlay = struct {
    selected_index: usize = 0,
    hovered_index: ?usize = null,
};

const ShellPickerMode = enum(u8) {
    new_tab = 0,
    split_right = 1,
    split_down = 2,
    set_default = 3,
};

const ShellPickerOverlay = struct {
    mode: ShellPickerMode = .new_tab,
    selected_index: usize = 0,
    hovered_index: ?usize = null,
};

const CloseTarget = union(enum) {
    pane: panes.PaneId,
    tab: workspace.TabId,
};

const CloseConfirmOverlay = struct {
    target: CloseTarget,
    confirmation: close_confirmation.CloseConfirmation,
    selected_button: u8 = 1,
    hovered_button: u8 = 0,
};

const OverlayState = union(enum) {
    none,
    theme_picker: ThemePickerOverlay,
    command_palette: CommandPaletteOverlay,
    tab_peek: TabPeekOverlay,
    path_completion: struct { pane_id: panes.PaneId },
    close_confirm: CloseConfirmOverlay,
    shell_picker: ShellPickerOverlay,
};

const CommandId = enum {
    open_path_completion,
    open_theme_picker,
    toggle_dark_light,
    open_tab_peek,
    zoom_in,
    zoom_out,
    zoom_reset,
    new_tab,
    new_tab_with_shell,
    split_right,
    split_right_with_shell,
    split_down,
    split_down_with_shell,
    change_default_shell,
    toggle_focus_mode,
    rename_active_tab,
    next_tab,
    previous_tab,
    close_active,
    toggle_cursor_blink,
};

const CommandDef = struct {
    id: CommandId,
    title: []const u8,
    shortcut: []const u8,
};

const command_defs = [_]CommandDef{
    .{ .id = .open_path_completion, .title = "Path Completion: Insert File or Folder Path...", .shortcut = "Ctrl+Shift+F" },
    .{ .id = .open_theme_picker, .title = "Color Theme: Open Theme Studio...", .shortcut = "Ctrl+Shift+M" },
    .{ .id = .toggle_dark_light, .title = "Color Theme: Toggle Dark / Light Mode", .shortcut = "Tab in Studio" },
    .{ .id = .open_tab_peek, .title = "Tab Overview: Peek All Tabs", .shortcut = "Ctrl+Shift+Space" },
    .{ .id = .zoom_in, .title = "Font Size: Zoom In", .shortcut = "Ctrl++" },
    .{ .id = .zoom_out, .title = "Font Size: Zoom Out", .shortcut = "Ctrl+-" },
    .{ .id = .zoom_reset, .title = "Font Size: Reset Zoom", .shortcut = "Ctrl+0" },
    .{ .id = .new_tab, .title = "Workspace: New Tab", .shortcut = "Ctrl+Shift+T" },
    .{ .id = .new_tab_with_shell, .title = "Workspace: New Tab with Shell...", .shortcut = "" },
    .{ .id = .split_right, .title = "Pane: Split Right", .shortcut = "Ctrl+Shift+R" },
    .{ .id = .split_right_with_shell, .title = "Pane: Split Right with Shell...", .shortcut = "" },
    .{ .id = .split_down, .title = "Pane: Split Down", .shortcut = "Ctrl+Shift+D" },
    .{ .id = .split_down_with_shell, .title = "Pane: Split Down with Shell...", .shortcut = "" },
    .{ .id = .change_default_shell, .title = "Shell: Change Default Shell...", .shortcut = "" },
    .{ .id = .toggle_focus_mode, .title = "Pane: Toggle Focus / Zoom Mode", .shortcut = "" },
    .{ .id = .rename_active_tab, .title = "Tab: Rename Active Tab", .shortcut = "Double-Click" },
    .{ .id = .next_tab, .title = "Workspace: Next Tab", .shortcut = "Ctrl+Tab" },
    .{ .id = .previous_tab, .title = "Workspace: Previous Tab", .shortcut = "Ctrl+Shift+Tab" },
    .{ .id = .close_active, .title = "Workspace: Close Active Pane or Tab", .shortcut = "Ctrl+Shift+W" },
    .{ .id = .toggle_cursor_blink, .title = "Cursor: Toggle Blinking", .shortcut = "" },
};

const PointerDrag = union(enum) { none, divider: DividerDrag, tab: TabDrag, pane: PaneDrag, selection: SelectionDrag, mouse: MouseDrag, scrollbar: ScrollbarDrag };

// This application owns sessions at stable addresses; tab/tree nodes only contain IDs.
const WorkspaceApplication = struct {
    allocator: std.mem.Allocator,
    api: binding.Api,
    project_directory: directory.DirectoryPath,
    machine_name: []u8,
    launch: shell.ShellLaunch,
    default_shell_kind: shell.ShellKind,
    shell_launches: std.ArrayList(*shell.ShellLaunch) = .empty,
    available_shells: [6]shell.ShellKind = undefined,
    available_shell_count: u8 = 0,
    sessions: std.ArrayList(*TerminalSession) = .empty,
    model: ?workspace.WorkspaceModel = null,
    placements: std.ArrayList(panes.PanePlacement) = .empty,
    metrics: ?platform.WindowMetrics = null,
    pointer_drag: PointerDrag = .none,
    editing_tab: ?TabRename = null,
    overlay: OverlayState = .none,
    path_completion: path_completion.PathCompletion,
    cursor_clock: std.time.Timer,
    cursor_blink: cursor_blink.CursorBlink = .{},
    status_pulse: status_pulse.StatusPulse = .{},
    cursor_blink_enabled: bool = true,
    cursor_window_active: bool = false,
    theme_index: usize = terminal_theme.default_dark_index,
    last_dark_theme: usize = terminal_theme.default_dark_index,
    last_light_theme: usize = terminal_theme.default_light_index,
    hovered_pane: ?panes.PaneId = null,
    hovered_header_action: ?panes.PaneHeaderAction = null,
    hovered_scrollbar: ?panes.PaneId = null,
    hovered_link: ?HoveredLink = null,
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
        const default_kind = launch.kind;
        try platform.setShellName(launch.shellName());
        var branch_buf: [128]u8 = undefined;
        const branch = readGitBranch(parsed.bytes, &branch_buf);
        try platform.setProjectInfo(projectDisplayName(parsed.bytes), branch);
        platform.setChromePalette(terminal_theme.themes[terminal_theme.default_dark_index].chromePalette());
        return .{
            .allocator = allocator,
            .api = api,
            .project_directory = parsed,
            .machine_name = machine_name,
            .launch = launch,
            .default_shell_kind = default_kind,
            .path_completion = path_completion.PathCompletion.init(allocator),
            .cursor_clock = try std.time.Timer.start(),
        };
    }
    fn deinit(self: *WorkspaceApplication) void {
        // Window callbacks have stopped before this runs. Native worker teardown precedes VT free.
        for (self.sessions.items) |session| session.destroy(self.allocator);
        self.sessions.deinit(self.allocator);
        if (self.model) |*model| model.deinit();
        self.placements.deinit(self.allocator);
        self.path_completion.deinit();
        self.allocator.free(self.project_directory.bytes);
        self.allocator.free(self.machine_name);
        for (self.shell_launches.items) |cached| {
            cached.deinit();
            self.allocator.destroy(cached);
        }
        self.shell_launches.deinit(self.allocator);
        self.launch.deinit();
    }
    pub fn fail(self: *WorkspaceApplication, err: anyerror) void {
        if (self.health == .failed) return;
        self.health = .{ .failed = err };
        platform.closeWindow();
    }
    fn cursorNowMs(self: *WorkspaceApplication) u64 {
        return self.cursor_clock.read() / std.time.ns_per_ms;
    }
    fn cursorBlinkEligible(self: *WorkspaceApplication) bool {
        const session = self.activeSession() orelse return false;
        return self.cursor_blink_enabled and self.cursor_window_active and self.overlay == .none and
            self.editing_tab == null and self.cursor_blink.state == .blinking and session.state == .running and
            self.cursor_blink.state.blinking.key.pane == session.id;
    }
    /// Animated status dots repaint every visible tab; hidden terminal grids stay unmaterialized.
    fn anyAnimatedStatus(self: *WorkspaceApplication) bool {
        const model = if (self.model) |*model| model else return false;
        if (!self.cursor_window_active or self.overlay != .none or self.editing_tab != null) return false;
        for (model.tabs.items) |tab| {
            const presentation = self.tabProgramStatus(tab.tree.root) catch return false;
            if (status_pulse.StatusPulse.animates(@intFromEnum(presentation.summary.state))) return true;
        }
        for (self.placements.items) |placement| {
            if (self.findSession(placement.id)) |session| {
                const summary = session.terminal.program_status.programStatus();
                if (status_pulse.StatusPulse.animates(@intFromEnum(summary.state))) return true;
            }
        }
        return false;
    }
    /// Cursor blink and status pulse share one Win32 wake, armed to the earliest pending step.
    fn syncAnimationWake(self: *WorkspaceApplication) !void {
        const now = self.cursorNowMs();
        self.status_pulse.synchronize(self.anyAnimatedStatus(), now);
        const blink_delay = self.cursor_blink.nextDelayMs(now);
        const pulse_delay = self.status_pulse.nextDelayMs(now);
        const delay: ?u32 = if (blink_delay) |bd| (if (pulse_delay) |pd| @min(bd, pd) else bd) else pulse_delay;
        try platform.scheduleCursorWake(delay);
    }
    fn resetCursorOnInput(self: *WorkspaceApplication) !void {
        const now = self.cursorNowMs();
        if (self.cursor_blink.resetOnInput(now)) platform.invalidateWindow();
        try self.syncAnimationWake();
    }
    pub fn onCursorActivity(self: *WorkspaceApplication, active: bool) void {
        if (self.health == .failed or self.cursor_window_active == active) return;
        self.cursor_window_active = active;
        self.cursor_blink.synchronize(null, self.cursorNowMs());
        self.syncAnimationWake() catch |err| {
            self.fail(err);
            return;
        };
        platform.invalidateWindow();
    }
    pub fn onCursorWake(self: *WorkspaceApplication) void {
        if (self.health == .failed) return;
        const now = self.cursorNowMs();
        var repaint = false;
        if (self.cursorBlinkEligible()) {
            if (self.cursor_blink.advance(now)) repaint = true;
        } else {
            self.cursor_blink.synchronize(null, now);
        }
        if (self.status_pulse.advance(now)) repaint = true;
        if (repaint) platform.invalidateWindow();
        self.syncAnimationWake() catch |err| self.fail(err);
    }
    fn currentTheme(self: *const WorkspaceApplication) terminal_theme.TerminalTheme {
        return terminal_theme.themes[@min(self.theme_index, terminal_theme.themes.len - 1)];
    }
    fn applyTheme(self: *WorkspaceApplication, index: usize) !void {
        const clamped = @min(index, terminal_theme.themes.len - 1);
        self.theme_index = clamped;
        const t = terminal_theme.themes[clamped];
        if (t.is_dark) {
            self.last_dark_theme = clamped;
        } else {
            self.last_light_theme = clamped;
        }
        platform.setChromePalette(t.chromePalette());
        for (self.sessions.items) |session| {
            try session.terminal.applyPalette(t.foreground, t.background, t.cursor, &t.ansi);
        }
        platform.invalidateWindow();
    }
    fn toggleDarkLightTheme(self: *WorkspaceApplication) !void {
        const current = self.currentTheme();
        const target = if (current.is_dark) self.last_light_theme else self.last_dark_theme;
        try self.applyTheme(target);
    }
    fn refreshProjectChrome(self: *WorkspaceApplication) void {
        const dir_bytes = if (self.activeSession()) |session| session.directory.current().bytes else self.project_directory.bytes;
        var branch_buf: [128]u8 = undefined;
        const branch = readGitBranch(dir_bytes, &branch_buf);
        platform.setProjectInfo(projectDisplayName(dir_bytes), branch) catch {};
    }
    fn findSession(self: *WorkspaceApplication, id: panes.PaneId) ?*TerminalSession {
        for (self.sessions.items) |session| if (session.id == id) return session;
        return null;
    }
    fn findPlacement(self: *WorkspaceApplication, id: panes.PaneId) ?panes.PanePlacement {
        for (self.placements.items) |placement| if (placement.id == id) return placement;
        return null;
    }
    fn beginTabRename(self: *WorkspaceApplication, index: usize) void {
        self.commitTabRename();
        const model = if (self.model) |*model| model else return;
        if (index >= model.tabs.items.len) return;
        _ = model.selectTab(index);
        const tab = model.tabs.items[index];
        const active_session = self.findSession(tab.active_pane) orelse return;
        const initial = tab.customTitle() orelse active_session.displayTitle();
        var edit: TabRename = .{ .tab_id = tab.id, .select_all = true };
        const n = @min(initial.len, edit.buf.len);
        @memcpy(edit.buf[0..n], initial[0..n]);
        edit.len = @intCast(n);
        self.editing_tab = edit;
        platform.invalidateWindow();
    }
    fn commitTabRename(self: *WorkspaceApplication) void {
        const edit = self.editing_tab orelse return;
        self.editing_tab = null;
        const model = if (self.model) |*model| model else return;
        if (model.tabIndex(edit.tab_id)) |idx| {
            model.tabs.items[idx].setCustomTitle(edit.buf[0..edit.len]);
        }
        platform.invalidateWindow();
    }
    fn cancelTabRename(self: *WorkspaceApplication) void {
        if (self.editing_tab != null) {
            self.editing_tab = null;
            platform.invalidateWindow();
        }
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
    fn discoverAvailableShells(self: *WorkspaceApplication) void {
        if (self.available_shell_count > 0) return;
        const system_directory = platform.systemDirectoryAlloc(self.allocator) catch null;
        defer if (system_directory) |dir| self.allocator.free(dir);
        const executable_directory = std.fs.selfExeDirPathAlloc(self.allocator) catch null;
        defer if (executable_directory) |dir| self.allocator.free(dir);
        const integration_directory = if (executable_directory) |dir| std.fmt.allocPrint(self.allocator, "{s}\\ShellIntegration", .{dir}) catch null else null;
        defer if (integration_directory) |dir| self.allocator.free(dir);
        var environment = std.process.getEnvMap(self.allocator) catch null;
        defer if (environment) |*env| env.deinit();

        const candidates = [_]shell.ShellKind{ .pwsh, .powershell, .cmd, .nu, .gitbash, .wsl };
        var count: u8 = 0;
        for (candidates) |candidate| {
            var available = candidate == self.launch.kind;
            if (!available) if (system_directory) |sys_dir| if (integration_directory) |int_dir| if (environment) |*env_ptr| {
                if (candidate == .wsl) {
                    if (shell.isWslLauncherAvailable(self.allocator, sys_dir) catch false) {
                        if (wsl_registry.readDefaultWslDistributionAlloc(self.allocator)) |distro| {
                            self.allocator.free(distro.bytes);
                            available = true;
                        } else |_| {}
                    }
                } else if (candidate.builtinProfile()) |profile| {
                    available = shell.isBuiltinShellAvailable(self.allocator, profile, sys_dir, int_dir, env_ptr) catch false;
                }
            };
            if (available and count < self.available_shells.len) {
                self.available_shells[count] = candidate;
                count += 1;
            }
        }
        if (count == 0) {
            self.available_shells[0] = self.launch.kind;
            count = 1;
        }
        self.available_shell_count = count;
    }
    fn resolveShellLaunch(self: *WorkspaceApplication, kind: shell.ShellKind) !*const shell.ShellLaunch {
        if (kind == self.launch.kind) return &self.launch;
        for (self.shell_launches.items) |cached| {
            if (cached.kind == kind) return cached;
        }
        try self.shell_launches.ensureUnusedCapacity(self.allocator, 1);
        const system_directory = try platform.systemDirectoryAlloc(self.allocator);
        defer self.allocator.free(system_directory);
        const executable_directory = try std.fs.selfExeDirPathAlloc(self.allocator);
        defer self.allocator.free(executable_directory);
        const integration_directory = try std.fmt.allocPrint(self.allocator, "{s}\\ShellIntegration", .{executable_directory});
        defer self.allocator.free(integration_directory);
        var environment = try std.process.getEnvMap(self.allocator);
        defer environment.deinit();

        const default_distribution = if (kind == .wsl) wsl_registry.readDefaultWslDistributionAlloc(self.allocator) catch null else null;
        defer if (default_distribution) |name| self.allocator.free(name.bytes);
        var request: shell.ShellRequest = undefined;
        if (kind == .wsl) {
            request = .{ .wsl = .{
                .distribution = if (default_distribution) |name| .{ .named = name } else .automatic,
                .command = .default,
            } };
        } else {
            const profile = kind.builtinProfile() orelse return &self.launch;
            request = .{ .builtin = profile };
        }
        const owned = try self.allocator.create(shell.ShellLaunch);
        errdefer self.allocator.destroy(owned);
        owned.* = try shell.ShellLaunch.resolve(self.allocator, request, system_directory, integration_directory, &environment);
        self.shell_launches.appendAssumeCapacity(owned);
        return owned;
    }
    fn addSession(self: *WorkspaceApplication, path: directory.DirectoryPath) !*TerminalSession {
        return self.addSessionWithKind(path, self.default_shell_kind);
    }
    fn addSessionWithKind(self: *WorkspaceApplication, path: directory.DirectoryPath, kind: shell.ShellKind) !*TerminalSession {
        const launch_ptr = try self.resolveShellLaunch(kind);
        // Reserving first prevents an allocation failure from stranding a live process.
        try self.sessions.ensureUnusedCapacity(self.allocator, 1);
        const token = self.next_token;
        self.next_token = try std.math.add(u32, token, 1);
        const session = try TerminalSession.create(self.allocator, &self.api, @enumFromInt(token), path, self.machine_name, launch_ptr);
        errdefer session.destroy(self.allocator);
        const t = self.currentTheme();
        try session.terminal.applyPalette(t.foreground, t.background, t.cursor, &t.ansi);
        self.sessions.appendAssumeCapacity(session);
        return session;
    }
    fn removeSession(self: *WorkspaceApplication, id: panes.PaneId) void {
        if (self.reported_mouse_pane == id) {
            self.reported_mouse_pane = null;
            self.reported_mouse_buttons = 0;
        }
        if (self.hovered_link) |hl| {
            if (hl.pane_id == id) self.hovered_link = null;
        }
        if (self.overlay == .path_completion and self.overlay.path_completion.pane_id == id) {
            self.overlay = .none;
        }
        if (self.overlay == .close_confirm and self.overlay.close_confirm.target == .pane and self.overlay.close_confirm.target.pane == id) {
            self.overlay = .none;
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
        const now = std.time.milliTimestamp();
        for (self.placements.items) |placement| {
            const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
            try session.resize(try metrics.terminalSize(placement.bounds));
            _ = session.refreshAppIcon(true);
            session.last_icon_probe_ms = now;
        }
        self.refreshProjectChrome();
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
        const previous = status_presentation.statusPresentationKey(session.terminal.program_status.programStatus());
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
        const status_changed = previous != status_presentation.statusPresentationKey(session.terminal.program_status.programStatus()) or previous_attention != session.needs_attention;
        const chrome_changed = title_changed or status_changed;
        if (!visible) {
            if (status_changed) _ = session.refreshAppIcon(false);
            session.sync_defer_since_ms = null;
            return status_changed;
        }
        if (chrome_changed and model.activeTab().active_pane == id) {
            self.refreshProjectChrome();
        }
        const now_probe = std.time.milliTimestamp();
        if (chrome_changed or now_probe - session.last_icon_probe_ms >= 200) {
            session.last_icon_probe_ms = now_probe;
            _ = session.refreshAppIcon(true);
        }
        const sync_active = session.terminal.synchronizedOutput() catch |err| {
            self.fail(err);
            return false;
        };
        if (sync_active) {
            const now = std.time.milliTimestamp();
            const since = session.sync_defer_since_ms orelse blk: {
                session.sync_defer_since_ms = now;
                break :blk now;
            };
            if (now - since < 100) {
                platform.deferSynchronizedInvalidation();
                return chrome_changed;
            }
            session.sync_defer_since_ms = now;
        } else {
            session.sync_defer_since_ms = null;
        }
        return true;
    }
    pub fn onTick(self: *WorkspaceApplication) void {
        if (self.health == .failed) return;
        const now = std.time.milliTimestamp();
        var any_changed = false;
        for (self.placements.items) |placement| {
            if (self.findSession(placement.id)) |session| {
                if (now - session.last_icon_probe_ms >= 200) {
                    session.last_icon_probe_ms = now;
                    if (session.refreshAppIcon(true)) any_changed = true;
                }
            }
        }
        if (any_changed) platform.invalidateWindow();
    }
    pub fn onPaint(self: *WorkspaceApplication) void {
        if (self.health == .failed) return;
        self.paintWorkspace() catch |err| self.fail(err);
    }
    fn paintWorkspace(self: *WorkspaceApplication) !void {
        const model = if (self.model) |*model| model else return;
        const active_theme = self.currentTheme();
        for (model.tabs.items, 0..) |tab, index| {
            const presentation = try self.tabProgramStatus(tab.tree.root);
            const active_session = self.findSession(tab.active_pane) orelse return error.MissingPaneSession;
            var tab_icons: [8]app_icon.AppIcon = undefined;
            var icon_count: usize = 0;
            try self.collectTabIcons(tab.tree.root, &tab_icons, &icon_count);
            if (icon_count == 0) {
                tab_icons[0] = active_session.icon;
                icon_count = 1;
            }
            const is_editing = if (self.editing_tab) |edit| edit.tab_id == tab.id else false;
            const reported_title = status_presentation.statusTitle(presentation.summary, active_session.displayTitle());
            const default_title = tab.customTitle() orelse reported_title;
            const tab_title = if (self.editing_tab) |*edit| (if (edit.tab_id == tab.id) edit.buf[0..edit.len] else default_title) else default_title;
            platform.drawTab(index, model.tabs.items.len, index == model.selected_tab, is_editing, tab_title, tab_icons[0..icon_count], presentation.summary, self.status_pulse.phaseIndex(), presentation.attention);
        }
        if (self.pointer_drag == .tab) {
            const metrics = self.metrics orelse return;
            if (try self.pointer_drag.tab.markerX(model, try tab_strip.TabStripLayout.init(metrics.width, model.tabs.items.len))) |x| try platform.drawTabInsertionMarker(x);
        }
        const active_tab = model.activeTab();
        const can_focus = active_tab.tree.paneCount() > 1;
        var saw_active_pane = false;
        for (self.placements.items) |placement| {
            if (placement.bounds.width < 14 or placement.bounds.height < 38) continue;
            const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
            const focused = active_tab.active_pane == placement.id and self.overlay == .none and self.editing_tab == null;
            const hovered = self.hovered_pane == placement.id and self.overlay == .none;
            const hovered_action = if (hovered) self.hovered_header_action else null;
            const frame = try session.terminal.readFrame();
            if (active_tab.active_pane == placement.id) {
                saw_active_pane = true;
                const key = if (focused and self.cursor_blink_enabled and self.cursor_window_active and session.state == .running)
                    cursor_blink.CursorKey.fromFrame(placement.id, frame)
                else
                    null;
                self.cursor_blink.synchronize(key, self.cursorNowMs());
            }
            const pane_link: ?platform.HoveredLinkSpan = if (self.overlay == .none) (if (self.hovered_link) |hl| (if (hl.pane_id == placement.id) .{ .row = hl.row, .start_col = hl.start_col, .end_col = hl.end_col } else null) else null) else null;
            {
                const summary = session.terminal.program_status.programStatus();
                platform.beginPane(placement.bounds, active_tab.active_pane == placement.id, hovered, can_focus, active_tab.is_focus_mode, hovered_action, status_presentation.statusTitle(summary, session.displayTitle()), session.icon, summary, self.status_pulse.phaseIndex());
                defer platform.endPane();
                try platform.drawTerminalFrame(frame, focused, self.cursor_blink.isShown(), pane_link, active_theme);
            }
            if (platform.ScrollbarGeometry.compute(placement.bounds, try session.terminal.scrollbar())) |sb| {
                const sb_active = self.hovered_scrollbar == placement.id or (self.pointer_drag == .scrollbar and self.pointer_drag.scrollbar.pane_id == placement.id);
                platform.drawScrollbar(placement.bounds, sb, sb_active);
            }
        }
        if (self.pointer_drag == .pane and self.pointer_drag.pane.dragging and self.pointer_drag.pane.zone != .none) {
            if (self.findPlacement(self.pointer_drag.pane.pane_id)) |placement| {
                if (placement.bounds.splitPreviewRect(self.pointer_drag.pane.zone)) |preview| {
                    platform.drawSplitPreview(preview);
                }
            }
        }
        if (!saw_active_pane) self.cursor_blink.synchronize(null, self.cursorNowMs());
        try self.syncAnimationWake();
        try self.drawActiveOverlay();
    }
    fn drawActiveOverlay(self: *WorkspaceApplication) !void {
        const model = if (self.model) |*model| model else return;
        switch (self.overlay) {
            .none => {},
            .theme_picker => |tp| {
                var cards: [6]platform.c.VexThemeCard = undefined;
                var count: usize = 0;
                for (terminal_theme.themes, 0..) |t, idx| {
                    if (t.is_dark != tp.dark_mode) continue;
                    if (count >= cards.len) break;
                    cards[count] = .{
                        .name = t.name.ptr,
                        .name_length = t.name.len,
                        .subtitle = t.subtitle.ptr,
                        .subtitle_length = t.subtitle.len,
                        .bg_rgb = t.background,
                        .fg_rgb = t.foreground,
                        .accent_rgb = t.accent,
                        .swatches = .{ t.ansi[1], t.ansi[2], t.ansi[3], t.ansi[4], t.ansi[5] },
                        .is_dark = t.is_dark,
                        .active = idx == tp.initial_theme_index,
                        .selected = count == tp.selected_slot,
                        .hovered = if (tp.hovered_slot) |hs| hs == count else false,
                    };
                    count += 1;
                }
                platform.drawThemePicker(tp.dark_mode, cards[0..count]);
            },
            .command_palette => |cp| {
                var matched: [command_defs.len]CommandDef = undefined;
                const count = filterCommands(cp.query_buf[0..cp.query_len], &matched);
                var items: [command_defs.len]platform.c.VexCommandItem = undefined;
                const visible = @min(count, 9);
                for (matched[0..visible], 0..) |cmd, idx| {
                    items[idx] = .{
                        .title = cmd.title.ptr,
                        .title_length = cmd.title.len,
                        .shortcut = if (cmd.shortcut.len == 0) null else cmd.shortcut.ptr,
                        .shortcut_length = cmd.shortcut.len,
                        .selected = idx == cp.selected_index,
                        .hovered = if (cp.hovered_index) |hi| hi == idx else false,
                    };
                }
                platform.drawCommandPalette(cp.query_buf[0..cp.query_len], items[0..visible]);
            },
            .tab_peek => |tpk| {
                var cards: [9]platform.c.VexTabPeekCard = undefined;
                var detail_bufs: [9][192]u8 = undefined;
                const visible = @min(model.tabs.items.len, cards.len);
                for (model.tabs.items[0..visible], 0..) |tab, idx| {
                    const active_session = self.findSession(tab.active_pane) orelse return error.MissingPaneSession;
                    const presentation = try self.tabProgramStatus(tab.tree.root);
                    const title = tab.customTitle() orelse status_presentation.statusTitle(presentation.summary, active_session.displayTitle());
                    const pane_count = tab.tree.paneCount();
                    const origin_title = if (presentation.origin_pane_id) |oid|
                        if (self.findSession(oid)) |s| s.displayTitle() else ""
                    else
                        "";
                    const detail = presentation.summary.formatOriginDetail(
                        &detail_bufs[idx],
                        presentation.origin_pane_number,
                        pane_count,
                        origin_title,
                    );
                    cards[idx] = .{
                        .title = if (title.len == 0) null else title.ptr,
                        .title_length = title.len,
                        .status_detail = if (detail.len == 0) null else detail.ptr,
                        .status_detail_length = detail.len,
                        .status_origin_pane = presentation.origin_pane_number,
                        .index = @intCast(idx),
                        .pane_count = @intCast(pane_count),
                        .icon = platform.nativeAppIcon(active_session.icon),
                        .status = platform.nativeProgramStatus(presentation.summary, self.status_pulse.phaseIndex()),
                        .active = idx == model.selected_tab,
                        .selected = idx == tpk.selected_index,
                        .hovered = if (tpk.hovered_index) |hi| hi == idx else false,
                    };
                }
                platform.drawTabPeek(cards[0..visible]);
            },
            .path_completion => |pc| {
                const popup_bounds = (try self.pathCompletionPopupBounds(pc.pane_id)) orelse return;
                var items: [8]platform.c.VexPathCompletionItem = undefined;
                const total = self.path_completion.matches.items.len;
                const start = @min(self.path_completion.first_visible, total);
                const visible = @min(total - start, items.len);
                for (0..visible) |i| {
                    const m = self.path_completion.matches.items[start + i];
                    const entry = self.path_completion.entries.items[m.entry_index];
                    items[i] = .{
                        .name = if (entry.name.len == 0) null else entry.name.ptr,
                        .name_length = entry.name.len,
                        .is_directory = entry.is_directory,
                        .selected = (start + i) == self.path_completion.selected_index,
                        .hovered = if (self.path_completion.hovered_index) |hi| hi == (start + i) else false,
                    };
                }
                platform.drawPathCompletion(
                    popup_bounds,
                    self.path_completion.directory,
                    self.path_completion.query(),
                    items[0..visible],
                    self.path_completion.scan_error,
                );
            },
            .close_confirm => |*cc| {
                platform.drawCloseConfirm(
                    cc.confirmation.titleSlice(),
                    cc.confirmation.messageSlice(),
                    cc.confirmation.confirmSlice(),
                    cc.confirmation.icon,
                    if (cc.confirmation.reason == .foreground_process) 2 else 0,
                    cc.selected_button,
                    cc.hovered_button,
                );
            },
            .shell_picker => |sp| {
                self.discoverAvailableShells();
                var items: [6]platform.c.VexShellPickerItem = undefined;
                const count: usize = @min(@as(usize, self.available_shell_count), items.len);
                for (self.available_shells[0..count], 0..) |kind, idx| {
                    const name = kind.displayName();
                    const sub = kind.subtitle();
                    items[idx] = .{
                        .name = if (name.len == 0) null else name.ptr,
                        .name_length = name.len,
                        .subtitle = if (sub.len == 0) null else sub.ptr,
                        .subtitle_length = sub.len,
                        .icon = platform.nativeAppIcon(app_icon.AppIcon.shellBrandIcon(@intFromEnum(kind))),
                        .is_default = kind == self.default_shell_kind,
                        .selected = idx == sp.selected_index,
                        .hovered = if (sp.hovered_index) |hi| hi == idx else false,
                    };
                }
                platform.drawShellPicker(@intFromEnum(sp.mode), items[0..count]);
            },
        }
    }
    fn collectTabCloseSnapshots(self: *WorkspaceApplication, node: anytype, out: *[16]close_confirmation.PaneCloseSnapshot, count: *usize) anyerror!void {
        switch (node.*) {
            .leaf => |id| {
                const session = self.findSession(id) orelse return error.MissingPaneSession;
                if (count.* < out.len) {
                    out[count.*] = session.closeSnapshot(true);
                    count.* += 1;
                }
            },
            .split => |split| {
                try self.collectTabCloseSnapshots(split.first, out, count);
                try self.collectTabCloseSnapshots(split.second, out, count);
            },
        }
    }
    fn requestClosePane(self: *WorkspaceApplication, pane_id: panes.PaneId) !void {
        self.commitTabRename();
        const model = if (self.model) |*model| model else return;
        if (model.activeTab().tree.paneCount() <= 1) {
            return self.requestCloseTab(model.selected_tab);
        }
        const session = self.findSession(pane_id) orelse return;
        if (close_confirmation.evaluatePane(session.closeSnapshot(true), .pane)) |confirmation| {
            self.overlay = .{ .close_confirm = .{
                .target = .{ .pane = pane_id },
                .confirmation = confirmation,
                .selected_button = 1,
                .hovered_button = 0,
            } };
            platform.invalidateWindow();
            return;
        }
        if (model.closePaneSplit(pane_id)) |id| {
            self.removeSession(id);
            try self.updateLayout();
        }
    }
    fn requestCloseTab(self: *WorkspaceApplication, index: usize) !void {
        self.commitTabRename();
        const model = if (self.model) |*model| model else return;
        if (index >= model.tabs.items.len or model.tabs.items.len <= 1) return;
        var snapshots: [16]close_confirmation.PaneCloseSnapshot = undefined;
        var count: usize = 0;
        try self.collectTabCloseSnapshots(model.tabs.items[index].tree.root, &snapshots, &count);
        if (close_confirmation.evaluateTab(snapshots[0..count])) |confirmation| {
            self.overlay = .{ .close_confirm = .{
                .target = .{ .tab = model.tabs.items[index].id },
                .confirmation = confirmation,
                .selected_button = 1,
                .hovered_button = 0,
            } };
            platform.invalidateWindow();
            return;
        }
        try self.closeTab(index);
        for (self.sessions.items) |session| {
            if (model.activeTab().tree.containsPane(session.id)) session.needs_attention = false;
        }
        try self.updateLayout();
    }
    fn requestCloseActive(self: *WorkspaceApplication) !void {
        const model = if (self.model) |*model| model else return;
        if (model.activeTab().tree.paneCount() > 1) {
            try self.requestClosePane(model.activeTab().active_pane);
        } else {
            try self.requestCloseTab(model.selected_tab);
        }
    }
    fn confirmCloseOverlay(self: *WorkspaceApplication) !void {
        if (self.overlay != .close_confirm) return;
        const target = self.overlay.close_confirm.target;
        self.overlay = .none;
        const model = if (self.model) |*model| model else return;
        switch (target) {
            .pane => |pane_id| {
                if (model.closePaneSplit(pane_id)) |id| {
                    self.removeSession(id);
                } else {
                    try self.closeTab(model.selected_tab);
                }
            },
            .tab => |tab_id| {
                if (model.tabIndex(tab_id)) |index| try self.closeTab(index);
            },
        }
        for (self.sessions.items) |session| {
            if (model.activeTab().tree.containsPane(session.id)) session.needs_attention = false;
        }
        try self.updateLayout();
    }
    fn pathCompletionPopupBounds(self: *WorkspaceApplication, pane_id: panes.PaneId) !?panes.PaneRect {
        const metrics = self.metrics orelse return null;
        const placement = self.findPlacement(pane_id) orelse return null;
        const session = self.findSession(pane_id) orelse return null;
        const frame = try session.terminal.readFrame();
        const total = self.path_completion.matches.items.len;
        const start = @min(self.path_completion.first_visible, total);
        const visible = @min(total - start, 8);
        const mw = @min(@as(f32, 420.0), @max(@as(f32, 260.0), metrics.width - 24.0));
        const mh = 56.0 + @as(f32, @floatFromInt(@max(visible, 1))) * 26.0 + 8.0;
        const cursor_col: f32 = if (frame.cursor.visible and frame.cursor.column < frame.columns) @floatFromInt(frame.cursor.column) else 0.0;
        const cursor_row: f32 = if (frame.cursor.visible and frame.cursor.row < frame.rows) @floatFromInt(frame.cursor.row) else 0.0;
        const anchor_x = placement.bounds.x + 6.0 + cursor_col * metrics.cell_width;
        const cursor_top = placement.bounds.y + 30.0 + cursor_row * metrics.cell_height;
        const cursor_bottom = cursor_top + metrics.cell_height;
        const mx = @round(std.math.clamp(anchor_x, 8.0, @max(8.0, metrics.width - mw - 8.0)));
        const my_below = cursor_bottom + 4.0;
        const my = @round(if (my_below + mh > metrics.height - 8.0 and cursor_top - mh - 4.0 >= 46.0)
            cursor_top - mh - 4.0
        else
            std.math.clamp(my_below, 46.0, @max(46.0, metrics.height - mh - 8.0)));
        return .{ .x = mx, .y = my, .width = mw, .height = mh };
    }
    fn openPathCompletion(self: *WorkspaceApplication) !void {
        self.commitTabRename();
        const model = if (self.model) |*model| model else return;
        const pane_id = model.activeTab().active_pane;
        const session = self.findSession(pane_id) orelse return;
        if (session.state != .running) return;
        var row_buf: [1024]u8 = undefined;
        const dir = if (session.directory.reported) |rep|
            rep
        else blk: {
            const frame = try session.terminal.readFrame();
            const line = extractCursorLine(frame, &row_buf);
            if (path_completion.readPromptDirectory(line)) |prompt_dir| {
                break :blk prompt_dir;
            }
            const cur = session.directory.current().bytes;
            break :blk if (cur.len > 0) cur else self.project_directory.bytes;
        };
        try self.path_completion.openDirectory(dir, "");
        self.overlay = .{ .path_completion = .{ .pane_id = pane_id } };
        platform.invalidateWindow();
    }
    fn insertSelectedPathCompletion(self: *WorkspaceApplication, pane_id: panes.PaneId, raw: bool) !void {
        const session = self.findSession(pane_id) orelse {
            self.overlay = .none;
            platform.invalidateWindow();
            return;
        };
        const full_path = try self.path_completion.selectedFullPathAlloc(self.allocator);
        defer if (full_path) |fp| self.allocator.free(fp);
        self.overlay = .none;
        platform.invalidateWindow();
        if (full_path) |fp| {
            const quote_kind = path_completion.ShellQuoteKind.fromProgram(session.shell_program);
            const formatted = try path_completion.formatPathAlloc(self.allocator, fp, quote_kind, raw);
            defer self.allocator.free(formatted);
            if (session.state == .running and formatted.len > 0) {
                try session.paste(self.allocator, formatted);
            }
        }
    }
    fn adjustFontZoom(self: *WorkspaceApplication, delta: f32) !void {
        _ = self;
        const current = platform.fontSize();
        const target: f32 = if (delta == 0.0) 14.0 else @round(current + delta);
        _ = try platform.setFontSize(target);
    }
    fn openThemePicker(self: *WorkspaceApplication) void {
        self.commitTabRename();
        const slot_info = themeSlotForIndex(self.theme_index);
        self.overlay = .{ .theme_picker = .{
            .dark_mode = slot_info.dark_mode,
            .selected_slot = slot_info.slot,
            .initial_theme_index = self.theme_index,
        } };
        platform.invalidateWindow();
    }
    fn openCommandPalette(self: *WorkspaceApplication) void {
        self.commitTabRename();
        self.overlay = .{ .command_palette = .{} };
        platform.invalidateWindow();
    }
    fn openTabPeek(self: *WorkspaceApplication) void {
        self.commitTabRename();
        const model = if (self.model) |*model| model else return;
        self.overlay = .{ .tab_peek = .{ .selected_index = model.selected_tab } };
        platform.invalidateWindow();
    }
    fn openShellPicker(self: *WorkspaceApplication, mode: ShellPickerMode) void {
        self.commitTabRename();
        self.discoverAvailableShells();
        var selected: usize = 0;
        const count: usize = self.available_shell_count;
        for (self.available_shells[0..count], 0..) |kind, idx| {
            if (kind == self.default_shell_kind) {
                selected = idx;
                break;
            }
        }
        self.overlay = .{ .shell_picker = .{
            .mode = mode,
            .selected_index = selected,
            .hovered_index = null,
        } };
        platform.invalidateWindow();
    }
    fn executeShellPickerChoice(self: *WorkspaceApplication, mode: ShellPickerMode, kind: shell.ShellKind) !void {
        self.overlay = .none;
        const model = if (self.model) |*model| model else return;
        switch (mode) {
            .set_default => {
                self.default_shell_kind = kind;
                try platform.setShellName(kind.displayName());
                platform.invalidateWindow();
            },
            .new_tab => {
                if (model.tabs.items.len >= workspace.WorkspaceModel.max_tabs) {
                    platform.invalidateWindow();
                    return;
                }
                const session = try self.addSessionWithKind(self.project_directory, kind);
                errdefer self.removeSession(session.id);
                try model.newTab(session.id);
                for (self.sessions.items) |s| {
                    if (model.activeTab().tree.containsPane(s.id)) s.needs_attention = false;
                }
                try self.updateLayout();
            },
            .split_right, .split_down => {
                const active = self.activeSession() orelse return error.MissingPaneSession;
                const session = try self.addSessionWithKind(active.directory.current(), kind);
                errdefer self.removeSession(session.id);
                try model.splitActivePane(session.id, if (mode == .split_right) .right else .down);
                try self.updateLayout();
            },
        }
    }
    fn executeCommand(self: *WorkspaceApplication, id: CommandId) !void {
        self.overlay = .none;
        switch (id) {
            .open_path_completion => try self.openPathCompletion(),
            .open_theme_picker => self.openThemePicker(),
            .toggle_dark_light => try self.toggleDarkLightTheme(),
            .open_tab_peek => self.openTabPeek(),
            .new_tab => try self.applyAction(.new_tab, 0),
            .new_tab_with_shell => self.openShellPicker(.new_tab),
            .split_right => try self.applyAction(.split_right, 0),
            .split_right_with_shell => self.openShellPicker(.split_right),
            .split_down => try self.applyAction(.split_down, 0),
            .split_down_with_shell => self.openShellPicker(.split_down),
            .change_default_shell => self.openShellPicker(.set_default),
            .toggle_focus_mode => {
                if (self.model) |*model| {
                    if (model.toggleFocusMode()) try self.updateLayout() else platform.invalidateWindow();
                }
            },
            .rename_active_tab => {
                if (self.model) |*model| {
                    self.beginTabRename(model.selected_tab);
                }
            },
            .zoom_in => try self.adjustFontZoom(1.0),
            .zoom_out => try self.adjustFontZoom(-1.0),
            .zoom_reset => try self.adjustFontZoom(0.0),
            .next_tab => try self.applyAction(.next_tab, 0),
            .previous_tab => try self.applyAction(.previous_tab, 0),
            .close_active => try self.requestCloseActive(),
            .toggle_cursor_blink => try self.applyAction(.toggle_cursor_blink, 0),
        }
    }
    fn collectTabIcons(self: *WorkspaceApplication, node: anytype, out: *[8]app_icon.AppIcon, count: *usize) anyerror!void {
        switch (node.*) {
            .leaf => |id| {
                const session = self.findSession(id) orelse return error.MissingPaneSession;
                if (session.icon.is_shell) return;
                for (out[0..count.*]) |existing| {
                    if (existing.eql(session.icon)) return;
                }
                if (count.* < out.len) {
                    out[count.*] = session.icon;
                    count.* += 1;
                }
            },
            .split => |split| {
                try self.collectTabIcons(split.first, out, count);
                try self.collectTabIcons(split.second, out, count);
            },
        }
    }
    const TabStatus = struct {
        summary: status.ProgramStatusSummary,
        attention: bool,
        origin_pane_number: u8 = 0,
        origin_pane_id: ?panes.PaneId = null,
        attention_pane_id: ?panes.PaneId = null,
    };
    fn tabProgramStatus(self: *WorkspaceApplication, node: anytype) anyerror!TabStatus {
        var leaf_counter: u8 = 0;
        return self.tabProgramStatusLeafOrder(node, &leaf_counter);
    }
    fn tabProgramStatusLeafOrder(self: *WorkspaceApplication, node: anytype, leaf_counter: *u8) anyerror!TabStatus {
        return switch (node.*) {
            .leaf => |id| blk: {
                leaf_counter.* +|= 1;
                const pane_num = leaf_counter.*;
                const session = self.findSession(id) orelse return error.MissingPaneSession;
                const summary = session.terminal.program_status.programStatus();
                const has_visible_status = summary.state != .none and summary.state != .idle;
                break :blk .{
                    .summary = summary,
                    .attention = session.needs_attention,
                    .origin_pane_number = if (has_visible_status) pane_num else 0,
                    .origin_pane_id = if (has_visible_status) id else null,
                    .attention_pane_id = if (session.needs_attention) id else null,
                };
            },
            .split => |split| blk: {
                const first = try self.tabProgramStatusLeafOrder(split.first, leaf_counter);
                const second = try self.tabProgramStatusLeafOrder(split.second, leaf_counter);
                const pick_second = second.summary.state.displayPriority() > first.summary.state.displayPriority();
                const chosen = if (pick_second) second else first;
                break :blk .{
                    .summary = chosen.summary,
                    .attention = first.attention or second.attention,
                    .origin_pane_number = chosen.origin_pane_number,
                    .origin_pane_id = chosen.origin_pane_id,
                    .attention_pane_id = first.attention_pane_id orelse second.attention_pane_id,
                };
            },
        };
    }
    fn focusTabAttentionOrigin(self: *WorkspaceApplication, tab_index: usize) void {
        const model = if (self.model) |*model| model else return;
        if (tab_index >= model.tabs.items.len) return;
        const presentation = self.tabProgramStatus(model.tabs.items[tab_index].tree.root) catch return;
        const target_pane = presentation.attention_pane_id orelse
            (if (presentation.summary.state == .blocked) presentation.origin_pane_id else null);
        if (target_pane) |pane_id| {
            _ = model.focusPane(pane_id);
        }
    }
    pub fn onKey(self: *WorkspaceApplication, key: u32, modifiers: u32) bool {
        if (self.health == .failed) return true;
        if (isModifierKey(key)) return false;
        self.resetCursorOnInput() catch |err| {
            self.fail(err);
            return true;
        };
        if (self.overlay != .none) {
            return self.handleOverlayKey(key, modifiers) catch |err| {
                self.fail(err);
                return true;
            };
        }
        if (self.editing_tab) |*edit| {
            if (platform.workspaceShortcut(key, modifiers)) |action| {
                self.commitTabRename();
                if (action == .close_active) {
                    self.requestCloseActive() catch |err| self.fail(err);
                } else {
                    self.onAction(action, 0);
                }
                return true;
            }
            if (key == 0x0D) {
                self.commitTabRename();
                return true;
            }
            if (key == 0x1B) {
                self.cancelTabRename();
                return true;
            }
            if (key == 0x08 or key == 0x2E) {
                if (edit.select_all or (modifiers & 2) != 0) {
                    edit.len = 0;
                    edit.select_all = false;
                } else if (edit.len > 0) {
                    var idx: u8 = edit.len - 1;
                    while (idx > 0 and (edit.buf[idx] & 0xC0) == 0x80) : (idx -= 1) {}
                    edit.len = idx;
                }
                platform.invalidateWindow();
                return true;
            }
            if ((modifiers & 6) == 0 and key >= 0x20 and !(key >= 0x21 and key <= 0x28) and !(key >= 0x70 and key <= 0x87)) {
                return false;
            }
            edit.select_all = false;
            platform.invalidateWindow();
            return true;
        }
        if (platform.workspaceShortcut(key, modifiers)) |action| {
            if (action == .close_active) {
                self.requestCloseActive() catch |err| self.fail(err);
            } else {
                self.onAction(action, 0);
            }
            return true;
        }
        const session = self.activeSession() orelse return false;
        return self.handleSessionKey(session, key, modifiers) catch |err| {
            self.fail(err);
            return true;
        };
    }
    fn handleOverlayKey(self: *WorkspaceApplication, key: u32, modifiers: u32) !bool {
        if (platform.workspaceShortcut(key, modifiers)) |action| {
            if (action == .zoom_in or action == .zoom_out or action == .zoom_reset) {
                try self.applyAction(action, 0);
                return true;
            }
            if (action == .theme_picker or action == .command_palette or action == .tab_peek or action == .path_completion) {
                if (self.overlay == .theme_picker) {
                    try self.applyTheme(self.overlay.theme_picker.initial_theme_index);
                }
                const same = (action == .theme_picker and self.overlay == .theme_picker) or
                    (action == .command_palette and self.overlay == .command_palette) or
                    (action == .tab_peek and self.overlay == .tab_peek) or
                    (action == .path_completion and self.overlay == .path_completion);
                self.overlay = .none;
                if (!same) try self.applyAction(action, 0) else platform.invalidateWindow();
                return true;
            }
        }
        switch (self.overlay) {
            .none => return false,
            .theme_picker => |*tp| {
                if (key == 0x1B) {
                    const initial = tp.initial_theme_index;
                    self.overlay = .none;
                    try self.applyTheme(initial);
                    return true;
                }
                if (key == 0x0D) {
                    self.overlay = .none;
                    platform.invalidateWindow();
                    return true;
                }
                if (key == 0x09) {
                    tp.dark_mode = !tp.dark_mode;
                    const fallback = if (tp.dark_mode) self.last_dark_theme else self.last_light_theme;
                    tp.selected_slot = themeSlotForIndex(fallback).slot;
                    try self.applyTheme(themeIndexForModeSlot(tp.dark_mode, tp.selected_slot));
                    return true;
                }
                const prev_slot = tp.selected_slot;
                switch (key) {
                    0x25 => tp.selected_slot = if (tp.selected_slot > 0) tp.selected_slot - 1 else 5,
                    0x27 => tp.selected_slot = (tp.selected_slot + 1) % 6,
                    0x26 => tp.selected_slot = if (tp.selected_slot >= 2) tp.selected_slot - 2 else tp.selected_slot + 4,
                    0x28 => tp.selected_slot = (tp.selected_slot + 2) % 6,
                    else => {},
                }
                if (tp.selected_slot != prev_slot) {
                    try self.applyTheme(themeIndexForModeSlot(tp.dark_mode, tp.selected_slot));
                }
                return true;
            },
            .command_palette => |*cp| {
                if (key == 0x1B) {
                    self.overlay = .none;
                    platform.invalidateWindow();
                    return true;
                }
                var matched: [command_defs.len]CommandDef = undefined;
                const count = @min(filterCommands(cp.query_buf[0..cp.query_len], &matched), 9);
                if (key == 0x0D) {
                    if (count > 0 and cp.selected_index < count) {
                        try self.executeCommand(matched[cp.selected_index].id);
                    } else {
                        self.overlay = .none;
                        platform.invalidateWindow();
                    }
                    return true;
                }
                if (key == 0x26 and count > 0) {
                    cp.selected_index = if (cp.selected_index > 0) cp.selected_index - 1 else count - 1;
                    platform.invalidateWindow();
                    return true;
                }
                if (key == 0x28 and count > 0) {
                    cp.selected_index = (cp.selected_index + 1) % count;
                    platform.invalidateWindow();
                    return true;
                }
                if (key == 0x08 or key == 0x2E) {
                    if ((modifiers & 2) != 0) {
                        cp.query_len = 0;
                    } else if (cp.query_len > 0) {
                        var idx: u8 = cp.query_len - 1;
                        while (idx > 0 and (cp.query_buf[idx] & 0xC0) == 0x80) : (idx -= 1) {}
                        cp.query_len = idx;
                    }
                    cp.selected_index = 0;
                    platform.invalidateWindow();
                    return true;
                }
                if ((modifiers & 6) == 0 and key >= 0x20 and !(key >= 0x21 and key <= 0x28) and !(key >= 0x70 and key <= 0x87)) {
                    return false;
                }
                return true;
            },
            .tab_peek => |*tpk| {
                if (key == 0x1B) {
                    self.overlay = .none;
                    platform.invalidateWindow();
                    return true;
                }
                const model = if (self.model) |*model| model else return true;
                const visible = @min(model.tabs.items.len, 9);
                if (visible == 0) return true;
                if (key == 0x0D) {
                    const target_tab = @min(tpk.selected_index, visible - 1);
                    self.overlay = .none;
                    try self.applyAction(.select_tab, @intCast(target_tab));
                    return true;
                }
                const cols: usize = if (visible <= 2) visible else 3;
                switch (key) {
                    0x25 => tpk.selected_index = if (tpk.selected_index > 0) tpk.selected_index - 1 else visible - 1,
                    0x27 => tpk.selected_index = (tpk.selected_index + 1) % visible,
                    0x26 => tpk.selected_index = if (tpk.selected_index >= cols) tpk.selected_index - cols else tpk.selected_index,
                    0x28 => tpk.selected_index = if (tpk.selected_index + cols < visible) tpk.selected_index + cols else tpk.selected_index,
                    else => {},
                }
                platform.invalidateWindow();
                return true;
            },
            .path_completion => |pc| {
                if (key == 0x1B) {
                    self.overlay = .none;
                    platform.invalidateWindow();
                    return true;
                }
                if (key == 0x0D) {
                    try self.insertSelectedPathCompletion(pc.pane_id, (modifiers & 1) != 0);
                    return true;
                }
                if (key == 0x09 or key == 0x27) {
                    if (try self.path_completion.enterSelectedDirectory()) {
                        platform.invalidateWindow();
                    } else if (key == 0x09) {
                        try self.insertSelectedPathCompletion(pc.pane_id, (modifiers & 1) != 0);
                    }
                    return true;
                }
                if (key == 0x25) {
                    if (try self.path_completion.navigateParent()) {
                        platform.invalidateWindow();
                    }
                    return true;
                }
                if (key == 0x26) {
                    self.path_completion.moveSelection(-1, 8);
                    platform.invalidateWindow();
                    return true;
                }
                if (key == 0x28) {
                    self.path_completion.moveSelection(1, 8);
                    platform.invalidateWindow();
                    return true;
                }
                if (key == 0x21) {
                    self.path_completion.moveSelection(-8, 8);
                    platform.invalidateWindow();
                    return true;
                }
                if (key == 0x22) {
                    self.path_completion.moveSelection(8, 8);
                    platform.invalidateWindow();
                    return true;
                }
                if (key == 0x08 or key == 0x2E) {
                    if ((modifiers & 2) != 0) {
                        try self.path_completion.clearQuery();
                    } else {
                        _ = try self.path_completion.backspaceQuery();
                    }
                    platform.invalidateWindow();
                    return true;
                }
                if ((modifiers & 6) == 0 and key >= 0x20 and !(key >= 0x21 and key <= 0x28) and !(key >= 0x70 and key <= 0x87)) {
                    return false;
                }
                return true;
            },
            .close_confirm => |*cc| {
                if (key == 0x1B or ((modifiers & 6) == 0 and key == 'N')) {
                    self.overlay = .none;
                    platform.invalidateWindow();
                    return true;
                }
                if ((modifiers & 6) == 0 and key == 'Y') {
                    try self.confirmCloseOverlay();
                    return true;
                }
                if (key == 0x0D or key == 0x20) {
                    if (cc.selected_button == 1) {
                        try self.confirmCloseOverlay();
                    } else {
                        self.overlay = .none;
                        platform.invalidateWindow();
                    }
                    return true;
                }
                if (key == 0x09 or key == 0x25 or key == 0x27) {
                    cc.selected_button = if (cc.selected_button == 0) 1 else 0;
                    platform.invalidateWindow();
                }
                return true;
            },
            .shell_picker => |*sp| {
                if (key == 0x1B) {
                    self.overlay = .none;
                    platform.invalidateWindow();
                    return true;
                }
                self.discoverAvailableShells();
                const count: usize = self.available_shell_count;
                if (count == 0) return true;
                if (key == 0x0D) {
                    const idx = @min(sp.selected_index, count - 1);
                    const mode = sp.mode;
                    const kind = self.available_shells[idx];
                    try self.executeShellPickerChoice(mode, kind);
                    return true;
                }
                if (key == 0x09) {
                    const next_mode: u8 = if ((modifiers & 1) != 0)
                        (@intFromEnum(sp.mode) + 3) % 4
                    else
                        (@intFromEnum(sp.mode) + 1) % 4;
                    sp.mode = @enumFromInt(next_mode);
                    platform.invalidateWindow();
                    return true;
                }
                switch (key) {
                    0x25 => sp.mode = @enumFromInt((@intFromEnum(sp.mode) + 3) % 4),
                    0x27 => sp.mode = @enumFromInt((@intFromEnum(sp.mode) + 1) % 4),
                    0x26 => sp.selected_index = if (sp.selected_index > 0) sp.selected_index - 1 else count - 1,
                    0x28 => sp.selected_index = (sp.selected_index + 1) % count,
                    else => {},
                }
                platform.invalidateWindow();
                return true;
            },
        }
    }
    fn handleSessionKey(self: *WorkspaceApplication, session: *TerminalSession, key: u32, modifiers: u32) !bool {
        if (try session.terminal.scrollViewportShortcut(key, modifiers)) {
            platform.invalidateWindow();
            return true;
        }
        if ((modifiers == 3 and key == 'C') or (modifiers == 2 and key == 0x2D)) {
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
        self.resetCursorOnInput() catch |err| {
            self.fail(err);
            return;
        };
        if (self.overlay == .command_palette) {
            if (bytes.len == 1 and (bytes[0] < 0x20 or bytes[0] == 0x7F)) return;
            var cp = &self.overlay.command_palette;
            if (@as(usize, cp.query_len) + bytes.len <= cp.query_buf.len) {
                @memcpy(cp.query_buf[cp.query_len .. cp.query_len + bytes.len], bytes);
                cp.query_len += @intCast(bytes.len);
                cp.selected_index = 0;
                platform.invalidateWindow();
            }
            return;
        }
        if (self.overlay == .path_completion) {
            if (bytes.len == 1 and (bytes[0] < 0x20 or bytes[0] == 0x7F)) return;
            self.path_completion.appendQuery(bytes) catch |err| {
                self.fail(err);
                return;
            };
            platform.invalidateWindow();
            return;
        }
        if (self.overlay != .none) return;
        if (self.editing_tab) |*edit| {
            if (bytes.len == 1 and (bytes[0] < 0x20 or bytes[0] == 0x7F)) return;
            if (edit.select_all) {
                edit.len = 0;
                edit.select_all = false;
            }
            if (@as(usize, edit.len) + bytes.len <= edit.buf.len) {
                @memcpy(edit.buf[edit.len .. edit.len + bytes.len], bytes);
                edit.len += @intCast(bytes.len);
                platform.invalidateWindow();
            }
            return;
        }
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
        self.resetCursorOnInput() catch |err| {
            self.fail(err);
            return;
        };
        if (self.overlay == .command_palette) {
            const trimmed = std.mem.trim(u8, bytes, " \t\r\n");
            if (trimmed.len > 0) {
                var cp = &self.overlay.command_palette;
                const space = cp.query_buf.len - cp.query_len;
                const n = @min(trimmed.len, space);
                @memcpy(cp.query_buf[cp.query_len .. cp.query_len + n], trimmed[0..n]);
                cp.query_len += @intCast(n);
                cp.selected_index = 0;
                platform.invalidateWindow();
            }
            return;
        }
        if (self.overlay == .path_completion) {
            const trimmed = std.mem.trim(u8, bytes, " \t\r\n");
            if (trimmed.len > 0) {
                self.path_completion.appendQuery(trimmed) catch |err| {
                    self.fail(err);
                    return;
                };
                platform.invalidateWindow();
            }
            return;
        }
        if (self.overlay != .none) return;
        if (self.editing_tab) |*edit| {
            const trimmed = std.mem.trim(u8, bytes, " \t\r\n");
            if (trimmed.len > 0) {
                if (edit.select_all) {
                    edit.len = 0;
                    edit.select_all = false;
                }
                const space = edit.buf.len - edit.len;
                const n = @min(trimmed.len, space);
                @memcpy(edit.buf[edit.len .. edit.len + n], trimmed[0..n]);
                edit.len += @intCast(n);
                platform.invalidateWindow();
            }
            return;
        }
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
        if ((modifiers & 2) != 0) {
            self.adjustFontZoom(if (delta < 0) 1.0 else -1.0) catch |err| self.fail(err);
            return;
        }
        if (self.overlay == .path_completion) {
            self.path_completion.moveSelection(if (delta < 0) -1 else 1, 8);
            platform.invalidateWindow();
            return;
        }
        if (self.overlay == .shell_picker) {
            self.discoverAvailableShells();
            const count: usize = self.available_shell_count;
            if (count > 0) {
                var sp = &self.overlay.shell_picker;
                sp.selected_index = if (delta < 0)
                    (if (sp.selected_index > 0) sp.selected_index - 1 else count - 1)
                else
                    (sp.selected_index + 1) % count;
                platform.invalidateWindow();
            }
            return;
        }
        if (self.overlay != .none) return;
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
        if (self.health == .failed or self.overlay != .none) return;
        self.handleMouseButton(button, pressed, x, y, modifiers) catch |err| self.fail(err);
    }
    fn handleMouseButton(self: *WorkspaceApplication, button: terminal.MouseButton, pressed: bool, x: f32, y: f32, modifiers: u32) !void {
        const model = if (self.model) |*model| model else return;
        const metrics = self.metrics orelse return;
        const can_focus = model.activeTab().tree.paneCount() > 1;
        const bit: u8 = if (button == .middle) 2 else if (button == .right) 4 else 1;
        if (pressed) {
            self.commitTabRename();
            for (self.placements.items) |placement| if (placement.bounds.contains(x, y)) {
                if (placement.bounds.hitHeaderAction(can_focus, x, y) != null) return;
                const vp = (try metrics.viewportPoint(placement.bounds, x, y, false)) orelse return;
                const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
                _ = model.focusPane(placement.id);
                try self.clearOtherSelections(placement.id);
                self.refreshProjectChrome();
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
        if (self.overlay != .none) return;
        const model = if (self.model) |*model| model else return;
        const can_focus = model.activeTab().tree.paneCount() > 1;
        for (self.placements.items) |placement| if (placement.bounds.contains(x, y)) {
            if (placement.bounds.hitHeaderAction(can_focus, x, y)) |header_action| {
                switch (header_action) {
                    .focus_toggle => {
                        _ = model.focusPane(placement.id);
                        try self.clearOtherSelections(placement.id);
                        self.refreshProjectChrome();
                        if (model.toggleFocusMode()) try self.updateLayout() else platform.invalidateWindow();
                    },
                    .split_right, .split_down => {
                        const target_session = self.findSession(placement.id) orelse return error.MissingPaneSession;
                        const session = try self.addSession(target_session.directory.current());
                        errdefer self.removeSession(session.id);
                        try model.splitPane(placement.id, session.id, if (header_action == .split_right) .right else .down);
                        try self.updateLayout();
                    },
                    .close => try self.requestClosePane(placement.id),
                }
                return;
            }
            _ = model.focusPane(placement.id);
            try self.clearOtherSelections(placement.id);
            self.refreshProjectChrome();
            platform.invalidateWindow();
            return;
        };
    }
    fn handleOverlayPointer(self: *WorkspaceApplication, event: platform.PointerEvent, x: f32, y: f32) !void {
        const metrics = self.metrics orelse return;
        const model = if (self.model) |*model| model else return;
        switch (self.overlay) {
            .none => {},
            .theme_picker => |*tp| {
                const mw = @min(@as(f32, 560.0), @max(@as(f32, 320.0), metrics.width - 48.0));
                const mh: f32 = 384.0;
                const mx = @round((metrics.width - mw) * 0.5);
                const my = @round(@max(@as(f32, 48.0), (metrics.height - mh) * 0.44));
                const toggle_x = mx + mw - 148.0;
                const toggle_y = my + 15.0;
                const grid_x = mx + 20.0;
                const grid_y = my + 58.0;
                const col_gap: f32 = 12.0;
                const row_gap: f32 = 10.0;
                const card_w = (mw - 40.0 - col_gap) * 0.5;
                const card_h: f32 = 88.0;

                var hit_slot: ?usize = null;
                for (0..6) |i| {
                    const cx = grid_x + @as(f32, @floatFromInt(i % 2)) * (card_w + col_gap);
                    const cy = grid_y + @as(f32, @floatFromInt(i / 2)) * (card_h + row_gap);
                    if (x >= cx and x < cx + card_w and y >= cy and y < cy + card_h) {
                        hit_slot = i;
                        break;
                    }
                }
                if (event == .hover or event == .move) {
                    if (tp.hovered_slot != hit_slot) {
                        tp.hovered_slot = hit_slot;
                        platform.invalidateWindow();
                    }
                    return;
                }
                if (event == .begin) {
                    if (x >= toggle_x and x < toggle_x + 128.0 and y >= toggle_y and y < toggle_y + 26.0) {
                        const want_dark = x < toggle_x + 64.0;
                        if (tp.dark_mode != want_dark) {
                            tp.dark_mode = want_dark;
                            const fallback = if (want_dark) self.last_dark_theme else self.last_light_theme;
                            tp.selected_slot = themeSlotForIndex(fallback).slot;
                            try self.applyTheme(themeIndexForModeSlot(tp.dark_mode, tp.selected_slot));
                        }
                        return;
                    }
                    if (hit_slot) |slot| {
                        tp.selected_slot = slot;
                        try self.applyTheme(themeIndexForModeSlot(tp.dark_mode, slot));
                        self.overlay = .none;
                        platform.invalidateWindow();
                        return;
                    }
                    if (x < mx or x >= mx + mw or y < my or y >= my + mh) {
                        const initial = tp.initial_theme_index;
                        self.overlay = .none;
                        try self.applyTheme(initial);
                    }
                }
            },
            .command_palette => |*cp| {
                var matched: [command_defs.len]CommandDef = undefined;
                const count = @min(filterCommands(cp.query_buf[0..cp.query_len], &matched), 9);
                const mw = @min(@as(f32, 520.0), @max(@as(f32, 300.0), metrics.width - 48.0));
                const mh = 58.0 + @as(f32, @floatFromInt(@max(count, 1))) * 34.0 + 12.0;
                const mx = @round((metrics.width - mw) * 0.5);
                const my: f32 = 58.0;
                const list_y = my + 52.0;

                var hit_idx: ?usize = null;
                if (x >= mx + 10.0 and x < mx + mw - 10.0 and y >= list_y) {
                    const row = @floor((y - list_y) / 34.0);
                    if (row >= 0 and row < @as(f32, @floatFromInt(count))) {
                        hit_idx = @intFromFloat(row);
                    }
                }
                if (event == .hover or event == .move) {
                    if (cp.hovered_index != hit_idx) {
                        cp.hovered_index = hit_idx;
                        if (hit_idx) |hi| cp.selected_index = hi;
                        platform.invalidateWindow();
                    }
                    return;
                }
                if (event == .begin) {
                    if (hit_idx) |hi| {
                        try self.executeCommand(matched[hi].id);
                        return;
                    }
                    if (x < mx or x >= mx + mw or y < my or y >= my + mh) {
                        self.overlay = .none;
                        platform.invalidateWindow();
                    }
                }
            },
            .tab_peek => |*tpk| {
                const visible = @min(model.tabs.items.len, 9);
                if (visible == 0) return;
                const cols: usize = if (visible <= 2) visible else 3;
                const rows: usize = (visible + cols - 1) / cols;
                const mw = @min(@as(f32, 620.0), @max(@as(f32, 340.0), metrics.width - 48.0));
                const card_h: f32 = 86.0;
                const col_gap: f32 = 12.0;
                const row_gap: f32 = 12.0;
                const mh = 62.0 + @as(f32, @floatFromInt(rows)) * card_h + @as(f32, @floatFromInt(if (rows > 0) rows - 1 else 0)) * row_gap + 24.0;
                const mx = @round((metrics.width - mw) * 0.5);
                const my = @round(@max(@as(f32, 52.0), (metrics.height - mh) * 0.42));
                const grid_x = mx + 20.0;
                const grid_y = my + 50.0;
                const card_w = (mw - 40.0 - @as(f32, @floatFromInt(cols - 1)) * col_gap) / @as(f32, @floatFromInt(cols));

                var hit_idx: ?usize = null;
                for (0..visible) |i| {
                    const cx = grid_x + @as(f32, @floatFromInt(i % cols)) * (card_w + col_gap);
                    const cy = grid_y + @as(f32, @floatFromInt(i / cols)) * (card_h + row_gap);
                    if (x >= cx and x < cx + card_w and y >= cy and y < cy + card_h) {
                        hit_idx = i;
                        break;
                    }
                }
                if (event == .hover or event == .move) {
                    if (tpk.hovered_index != hit_idx) {
                        tpk.hovered_index = hit_idx;
                        if (hit_idx) |hi| tpk.selected_index = hi;
                        platform.invalidateWindow();
                    }
                    return;
                }
                if (event == .begin) {
                    if (hit_idx) |hi| {
                        self.overlay = .none;
                        try self.applyAction(.select_tab, @intCast(hi));
                        return;
                    }
                    if (x < mx or x >= mx + mw or y < my or y >= my + mh) {
                        self.overlay = .none;
                        platform.invalidateWindow();
                    }
                }
            },
            .path_completion => |pc| {
                const popup = (try self.pathCompletionPopupBounds(pc.pane_id)) orelse return;
                const total = self.path_completion.matches.items.len;
                const start = @min(self.path_completion.first_visible, total);
                const visible = @min(total - start, 8);
                const list_y = popup.y + 56.0;
                var hit_idx: ?usize = null;
                if (x >= popup.x + 6.0 and x < popup.x + popup.width - 6.0 and y >= list_y) {
                    const row = @floor((y - list_y) / 26.0);
                    if (row >= 0 and row < @as(f32, @floatFromInt(visible))) {
                        hit_idx = start + @as(usize, @intFromFloat(row));
                    }
                }
                if (event == .hover or event == .move) {
                    if (self.path_completion.hovered_index != hit_idx) {
                        self.path_completion.hovered_index = hit_idx;
                        if (hit_idx) |hi| self.path_completion.selected_index = hi;
                        platform.invalidateWindow();
                    }
                    return;
                }
                if (event == .begin) {
                    if (hit_idx) |hi| {
                        self.path_completion.selected_index = hi;
                        try self.insertSelectedPathCompletion(pc.pane_id, false);
                        return;
                    }
                    if (!popup.contains(x, y)) {
                        self.overlay = .none;
                        platform.invalidateWindow();
                    }
                }
            },
            .close_confirm => |*cc| {
                const mw = @min(@as(f32, 440.0), @max(@as(f32, 300.0), metrics.width - 48.0));
                const mh: f32 = 186.0;
                const mx = @round((metrics.width - mw) * 0.5);
                const my = @round(@max(@as(f32, 56.0), (metrics.height - mh) * 0.44));
                const btn_y = my + mh - 46.0;
                const btn_h: f32 = 32.0;
                const confirm_w: f32 = 128.0;
                const confirm_x = mx + mw - 20.0 - confirm_w;
                const cancel_w: f32 = 88.0;
                const cancel_x = confirm_x - 10.0 - cancel_w;

                var hit_btn: u8 = 0;
                if (y >= btn_y and y < btn_y + btn_h) {
                    if (x >= cancel_x and x < cancel_x + cancel_w) {
                        hit_btn = 1;
                    } else if (x >= confirm_x and x < confirm_x + confirm_w) {
                        hit_btn = 2;
                    }
                }
                if (event == .hover or event == .move) {
                    if (cc.hovered_button != hit_btn) {
                        cc.hovered_button = hit_btn;
                        platform.invalidateWindow();
                    }
                    return;
                }
                if (event == .begin) {
                    if (hit_btn == 1) {
                        self.overlay = .none;
                        platform.invalidateWindow();
                        return;
                    }
                    if (hit_btn == 2) {
                        try self.confirmCloseOverlay();
                        return;
                    }
                    if (x < mx or x >= mx + mw or y < my or y >= my + mh) {
                        self.overlay = .none;
                        platform.invalidateWindow();
                    }
                }
            },
            .shell_picker => |*sp| {
                self.discoverAvailableShells();
                const count: usize = @min(@as(usize, self.available_shell_count), 6);
                const mw = @min(@as(f32, 460.0), @max(@as(f32, 300.0), metrics.width - 48.0));
                const row_h: f32 = 42.0;
                const mh = 76.0 + @as(f32, @floatFromInt(@max(count, 1))) * row_h + 14.0;
                const mx = @round((metrics.width - mw) * 0.5);
                const my: f32 = 52.0;
                const pill_gap: f32 = 6.0;
                const pill_w = (mw - 32.0 - pill_gap * 3.0) * 0.25;
                const pill_y = my + 38.0;
                const pill_h: f32 = 24.0;
                const list_y = my + 70.0;

                var hit_idx: ?usize = null;
                if (x >= mx + 12.0 and x < mx + mw - 12.0 and y >= list_y) {
                    const row = @floor((y - list_y) / row_h);
                    if (row >= 0 and row < @as(f32, @floatFromInt(count))) {
                        hit_idx = @intFromFloat(row);
                    }
                }
                if (event == .hover or event == .move) {
                    if (sp.hovered_index != hit_idx) {
                        sp.hovered_index = hit_idx;
                        if (hit_idx) |hi| sp.selected_index = hi;
                        platform.invalidateWindow();
                    }
                    return;
                }
                if (event == .begin) {
                    if (y >= pill_y and y < pill_y + pill_h) {
                        for (0..4) |m| {
                            const px = mx + 16.0 + @as(f32, @floatFromInt(m)) * (pill_w + pill_gap);
                            if (x >= px and x < px + pill_w) {
                                sp.mode = @enumFromInt(@as(u8, @intCast(m)));
                                platform.invalidateWindow();
                                return;
                            }
                        }
                    }
                    if (hit_idx) |hi| {
                        const mode = sp.mode;
                        const kind = self.available_shells[hi];
                        try self.executeShellPickerChoice(mode, kind);
                        return;
                    }
                    if (x < mx or x >= mx + mw or y < my or y >= my + mh) {
                        self.overlay = .none;
                        platform.invalidateWindow();
                    }
                }
            },
        }
    }
    fn updateHoverState(self: *WorkspaceApplication, can_focus: bool, x: f32, y: f32, modifiers: u32) !void {
        var next_pane: ?panes.PaneId = null;
        var next_action: ?panes.PaneHeaderAction = null;
        var next_scrollbar: ?panes.PaneId = null;
        var next_link: ?HoveredLink = null;
        if (self.pointer_drag == .none) {
            for (self.placements.items) |placement| if (placement.bounds.contains(x, y)) {
                next_pane = placement.id;
                next_action = placement.bounds.hitHeaderAction(can_focus, x, y);
                if (next_action == null) {
                    if (self.findSession(placement.id)) |session| {
                        if ((modifiers & 1) != 0 or !try session.terminal.isMouseTracking()) {
                            if (platform.ScrollbarGeometry.compute(placement.bounds, try session.terminal.scrollbar())) |sb| {
                                if (sb.containsPoint(placement.bounds, x, y)) next_scrollbar = placement.id;
                            }
                        }
                        if (next_scrollbar == null) {
                            if (self.metrics) |metrics| {
                                if (try metrics.viewportPoint(placement.bounds, x, y, false)) |vp| {
                                    const frame = try session.terminal.readFrame();
                                    if (url_detector.findLinkInFrameRow(frame, vp.row, vp.column, null)) |match| {
                                        next_link = .{
                                            .pane_id = placement.id,
                                            .row = vp.row,
                                            .start_col = match.start_col,
                                            .end_col = match.end_col,
                                        };
                                    }
                                }
                            }
                        }
                    }
                }
                break;
            };
        }
        const link_changed = !std.meta.eql(self.hovered_link, next_link);
        if (self.hovered_pane != next_pane or self.hovered_header_action != next_action or self.hovered_scrollbar != next_scrollbar or link_changed) {
            self.hovered_pane = next_pane;
            self.hovered_header_action = next_action;
            self.hovered_scrollbar = next_scrollbar;
            self.hovered_link = next_link;
            platform.invalidateWindow();
        }
    }
    fn cancelPointerDrag(self: *WorkspaceApplication) !bool {
        const model = if (self.model) |*model| model else return false;
        const clear_preview = self.pointer_drag == .tab or self.pointer_drag == .pane or self.pointer_drag == .selection or self.pointer_drag == .scrollbar;
        const changed = switch (self.pointer_drag) {
            .none, .tab, .pane, .mouse, .scrollbar => false,
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
        if (event == .begin) self.resetCursorOnInput() catch |err| {
            self.fail(err);
            return .normal;
        };
        return self.handlePointer(event, x, y, modifiers) catch |err| {
            self.fail(err);
            return .normal;
        };
    }
    fn handlePointer(self: *WorkspaceApplication, event: platform.PointerEvent, x: f32, y: f32, modifiers: u32) !platform.PointerResponse {
        if (self.overlay != .none) {
            try self.handleOverlayPointer(event, x, y);
            return .normal;
        }
        const model = if (self.model) |*model| model else return .normal;
        const metrics = self.metrics orelse return .normal;
        const layout = try tab_strip.TabStripLayout.init(metrics.width, model.tabs.items.len);
        const point: tab_strip.TabPointer = .{ .x = x, .y = y };
        const bounds = workspaceBounds(metrics);
        const can_focus = model.activeTab().tree.paneCount() > 1;
        switch (event) {
            .hover => {
                try self.updateHoverState(can_focus, x, y, modifiers);
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
                    if (hit.part == .close) {
                        self.commitTabRename();
                        return .normal;
                    }
                    if (self.editing_tab) |edit| {
                        if (edit.tab_id != model.tabs.items[hit.index].id) {
                            self.commitTabRename();
                        }
                    }
                    try self.applyAction(.select_tab, @intCast(hit.index));
                    var drag: TabDrag = .none;
                    if (try drag.begin(model, layout, model.activeTab().id, point)) self.pointer_drag = .{ .tab = drag };
                } else {
                    self.commitTabRename();
                    if (!model.activeTab().is_focus_mode) {
                        var drag: DividerDrag = .{};
                        if (try drag.begin(&model.activeTab().tree, bounds, x, y)) self.pointer_drag = .{ .divider = drag };
                    }
                    if (self.pointer_drag == .none) {
                        for (self.placements.items) |placement| if (placement.bounds.contains(x, y)) {
                            if (placement.bounds.hitHeaderAction(can_focus, x, y) != null) break;
                            if (placement.bounds.isHeaderDragArea(can_focus, x, y)) {
                                _ = model.focusPane(placement.id);
                                try self.clearOtherSelections(placement.id);
                                self.refreshProjectChrome();
                                self.pointer_drag = .{ .pane = .{
                                    .pane_id = placement.id,
                                    .start_x = x,
                                    .start_y = y,
                                } };
                                platform.invalidateWindow();
                                break;
                            }
                            const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
                            if ((modifiers & 1) != 0 or !try session.terminal.isMouseTracking()) {
                                if (platform.ScrollbarGeometry.compute(placement.bounds, try session.terminal.scrollbar())) |sb| {
                                    if (sb.containsPoint(placement.bounds, x, y)) {
                                        _ = model.focusPane(placement.id);
                                        try self.clearOtherSelections(placement.id);
                                        self.refreshProjectChrome();
                                        if (sb.containsThumb(y)) {
                                            self.pointer_drag = .{ .scrollbar = .{
                                                .pane_id = placement.id,
                                                .grab_offset = y - sb.thumb_y,
                                            } };
                                        } else {
                                            const page_rows: i32 = @intCast(if (session.size) |sz| sz.rows else 24);
                                            const delta: i32 = if (y < sb.thumb_y) -page_rows else page_rows;
                                            session.terminal.scroll(delta);
                                        }
                                        platform.invalidateWindow();
                                        break;
                                    }
                                }
                            }
                            if (try metrics.viewportPoint(placement.bounds, x, y, false)) |vp| {
                                if ((modifiers & 2) != 0) {
                                    const frame = try session.terminal.readFrame();
                                    var url_buf: [2048]u8 = undefined;
                                    if (url_detector.findLinkInFrameRow(frame, vp.row, vp.column, &url_buf)) |match| {
                                        if (match.url_len > 0) {
                                            _ = model.focusPane(placement.id);
                                            try self.clearOtherSelections(placement.id);
                                            self.refreshProjectChrome();
                                            platform.openUrl(url_buf[0..match.url_len]) catch {};
                                            platform.invalidateWindow();
                                            break;
                                        }
                                    }
                                }
                                _ = model.focusPane(placement.id);
                                try self.clearOtherSelections(placement.id);
                                self.refreshProjectChrome();
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
                    .none => try self.updateHoverState(can_focus, x, y, modifiers),
                    .divider => |*drag| if (try drag.move(&model.activeTab().tree, bounds, x, y)) try self.updateLayout(),
                    .tab => |*drag| {
                        if (event == .end) {
                            _ = try drag.finish(model, layout, point);
                            platform.invalidateWindow();
                        } else if (try drag.move(model, layout, point)) platform.invalidateWindow();
                    },
                    .pane => |*pdrag| {
                        if (!pdrag.dragging and (@abs(x - pdrag.start_x) + @abs(y - pdrag.start_y)) >= 8.0) {
                            pdrag.dragging = true;
                        }
                        if (pdrag.dragging) {
                            const next_zone: panes.DropZone = if (self.findPlacement(pdrag.pane_id)) |pl| pl.bounds.dropZoneAt(x, y) else .none;
                            if (event == .end) {
                                const zone = next_zone;
                                pdrag.zone = .none;
                                if (zone != .none) {
                                    const target_session = self.findSession(pdrag.pane_id) orelse return error.MissingPaneSession;
                                    const session = try self.addSession(target_session.directory.current());
                                    errdefer self.removeSession(session.id);
                                    const axis: panes.SplitAxis = if (zone == .left or zone == .right) .right else .down;
                                    try model.splitPane(pdrag.pane_id, session.id, axis);
                                    try self.updateLayout();
                                } else {
                                    platform.invalidateWindow();
                                }
                            } else if (pdrag.zone != next_zone) {
                                pdrag.zone = next_zone;
                                platform.invalidateWindow();
                            }
                        }
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
                    .scrollbar => |sb_drag| {
                        if (self.findPlacement(sb_drag.pane_id)) |placement| {
                            if (self.findSession(sb_drag.pane_id)) |session| {
                                if (platform.ScrollbarGeometry.compute(placement.bounds, try session.terminal.scrollbar())) |sb| {
                                    try session.terminal.scrollToRow(sb.targetOffsetForY(y, sb_drag.grab_offset));
                                    platform.invalidateWindow();
                                }
                            }
                        }
                    },
                }
                if (event == .end) {
                    self.pointer_drag = .none;
                    try self.updateHoverState(model.activeTab().tree.paneCount() > 1, x, y, modifiers);
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
        if (self.pointer_drag == .pane) return if (self.pointer_drag.pane.dragging) .tab_dragging else .tab_armed;
        if (self.pointer_drag == .scrollbar) return .tab_dragging;
        if (self.pointer_drag == .selection or self.pointer_drag == .mouse) return .selection;
        if (event == .hover and self.pointer_drag == .none and self.hovered_link != null) return .link;
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
        switch (action) {
            .toggle_cursor_blink => {
                self.cursor_blink_enabled = !self.cursor_blink_enabled;
                self.cursor_blink.synchronize(null, self.cursorNowMs());
                try self.syncAnimationWake();
                platform.invalidateWindow();
                return;
            },
            .command_palette => {
                if (index > 0) {
                    self.overlay = .none;
                    platform.invalidateWindow();
                    return;
                }
                self.openCommandPalette();
                return;
            },
            .theme_picker => {
                if (index == 99) {
                    try self.toggleDarkLightTheme();
                    return;
                }
                if (index > 0 and index <= terminal_theme.themes.len) {
                    try self.applyTheme(index - 1);
                    return;
                }
                self.openThemePicker();
                return;
            },
            .tab_peek => {
                if (index > 0) {
                    self.overlay = .none;
                    platform.invalidateWindow();
                    return;
                }
                self.openTabPeek();
                return;
            },
            .path_completion => {
                if (index == 99) {
                    self.overlay = .none;
                    platform.invalidateWindow();
                    return;
                }
                if (index == 98) {
                    if (self.overlay == .path_completion) {
                        try self.insertSelectedPathCompletion(self.overlay.path_completion.pane_id, false);
                    }
                    return;
                }
                try self.openPathCompletion();
                return;
            },
            .zoom_in => {
                try self.adjustFontZoom(1.0);
                return;
            },
            .zoom_out => {
                try self.adjustFontZoom(-1.0);
                return;
            },
            .zoom_reset => {
                try self.adjustFontZoom(0.0);
                return;
            },
            .rename_tab => {
                self.beginTabRename(index);
                return;
            },
            .request_close_tab => {
                if (index == 99) {
                    try self.requestCloseActive();
                    return;
                }
                if (index == 98) {
                    if (self.overlay == .close_confirm) {
                        self.overlay = .none;
                        platform.invalidateWindow();
                    }
                    return;
                }
                if (index == 97) {
                    try self.confirmCloseOverlay();
                    return;
                }
                try self.requestCloseTab(index);
                return;
            },
            .shell_picker => {
                if (index == 0) {
                    if (self.overlay == .shell_picker) {
                        self.overlay = .none;
                        platform.invalidateWindow();
                    } else {
                        self.openShellPicker(.new_tab);
                    }
                    return;
                }
                if (index == 99) {
                    if (self.overlay == .shell_picker) {
                        self.overlay = .none;
                        platform.invalidateWindow();
                    }
                    return;
                }
                if (index >= 1 and index <= 7) {
                    try self.executeShellPickerChoice(.new_tab, @enumFromInt(index));
                    return;
                }
                if (index >= 11 and index <= 17) {
                    try self.executeShellPickerChoice(.split_right, @enumFromInt(index - 10));
                    return;
                }
                if (index >= 21 and index <= 27) {
                    try self.executeShellPickerChoice(.split_down, @enumFromInt(index - 20));
                    return;
                }
                if (index >= 31 and index <= 37) {
                    try self.executeShellPickerChoice(.set_default, @enumFromInt(index - 30));
                    return;
                }
                return;
            },
            else => {},
        }
        const model = if (self.model) |*model| model else return;
        if (action != .select_tab) {
            self.commitTabRename();
        } else if (self.editing_tab) |edit| {
            if (index >= model.tabs.items.len or edit.tab_id != model.tabs.items[index].id) {
                self.commitTabRename();
            }
        }
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
            .next_tab => {
                model.cycleTab(true);
                self.focusTabAttentionOrigin(model.selected_tab);
            },
            .previous_tab => {
                model.cycleTab(false);
                self.focusTabAttentionOrigin(model.selected_tab);
            },
            .select_tab => {
                if (!model.selectTab(index)) return;
                self.focusTabAttentionOrigin(model.selected_tab);
            },
            .close_active => {
                if (model.closeActiveSplit()) |id| self.removeSession(id) else try self.closeTab(model.selected_tab);
            },
            .close_tab => try self.closeTab(index),
            .rename_tab, .command_palette, .theme_picker, .tab_peek, .path_completion, .zoom_in, .zoom_out, .zoom_reset, .request_close_tab, .toggle_cursor_blink, .shell_picker => unreachable,
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
        self.handleProcessExit(id, code) catch |err| self.fail(err);
    }
    fn handleProcessExit(self: *WorkspaceApplication, id: panes.PaneId, code: u32) !void {
        const session = self.findSession(id) orelse return;
        if (session.state != .running) return;
        const model = if (self.model) |*model| model else return error.MissingPaneSession;
        const tab_id = owner: {
            for (model.tabs.items) |tab| {
                if (tab.tree.containsPane(id)) break :owner tab.id;
            }
            return error.MissingPaneSession;
        };
        session.state = .{ .exited = code };
        session.terminal.program_status.onProgramExited();
        _ = try self.cancelPointerDrag();
        if (self.overlay == .close_confirm) {
            const targets_exit = switch (self.overlay.close_confirm.target) {
                .pane => |pane_id| pane_id == id,
                .tab => |target_tab| target_tab == tab_id,
            };
            if (targets_exit) self.overlay = .none;
        }
        switch (model.closeExitedPane(id)) {
            .stale => return error.MissingPaneSession,
            .retained_final => {
                var banner_buf: [96]u8 = undefined;
                const banner = try std.fmt.bufPrint(&banner_buf, "\r\n\x1b[2m[process exited with code {d}]\x1b[m\r\n", .{code});
                try session.terminal.feedOutput(banner);
                _ = session.refreshAppIcon(false);
                platform.invalidateWindow();
            },
            .pane_closed, .tab_closed => |action| {
                if (action == .tab_closed) {
                    if (self.editing_tab) |edit| {
                        if (edit.tab_id == tab_id) self.cancelTabRename();
                    }
                    if (self.overlay == .tab_peek) {
                        self.overlay.tab_peek.selected_index = @min(self.overlay.tab_peek.selected_index, model.tabs.items.len - 1);
                        self.overlay.tab_peek.hovered_index = null;
                    }
                }
                self.removeSession(id);
                for (self.sessions.items) |remaining| {
                    if (model.activeTab().tree.containsPane(remaining.id)) remaining.needs_attention = false;
                }
                try self.updateLayout();
            },
        }
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
            34 => return @intFromEnum(self.default_shell_kind),
            38 => return if (self.activeSession()) |session| @intFromEnum(session.icon.glyph) else 0,
            72 => return @intFromBool(self.cursor_blink_enabled),
            73 => return @intFromBool(self.cursor_blink.isShown()),
            74 => return @intFromBool(self.cursor_blink.state == .blinking),
            75 => return @intFromBool(platform.cursorWakeArmed()),
            76 => return @intFromBool(self.cursor_window_active),
            77 => return if (self.activeSession()) |session| @intFromBool(session.terminal.frame.cursor.blinking) else 0,
            78 => return if (self.activeSession()) |session| @intFromBool(session.terminal.frame.cursor.visible) else 0,
            79 => return if (self.activeSession()) |session| session.terminal.frame.generation else 0,
            80 => return if (self.activeSession()) |session| @intFromEnum(session.terminal.frame.cursor.shape) else 0,
            81 => return if (self.activeSession()) |session| session.terminal.frame.cursor.column else 0,
            82 => return if (self.activeSession()) |session| session.terminal.frame.cursor.row else 0,
            83 => return if (self.metrics) |metrics| @intFromFloat(@round(metrics.cell_width * 1000)) else 0,
            84 => return if (self.metrics) |metrics| @intFromFloat(@round(metrics.cell_height * 1000)) else 0,
            90 => return self.status_pulse.phaseIndex(),
            91 => return @intFromBool(self.status_pulse.state == .pulsing),
            92 => return if (self.activeSession()) |session| @intFromEnum(session.icon.kind) else 0,
            93 => return if (self.activeSession()) |session| if (session.icon.badge_len > 0) session.icon.badge_text[0] else 0 else 0,
            94 => return if (self.activeSession()) |session| session.active_process_title_len else 0,
            95 => return probeBytes(if (self.activeSession()) |session| session.active_process_title[0..session.active_process_title_len] else "", 0),
            96 => return probeBytes(if (self.activeSession()) |session| session.active_process_title[0..session.active_process_title_len] else "", 8),
            85 => return if (self.activeSession()) |session| @intFromEnum(session.shell_kind) else 0,
            86 => return (self.tabProgramStatus(model.activeTab().tree.root) catch return 0).origin_pane_number,
            87 => return if ((self.tabProgramStatus(model.activeTab().tree.root) catch return 0).origin_pane_id) |id| @intFromEnum(id) else 0,
            88 => {
                self.discoverAvailableShells();
                return self.available_shell_count;
            },
            89 => return if (self.overlay == .shell_picker) @intFromEnum(self.overlay.shell_picker.mode) else 0,
            70 => return if (self.activeSession()) |session| @intFromBool(session.state == .exited) else 0,
            71 => return if (self.activeSession()) |session| switch (session.state) {
                .running => 0,
                .exited => |code| code,
            } else 0,
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
            51 => return self.theme_index,
            52 => return @intFromBool(self.currentTheme().is_dark),
            53 => return switch (self.overlay) {
                .none => 0,
                .command_palette => 1,
                .theme_picker => 2,
                .tab_peek => 3,
                .path_completion => 4,
                .close_confirm => 5,
                .shell_picker => 6,
            },
            54 => return @intFromFloat(@round(platform.fontSize())),
            55 => return self.path_completion.matches.items.len,
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

/// Packs up to eight ASCII bytes of a diagnostic probe string without allocating.
fn probeBytes(text: []const u8, offset: usize) u64 {
    var word: u64 = 0;
    if (offset >= text.len) return 0;
    const slice = text[offset..@min(text.len, offset + 8)];
    for (slice, 0..) |byte, index| {
        word |= @as(u64, byte) << @intCast(index * 8);
    }
    return word;
}

fn extractCursorLine(frame: *const terminal.TerminalFrame, buf: []u8) []const u8 {    if (!frame.cursor.visible or frame.cursor.row >= frame.rows or frame.columns == 0) return &.{};
    const row_offset = @as(usize, frame.cursor.row) * @as(usize, frame.columns);
    if (row_offset + frame.columns > frame.cells.items.len) return &.{};
    const cells = frame.cells.items[row_offset .. row_offset + frame.columns];
    var len: usize = 0;
    for (cells) |cell| {
        if (cell.width == .tail) continue;
        const text = frame.cellText(cell);
        if (text.len == 0) {
            if (len >= buf.len) break;
            buf[len] = ' ';
            len += 1;
        } else {
            if (len + text.len > buf.len) break;
            @memcpy(buf[len .. len + text.len], text);
            len += text.len;
        }
    }
    return std.mem.trimRight(u8, buf[0..len], " ");
}

fn themeIndexForModeSlot(dark_mode: bool, target_slot: usize) usize {
    var slot: usize = 0;
    for (terminal_theme.themes, 0..) |t, idx| {
        if (t.is_dark != dark_mode) continue;
        if (slot == target_slot) return idx;
        slot += 1;
    }
    return if (dark_mode) terminal_theme.default_dark_index else terminal_theme.default_light_index;
}

fn themeSlotForIndex(theme_index: usize) struct { dark_mode: bool, slot: usize } {
    const clamped = @min(theme_index, terminal_theme.themes.len - 1);
    const dark_mode = terminal_theme.themes[clamped].is_dark;
    var slot: usize = 0;
    for (terminal_theme.themes[0..clamped]) |t| {
        if (t.is_dark == dark_mode) slot += 1;
    }
    return .{ .dark_mode = dark_mode, .slot = slot };
}

fn filterCommands(query: []const u8, out: *[command_defs.len]CommandDef) usize {
    const trimmed = std.mem.trim(u8, query, " \t");
    if (trimmed.len == 0) {
        @memcpy(out[0..command_defs.len], &command_defs);
        return command_defs.len;
    }
    var tokens: [8][]const u8 = undefined;
    var token_count: usize = 0;
    var it = std.mem.tokenizeScalar(u8, trimmed, ' ');
    while (it.next()) |token| {
        if (token_count < tokens.len) {
            tokens[token_count] = token;
            token_count += 1;
        }
    }
    if (token_count == 0) {
        @memcpy(out[0..command_defs.len], &command_defs);
        return command_defs.len;
    }

    var count: usize = 0;
    for (command_defs) |cmd| {
        var matches_all = true;
        for (tokens[0..token_count]) |tok| {
            if (!containsAsciiIgnoreCase(cmd.title, tok) and !containsAsciiIgnoreCase(cmd.shortcut, tok)) {
                matches_all = false;
                break;
            }
        }
        if (matches_all) {
            out[count] = cmd;
            count += 1;
        }
    }
    return count;
}

fn containsAsciiIgnoreCase(haystack: []const u8, needle: []const u8) bool {
    if (needle.len == 0) return true;
    if (needle.len > haystack.len) return false;
    var i: usize = 0;
    while (i + needle.len <= haystack.len) : (i += 1) {
        if (std.ascii.eqlIgnoreCase(haystack[i .. i + needle.len], needle)) return true;
    }
    return false;
}

fn workspaceBounds(metrics: platform.WindowMetrics) panes.PaneRect {
    return .{ .x = 0, .y = 44, .width = @max(0, metrics.width), .height = @max(0, metrics.height - 44) };
}

fn isModifierKey(key: u32) bool {
    return switch (key) {
        0x10, 0x11, 0x12, 0x14, 0x5B, 0x5C, 0x90, 0xA0...0xA5 => true,
        else => false,
    };
}

fn projectDisplayName(path: []const u8) []const u8 {
    const trimmed = std.mem.trimRight(u8, path, "\\/");
    const base = std.fs.path.basename(trimmed);
    return if (base.len > 0 and base.len <= 64) base else "Vex";
}

fn readGitBranch(project_path: []const u8, out_buf: *[128]u8) []const u8 {
    var current = std.mem.trimRight(u8, project_path, "\\/");
    for (0..16) |_| {
        if (current.len == 0) break;
        var path_buf: [1024]u8 = undefined;
        if (std.fmt.bufPrint(&path_buf, "{s}\\.git\\HEAD", .{current})) |head_path| {
            if (std.fs.openFileAbsolute(head_path, .{})) |file| {
                defer file.close();
                var raw: [256]u8 = undefined;
                if (file.readAll(&raw)) |read_len| {
                    const line = std.mem.trim(u8, raw[0..read_len], " \t\r\n");
                    const prefix = "ref: refs/heads/";
                    const branch = if (std.mem.startsWith(u8, line, prefix))
                        line[prefix.len..]
                    else if (line.len >= 7)
                        line[0..7]
                    else
                        "";
                    if (branch.len > 0 and branch.len <= out_buf.len and std.unicode.utf8ValidateSlice(branch)) {
                        @memcpy(out_buf[0..branch.len], branch);
                        return out_buf[0..branch.len];
                    }
                } else |_| {}
                return "";
            } else |_| {}
        } else |_| break;
        const parent = std.fs.path.dirname(current) orelse break;
        if (std.mem.eql(u8, parent, current)) break;
        current = parent;
    }
    return "";
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

test "filterCommands matches empty query, case-insensitive substrings, and multi-token queries" {
    var matched: [command_defs.len]CommandDef = undefined;
    const all = filterCommands("", &matched);
    try std.testing.expectEqual(command_defs.len, all);

    const theme = filterCommands("theme", &matched);
    try std.testing.expect(theme >= 2);

    const tab_new = filterCommands("tab new", &matched);
    try std.testing.expect(tab_new >= 2);

    const dark_mode = filterCommands("theme dark", &matched);
    try std.testing.expectEqual(@as(usize, 1), dark_mode);
    try std.testing.expectEqual(CommandId.toggle_dark_light, matched[0].id);

    const ctrl_r = filterCommands("Ctrl+Shift+R", &matched);
    try std.testing.expectEqual(@as(usize, 1), ctrl_r);
    try std.testing.expectEqual(CommandId.split_right, matched[0].id);
}
