const std = @import("std");
const binding = @import("../terminal/api.zig");
const platform = @import("../windows/windows_ffi.zig");
const panes = @import("../workspace/pane_tree.zig");
const workspace = @import("../workspace/workspace_model.zig");
const directory = @import("../shell/directory.zig");
const status = @import("../terminal/program_status.zig");
const status_presentation = @import("../terminal/status_presentation.zig");
const shell = @import("../shell/shell_launch.zig");
const TerminalSession = @import("terminal_session.zig").TerminalSession;
const wsl_registry = @import("../windows/wsl_registry.zig");
const tab_strip = @import("../workspace/tab_strip.zig");
const app_icon = @import("../terminal/app_icon.zig");
const terminal_theme = @import("../workspace/terminal_theme.zig");
const path_completion = @import("../terminal/path_completion.zig");
const completion_popup = @import("../workspace/completion_popup.zig");
const PathIndex = @import("../terminal/path_index.zig").PathIndex;
const close_confirmation = @import("../workspace/close_confirmation.zig");
const cursor_blink = @import("../workspace/cursor_blink.zig");
const status_pulse = @import("../workspace/status_pulse.zig");
const keyboard = @import("terminal_keyboard.zig");
const pointer = @import("terminal_pointer.zig");
const overlays = @import("terminal_overlays.zig");
const commands = @import("terminal_commands.zig");
const diagnostics = @import("terminal_diagnostics.zig");
const modals = @import("terminal_modals.zig");
const dialogs = @import("../windows/windows_dialog.zig");
const preferences = @import("user_settings.zig");

/// Owns stable sessions on the window thread; feature modules borrow this owner, never copy it.
pub const TerminalApplication = struct {
    pub const onProbe = diagnostics.onProbe;
    pub const onDialogEvent = modals.onDialogEvent;
    pub const onKey = keyboard.onKey;
    pub const onText = keyboard.onText;
    pub const onPaste = keyboard.onPaste;
    pub const onScroll = pointer.onScroll;
    pub const onMouseButton = pointer.onMouseButton;
    pub const onClick = pointer.onClick;
    pub const onPointer = pointer.onPointer;
    allocator: std.mem.Allocator,
    api: binding.Api,
    startup_directory: directory.DirectoryPath,
    machine_name: []u8,
    launch: shell.ShellLaunch,
    default_shell_kind: shell.ShellKind,
    settings: preferences.UserSettings,
    settings_path: []u8,
    settings_diagnostic: preferences.SettingsDiagnostic,
    startup_preferences: union(enum) { font_pending: ?anyerror, warning_pending: anyerror, applied },
    shell_launches: std.ArrayList(*shell.ShellLaunch) = .empty,
    available_shells: [6]shell.ShellKind = undefined,
    available_shell_count: u8 = 0,
    sessions: std.ArrayList(*TerminalSession) = .empty,
    model: ?workspace.WorkspaceModel = null,
    placements: std.ArrayList(panes.PanePlacement) = .empty,
    preview_placements: std.ArrayList(panes.PanePlacement) = .empty,
    metrics: ?platform.WindowMetrics = null,
    pointer_drag: pointer.PointerDrag = .none,
    editing_tab: ?keyboard.TabRename = null,
    overlay: overlays.OverlayState = .none,
    path_completion: path_completion.PathCompletion,
    path_index: ?*PathIndex = null,
    path_index_revision: u64 = 0,
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
    zoom_wheel: @import("../workspace/wheel_scroll.zig").WheelScroll = .{},
    completion_wheel: @import("../workspace/wheel_scroll.zig").WheelScroll = .{},
    hovered_link: ?pointer.HoveredLink = null,
    reported_mouse_pane: ?panes.PaneId = null,
    reported_mouse_buttons: u8 = 0,
    next_token: u32 = 1,
    health: union(enum) { running, failed: anyerror } = .running,

    fn init(allocator: std.mem.Allocator) !TerminalApplication {
        const api = try binding.Api.load();
        const path = try std.process.getCwdAlloc(allocator);
        errdefer allocator.free(path);
        const parsed = try directory.DirectoryPath.parse(path);
        const settings_path = try preferences.userSettingsPathAlloc(allocator);
        errdefer allocator.free(settings_path);
        var settings_load_error: ?anyerror = null;
        var settings_diagnostic: preferences.SettingsDiagnostic = .{};
        const settings = preferences.loadUserSettingsDiagnostic(allocator, settings_path, &settings_diagnostic) catch |err| blk: {
            if (err == error.OutOfMemory) return err;
            settings_load_error = err;
            break :blk preferences.UserSettings{};
        };
        const machine_name = try platform.machineNameAlloc(allocator);
        errdefer allocator.free(machine_name);
        const argv = try std.process.argsAlloc(allocator);
        defer std.process.argsFree(allocator, argv);
        var parsed_request = try shell.ShellRequest.parseArguments(if (argv.len > 0) argv[1..] else &.{});
        if (argv.len <= 1) {
            parsed_request = switch (settings.default_shell) {
                .startup => parsed_request,
                .wsl => .{ .wsl = .{ .distribution = .automatic, .command = .default } },
                else => .{ .builtin = std.meta.stringToEnum(shell.BuiltinShellProfile, @tagName(settings.default_shell)) orelse return error.InvalidDefaultShell },
            };
        }
        var request = try parsed_request.forWorkingDirectory(parsed);
        const default_distribution = if (request == .wsl and request.wsl.distribution == .automatic) try wsl_registry.readDefaultWslDistributionAlloc(allocator) else null;
        defer if (default_distribution) |name| allocator.free(name.bytes);
        if (default_distribution) |name| request.wsl.distribution = .{ .named = name };
        const system_directory = try platform.systemDirectoryAlloc(allocator);
        defer allocator.free(system_directory);
        var environment = try std.process.getEnvMap(allocator);
        defer environment.deinit();
        var launch = try shell.ShellLaunch.resolve(allocator, request, system_directory, &environment);
        errdefer launch.deinit();
        const default_kind = launch.kind;
        try platform.setShellName(launch.shellName());
        platform.setChromePalette(terminal_theme.themes[settings.theme_index].chromePalette());
        return .{
            .allocator = allocator,
            .api = api,
            .startup_directory = parsed,
            .machine_name = machine_name,
            .launch = launch,
            .default_shell_kind = default_kind,
            .settings = settings,
            .settings_path = settings_path,
            .settings_diagnostic = settings_diagnostic,
            .startup_preferences = .{ .font_pending = settings_load_error },
            .theme_index = settings.theme_index,
            .last_dark_theme = if (terminal_theme.themes[settings.theme_index].is_dark) settings.theme_index else terminal_theme.default_dark_index,
            .last_light_theme = if (!terminal_theme.themes[settings.theme_index].is_dark) settings.theme_index else terminal_theme.default_light_index,
            .cursor_blink_enabled = settings.cursor_blink,
            .path_completion = path_completion.PathCompletion.init(allocator),
            .cursor_clock = try std.time.Timer.start(),
        };
    }
    fn deinit(self: *TerminalApplication) void {
        // Window callbacks have stopped before this runs. Native worker teardown precedes VT free.
        for (self.sessions.items) |session| session.destroy(self.allocator);
        self.sessions.deinit(self.allocator);
        if (self.model) |*model| model.deinit();
        self.placements.deinit(self.allocator);
        self.preview_placements.deinit(self.allocator);
        if (self.path_index) |index| index.destroy();
        self.path_completion.deinit();
        self.allocator.free(self.startup_directory.bytes);
        self.allocator.free(self.machine_name);
        self.allocator.free(self.settings_path);
        for (self.shell_launches.items) |cached| {
            cached.deinit();
            self.allocator.destroy(cached);
        }
        self.shell_launches.deinit(self.allocator);
        self.launch.deinit();
    }
    pub fn fail(self: *TerminalApplication, err: anyerror) void {
        if (self.health == .failed) return;
        self.health = .{ .failed = err };
        platform.closeWindow();
    }
    pub fn cursorNowMs(self: *TerminalApplication) u64 {
        return self.cursor_clock.read() / std.time.ns_per_ms;
    }
    fn cursorBlinkEligible(self: *TerminalApplication) bool {
        const session = self.activeSession() orelse return false;
        return self.cursor_blink_enabled and self.cursor_window_active and self.overlay == .none and
            self.editing_tab == null and self.cursor_blink.state == .blinking and session.state == .running and
            self.cursor_blink.state.blinking.key.pane == session.id;
    }
    /// Animated status dots repaint every visible tab; hidden terminal grids stay unmaterialized.
    fn anyAnimatedStatus(self: *TerminalApplication) bool {
        const model = if (self.model) |*model| model else return false;
        if (!self.cursor_window_active or self.overlay != .none or self.editing_tab != null or !platform.animationsEnabled()) return false;
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
    fn syncAnimationWake(self: *TerminalApplication) !void {
        const now = self.cursorNowMs();
        self.status_pulse.synchronize(self.anyAnimatedStatus(), now);
        const blink_delay = self.cursor_blink.nextDelayMs(now);
        const pulse_delay = self.status_pulse.nextDelayMs(now);
        var delay: ?u32 = if (blink_delay) |bd| (if (pulse_delay) |pd| @min(bd, pd) else bd) else pulse_delay;
        for (self.placements.items) |placement| {
            const session = self.findSession(placement.id) orelse continue;
            if (session.scrollbar_visibility.nextDelayMs(now, self.scrollbarActive(placement.id))) |sd|
                delay = if (delay) |d| @min(d, sd) else sd;
        }
        try platform.scheduleCursorWake(delay);
    }
    pub fn resetCursorOnInput(self: *TerminalApplication) !void {
        const now = self.cursorNowMs();
        if (self.cursor_blink.resetOnInput(now)) platform.invalidateWindow();
        try self.syncAnimationWake();
    }
    pub fn onCursorActivity(self: *TerminalApplication, active: bool) void {
        if (self.health == .failed or self.cursor_window_active == active) return;
        self.cursor_window_active = active;
        self.cursor_blink.synchronize(null, self.cursorNowMs());
        self.syncAnimationWake() catch |err| {
            self.fail(err);
            return;
        };
        platform.invalidateWindow();
    }
    pub fn onCursorWake(self: *TerminalApplication) void {
        if (self.health == .failed) return;
        const now = self.cursorNowMs();
        var repaint = false;
        if (self.cursorBlinkEligible()) {
            if (self.cursor_blink.advance(now)) repaint = true;
        } else {
            self.cursor_blink.synchronize(null, now);
        }
        if (self.status_pulse.advance(now)) repaint = true;
        for (self.placements.items) |placement| {
            const session = self.findSession(placement.id) orelse continue;
            if (session.scrollbar_visibility.hide_at != null and !self.scrollbarActive(placement.id)) repaint = true;
        }
        if (repaint) platform.invalidateWindow();
        self.syncAnimationWake() catch |err| self.fail(err);
    }
    fn scrollbarActive(self: *const TerminalApplication, id: panes.PaneId) bool {
        return self.overlay == .none and (self.hovered_scrollbar == id or
            (self.pointer_drag == .scrollbar and self.pointer_drag.scrollbar.pane_id == id));
    }
    pub fn currentTheme(self: *const TerminalApplication) terminal_theme.TerminalTheme {
        return terminal_theme.themes[@min(self.theme_index, terminal_theme.themes.len - 1)];
    }
    pub fn applyTheme(self: *TerminalApplication, index: usize) !void {
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
    /// Captures runtime values without serializing any live session or launch command.
    pub fn currentPreferences(self: *TerminalApplication) preferences.UserSettings {
        return .{ .theme_index = @intCast(self.theme_index), .font_size = @intFromFloat(@round(platform.fontSize())), .cursor_blink = self.cursor_blink_enabled, .default_shell = self.settings.default_shell, .font_family = self.settings.font_family };
    }
    /// Writes GUI preferences only when changed; storage failure never closes running terminals.
    pub fn commitPreferences(self: *TerminalApplication, settings: preferences.UserSettings) !void {
        if (std.meta.eql(self.settings, settings) and std.meta.eql(self.currentPreferences(), settings)) return;
        try self.updatePreferences(settings, .gui);
    }
    /// Reloads Lua on demand, retaining the last valid preferences on configuration errors.
    pub fn reloadUserConfiguration(self: *TerminalApplication) void {
        self.settings_diagnostic = .{};
        const settings = preferences.loadUserSettingsDiagnostic(self.allocator, self.settings_path, &self.settings_diagnostic) catch |err| {
            dialogs.showSettingsLoadFailure(err, &self.settings_diagnostic, .reload);
            return;
        };
        if (std.meta.eql(self.currentPreferences(), settings)) {
            self.settings = settings;
            return;
        }
        self.updatePreferences(settings, .lua) catch |err| {
            dialogs.showSettingsLoadFailure(err, &self.settings_diagnostic, .reload);
        };
    }
    fn updatePreferences(self: *TerminalApplication, settings: preferences.UserSettings, source: enum { gui, lua }) !void {
        try settings.validate();
        const kind: shell.ShellKind = if (settings.default_shell == .startup) self.launch.kind else std.meta.intToEnum(shell.ShellKind, @intFromEnum(settings.default_shell)) catch return error.InvalidDefaultShell;
        if (settings.default_shell != .startup and settings.default_shell != self.settings.default_shell) {
            self.discoverAvailableShells();
            var available = false;
            for (self.available_shells[0..self.available_shell_count]) |candidate| if (candidate == kind) {
                available = true;
            };
            if (!available) return error.UnavailableShellSelection;
        }
        if (source == .gui and !std.meta.eql(self.settings, settings)) preferences.saveUserSettings(self.allocator, self.settings_path, settings) catch |err| {
            dialogs.showSettingsFailure(err);
            platform.invalidateWindow();
            return;
        };
        try platform.setFontPreferences(&settings.font_family, @floatFromInt(settings.font_size));
        self.settings = settings;
        if (self.theme_index != settings.theme_index) try self.applyTheme(settings.theme_index);
        self.cursor_blink_enabled = settings.cursor_blink;
        self.cursor_blink.synchronize(null, self.cursorNowMs());
        self.default_shell_kind = kind;
        try platform.setShellName(if (settings.default_shell == .startup) self.launch.shellName() else kind.displayName());
        try self.syncAnimationWake();
        platform.invalidateWindow();
    }
    fn toggleDarkLightTheme(self: *TerminalApplication) !void {
        const current = self.currentTheme();
        const target = if (current.is_dark) self.last_light_theme else self.last_dark_theme;
        try self.applyTheme(target);
    }
    pub fn findSession(self: *TerminalApplication, id: panes.PaneId) ?*TerminalSession {
        for (self.sessions.items) |session| if (session.id == id) return session;
        return null;
    }
    pub fn findPlacement(self: *TerminalApplication, id: panes.PaneId) ?panes.PanePlacement {
        for (self.placements.items) |placement| if (placement.id == id) return placement;
        return null;
    }
    fn beginTabRename(self: *TerminalApplication, index: usize) void {
        self.commitTabRename();
        const model = if (self.model) |*model| model else return;
        if (index >= model.tabs.items.len) return;
        _ = model.selectTab(index);
        const tab = model.tabs.items[index];
        const active_session = self.findSession(tab.active_pane) orelse return;
        const initial = tab.customTitle() orelse active_session.displayTitle();
        var edit: keyboard.TabRename = .{ .tab_id = tab.id, .select_all = true };
        const n = @min(initial.len, edit.buf.len);
        @memcpy(edit.buf[0..n], initial[0..n]);
        edit.len = @intCast(n);
        self.editing_tab = edit;
        platform.invalidateWindow();
    }
    pub fn commitTabRename(self: *TerminalApplication) void {
        const edit = self.editing_tab orelse return;
        self.editing_tab = null;
        const model = if (self.model) |*model| model else return;
        if (model.tabIndex(edit.tab_id)) |idx| {
            model.tabs.items[idx].setCustomTitle(edit.buf[0..edit.len]);
        }
        platform.invalidateWindow();
    }
    pub fn cancelTabRename(self: *TerminalApplication) void {
        if (self.editing_tab != null) {
            self.editing_tab = null;
            platform.invalidateWindow();
        }
    }
    pub fn clearOtherSelections(self: *TerminalApplication, keep: panes.PaneId) !void {
        for (self.sessions.items) |session| {
            if (session.id != keep and try session.terminal.hasSelection()) {
                try session.terminal.clearSelection(true);
            }
        }
    }
    pub fn activeSession(self: *TerminalApplication) ?*TerminalSession {
        const model = if (self.model) |*model| model else return null;
        return self.findSession(model.activeTab().active_pane);
    }
    pub fn discoverAvailableShells(self: *TerminalApplication) void {
        if (self.available_shell_count > 0) return;
        const system_directory = platform.systemDirectoryAlloc(self.allocator) catch null;
        defer if (system_directory) |dir| self.allocator.free(dir);
        var environment = std.process.getEnvMap(self.allocator) catch null;
        defer if (environment) |*env| env.deinit();

        const candidates = [_]shell.ShellKind{ .pwsh, .powershell, .cmd, .nu, .gitbash, .wsl };
        var count: u8 = 0;
        for (candidates) |candidate| {
            var available = candidate == self.launch.kind;
            if (!available) if (system_directory) |sys_dir| if (environment) |*env_ptr| {
                if (candidate == .wsl) {
                    if (shell.isWslLauncherAvailable(self.allocator, sys_dir) catch false) {
                        if (wsl_registry.readDefaultWslDistributionAlloc(self.allocator)) |distro| {
                            self.allocator.free(distro.bytes);
                            available = true;
                        } else |_| {}
                    }
                } else if (candidate.builtinProfile()) |profile| {
                    available = shell.isBuiltinShellAvailable(self.allocator, profile, sys_dir, env_ptr) catch false;
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
    fn resolveShellLaunch(self: *TerminalApplication, kind: shell.ShellKind) !*const shell.ShellLaunch {
        if (kind == self.launch.kind) return &self.launch;
        for (self.shell_launches.items) |cached| {
            if (cached.kind == kind) return cached;
        }
        try self.shell_launches.ensureUnusedCapacity(self.allocator, 1);
        const system_directory = try platform.systemDirectoryAlloc(self.allocator);
        defer self.allocator.free(system_directory);
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
        owned.* = try shell.ShellLaunch.resolve(self.allocator, request, system_directory, &environment);
        self.shell_launches.appendAssumeCapacity(owned);
        return owned;
    }
    pub fn addSession(self: *TerminalApplication, path: directory.DirectoryPath) !*TerminalSession {
        return self.addSessionWithKind(path, self.default_shell_kind);
    }
    fn addSessionWithKind(self: *TerminalApplication, path: directory.DirectoryPath, kind: shell.ShellKind) !*TerminalSession {
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
    pub fn removeSession(self: *TerminalApplication, id: panes.PaneId) void {
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
            dialogs.closeDialog();
            self.overlay = .none;
        }
        for (self.sessions.items, 0..) |session, index| if (session.id == id) {
            _ = self.sessions.swapRemove(index);
            session.destroy(self.allocator);
            return;
        };
    }
    fn initializeWorkspace(self: *TerminalApplication) !void {
        const first = try self.addSession(self.startup_directory);
        errdefer self.removeSession(first.id);
        self.model = try workspace.WorkspaceModel.init(self.allocator, first.id);
    }
    pub fn updateLayout(self: *TerminalApplication) !void {
        const model = if (self.model) |*model| model else return;
        const metrics = self.metrics orelse return;
        try platform.setTabStripLayout(try tab_strip.TabStripLayout.init(metrics.width, model.tabs.items.len));
        const tab = model.activeTab();
        const bounds = windowContentBounds(metrics);
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
        platform.invalidateWindow();
    }
    pub fn onResize(self: *TerminalApplication, metrics: platform.WindowMetrics) void {
        if (self.health == .failed) return;
        if (self.startup_preferences == .font_pending) {
            self.startup_preferences = if (self.startup_preferences.font_pending) |err| .{ .warning_pending = err } else .applied;
            if (self.settings.font_size != 14 or !std.mem.eql(u8, self.settings.fontFamily(), "Cascadia Mono")) {
                platform.setFontPreferences(&self.settings.font_family, @floatFromInt(self.settings.font_size)) catch |err| self.fail(err);
                return;
            }
        }
        _ = pointer.cancelPointerDrag(self) catch |err| {
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
    pub fn onOutput(self: *TerminalApplication, id: panes.PaneId, bytes: []const u8) bool {
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
            if (chrome_changed) _ = session.refreshAppIcon(false);
            session.sync_defer_since_ms = null;
            return chrome_changed or overlays.peekContainsPane(self, id);
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
        if (self.overlay == .tab_peek) return chrome_changed or overlays.peekContainsPane(self, id);
        return true;
    }
    pub fn onTick(self: *TerminalApplication) void {
        if (self.health == .failed) return;
        if (self.startup_preferences == .warning_pending) {
            const err = self.startup_preferences.warning_pending;
            self.startup_preferences = .applied;
            dialogs.showSettingsLoadFailure(err, &self.settings_diagnostic, .startup);
        }
        self.syncPathIndex() catch |err| {
            self.fail(err);
            return;
        };
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
    pub fn onPaint(self: *TerminalApplication) void {
        if (self.health == .failed) return;
        self.paintWorkspace() catch |err| self.fail(err);
    }
    fn paintWorkspace(self: *TerminalApplication) !void {
        try overlays.finishPeek(self);
        const model = if (self.model) |*model| model else return;
        const active_theme = self.currentTheme();
        platform.drawTabStripBackground(model.selected_tab);
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
            if (self.overlay == .tab_peek) break;
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
                try platform.drawTerminalFrame(placement.id, frame, focused and session.state == .running, self.cursor_blink.isShown(), pane_link, active_theme);
            }
            const scrollbar = try session.terminal.scrollbar();
            var geometry = platform.ScrollbarGeometry.compute(placement.bounds, scrollbar);
            if (geometry) |*sb| {
                if (self.pointer_drag == .scrollbar and self.pointer_drag.scrollbar.pane_id == placement.id) {
                    const drag = self.pointer_drag.scrollbar;
                    sb.thumb_y = sb.thumbTopForY(drag.pointer_y, drag.grab_offset);
                }
            }
            const sb_active = self.scrollbarActive(placement.id);
            const opacity = session.scrollbar_visibility.opacity(self.cursorNowMs(), geometry != null, sb_active, platform.animationsEnabled());
            if (geometry) |sb| {
                if (opacity > 0) platform.drawScrollbar(placement.bounds, sb, sb_active, opacity);
            }
            if (session.state == .exited and scrollbar.offset == scrollbar.scrollable())
                platform.drawSessionExit(placement.bounds, session.state.exited);
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
        try overlays.drawActiveOverlay(self);
    }
    fn reviewTabClose(self: *TerminalApplication, node: anytype, review: *close_confirmation.TabCloseReview) anyerror!void {
        if (review.isComplete()) return;
        switch (node.*) {
            .leaf => |id| {
                const session = self.findSession(id) orelse return error.MissingPaneSession;
                review.includePane(session.closeSnapshot(true));
            },
            .split => |split| {
                try self.reviewTabClose(split.first, review);
                try self.reviewTabClose(split.second, review);
            },
        }
    }
    pub fn requestClosePane(self: *TerminalApplication, pane_id: panes.PaneId) !void {
        self.commitTabRename();
        const model = if (self.model) |*model| model else return;
        const owner = for (model.tabs.items, 0..) |tab, index| {
            if (tab.tree.containsPane(pane_id)) break index;
        } else return;
        if (model.tabs.items[owner].tree.paneCount() <= 1) {
            return self.requestCloseTab(owner);
        }
        const session = self.findSession(pane_id) orelse return;
        if (close_confirmation.evaluatePane(session.closeSnapshot(true), .pane)) |confirmation| {
            self.overlay = .{ .close_confirm = .{
                .target = .{ .pane = pane_id },
                .confirmation = confirmation,
            } };
            try modals.presentTerminalModal(self);
            platform.invalidateWindow();
            return;
        }
        if (model.closePaneSplitAnywhere(pane_id)) |id| {
            self.removeSession(id);
            try self.updateLayout();
        }
    }
    fn requestCloseTab(self: *TerminalApplication, index: usize) !void {
        self.commitTabRename();
        const model = if (self.model) |*model| model else return;
        if (index >= model.tabs.items.len or model.tabs.items.len <= 1) return;
        var review: close_confirmation.TabCloseReview = .{};
        try self.reviewTabClose(model.tabs.items[index].tree.root, &review);
        if (review.confirmation) |confirmation| {
            self.overlay = .{ .close_confirm = .{
                .target = .{ .tab = model.tabs.items[index].id },
                .confirmation = confirmation,
            } };
            try modals.presentTerminalModal(self);
            platform.invalidateWindow();
            return;
        }
        try self.closeTab(index);
        for (self.sessions.items) |session| {
            if (model.activeTab().tree.containsPane(session.id)) session.needs_attention = false;
        }
        try self.updateLayout();
    }
    pub fn requestCloseActive(self: *TerminalApplication) !void {
        const model = if (self.model) |*model| model else return;
        if (model.activeTab().tree.paneCount() > 1) {
            try self.requestClosePane(model.activeTab().active_pane);
        } else {
            try self.requestCloseTab(model.selected_tab);
        }
    }
    pub fn confirmCloseOverlay(self: *TerminalApplication) !void {
        if (self.overlay != .close_confirm) return;
        dialogs.closeDialog();
        const target = self.overlay.close_confirm.target;
        const return_to_peek = self.overlay.close_confirm.return_to_peek;
        self.overlay = .none;
        const model = if (self.model) |*model| model else return;
        switch (target) {
            .pane => |pane_id| {
                if (model.closePaneSplitAnywhere(pane_id)) |id| {
                    self.removeSession(id);
                } else {
                    for (model.tabs.items, 0..) |tab, index| {
                        if (tab.tree.containsPane(pane_id)) {
                            try self.closeTab(index);
                            break;
                        }
                    }
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
        if (return_to_peek) |index| overlays.restorePeek(self, index);
    }
    pub fn pathCompletionPopup(self: *TerminalApplication) ?completion_popup.Layout {
        if (self.overlay != .path_completion) return null;
        const metrics = self.metrics orelse return null;
        const state = self.overlay.path_completion;
        const placement = self.findPlacement(state.pane_id) orelse return null;
        return completion_popup.compute(.{ .x = 0, .y = 0, .width = metrics.width, .height = metrics.height }, placement.bounds, metrics.cell_width, metrics.cell_height, state.anchor);
    }
    fn openPathCompletion(self: *TerminalApplication) !void {
        self.commitTabRename();
        const model = if (self.model) |*model| model else return;
        const pane_id = model.activeTab().active_pane;
        const session = self.findSession(pane_id) orelse return;
        if (session.state != .running) return;
        const frame = try session.terminal.readFrame();
        const anchor: ?completion_popup.Anchor = if (frame.cursor.visible and frame.cursor.column < frame.columns and frame.cursor.row < frame.rows)
            .{ .column = frame.cursor.caretColumn(), .row = frame.cursor.row }
        else
            null;
        const metrics = self.metrics orelse return;
        const placement = self.findPlacement(pane_id) orelse return;
        if (completion_popup.compute(.{ .x = 0, .y = 0, .width = metrics.width, .height = metrics.height }, placement.bounds, metrics.cell_width, metrics.cell_height, anchor) == null) return;
        // Only parsed OSC 7/launch metadata selects a directory; terminal text is not a trusted prompt.
        const dir = session.directory.current().bytes;
        if (self.path_completion.index == .synchronous) self.path_completion.index = .{ .deferred = .{} };
        self.path_completion.recursive = true;
        try self.path_completion.openDirectory(dir, "");
        try self.syncPathIndex();
        self.overlay = .{ .path_completion = .{ .pane_id = pane_id, .anchor = anchor } };
        platform.invalidateWindow();
    }
    pub fn syncPathIndex(self: *TerminalApplication) !void {
        const state = switch (self.path_completion.index) {
            .synchronous => return,
            .deferred => |value| value,
        };
        if (state.loading and state.revision != self.path_index_revision) {
            const index = self.path_index orelse blk: {
                const created = try PathIndex.create(self.allocator);
                self.path_index = created;
                break :blk created;
            };
            try index.request(self.path_completion.directory, state.revision);
            self.path_index_revision = state.revision;
        }
        const index = self.path_index orelse return;
        if (index.takeResult()) |value| {
            var result = value;
            defer result.deinit();
            if (result.revision != state.revision or !state.loading) return;
            switch (result.outcome) {
                .failure => |err| return err,
                .index => |*completed| try self.path_completion.applyDeferredIndex(completed),
            }
            if (self.overlay == .path_completion) platform.invalidateWindow();
        }
    }
    pub fn insertSelectedPathCompletion(self: *TerminalApplication, pane_id: panes.PaneId, raw: bool) !void {
        const session = self.findSession(pane_id) orelse {
            self.overlay = .none;
            platform.invalidateWindow();
            return;
        };
        const full_path = try self.path_completion.selectedFullPathAlloc(self.allocator) orelse return;
        defer self.allocator.free(full_path);
        const quote_kind = path_completion.ShellQuoteKind.fromProgram(session.shell_program);
        // Foreground applications own their input syntax; only a shell prompt gets shell quoting.
        const literal = raw or session.has_foreground_process or session.terminal.program_status.programStatus().state == .blocked;
        const formatted = try path_completion.formatPathAlloc(self.allocator, full_path, quote_kind, literal);
        defer self.allocator.free(formatted);
        self.overlay = .none;
        platform.invalidateWindow();
        if (session.state == .running and formatted.len > 0) try session.paste(self.allocator, formatted);
    }
    pub fn adjustFontZoom(self: *TerminalApplication, delta: f32) !void {
        _ = self;
        const current = platform.fontSize();
        const target: f32 = if (delta == 0.0) 14.0 else @round(current + delta);
        _ = try platform.setFontSize(target);
    }
    fn openThemePicker(self: *TerminalApplication) void {
        self.commitTabRename();
        self.overlay = .{ .theme_picker = .{ .initial_theme_index = self.theme_index } };
        modals.presentTerminalModal(self) catch |err| self.fail(err);
        platform.invalidateWindow();
    }
    fn openCommandPalette(self: *TerminalApplication) void {
        self.commitTabRename();
        self.overlay = .{ .command_palette = .{} };
        modals.presentTerminalModal(self) catch |err| self.fail(err);
        platform.invalidateWindow();
    }
    fn openTabPeek(self: *TerminalApplication) void {
        self.commitTabRename();
        const model = if (self.model) |*model| model else return;
        dialogs.closeDialog();
        self.overlay = .{ .tab_peek = .{ .selected_index = model.selected_tab, .started_ms = self.cursorNowMs() } };
        self.syncAnimationWake() catch |err| self.fail(err);
        platform.invalidateWindow();
    }
    fn openShellPicker(self: *TerminalApplication, mode: overlays.ShellPickerMode) void {
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
        } };
        modals.presentTerminalModal(self) catch |err| self.fail(err);
        platform.invalidateWindow();
    }
    pub fn executeShellPickerChoice(self: *TerminalApplication, mode: overlays.ShellPickerMode, kind: shell.ShellKind) !void {
        if (mode != .set_default) dialogs.closeDialog();
        self.overlay = .none;
        const model = if (self.model) |*model| model else return;
        switch (mode) {
            .set_default => {
                var settings = self.currentPreferences();
                settings.default_shell = std.meta.intToEnum(preferences.DefaultShell, @intFromEnum(kind)) catch return error.InvalidDefaultShell;
                try self.commitPreferences(settings);
            },
            .new_tab => {
                if (model.tabs.items.len >= workspace.WorkspaceModel.max_tabs) {
                    platform.invalidateWindow();
                    return;
                }
                const session = try self.addSessionWithKind(self.startup_directory, kind);
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
    pub fn executeCommand(self: *TerminalApplication, id: commands.CommandId) !void {
        self.overlay = .none;
        switch (id) {
            .open_path_completion => try self.openPathCompletion(),
            .open_theme_picker => self.openThemePicker(),
            .open_settings => try self.applyAction(.settings, 0),
            .reload_configuration => self.reloadUserConfiguration(),
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
    fn collectTabIcons(self: *TerminalApplication, node: anytype, out: *[8]app_icon.AppIcon, count: *usize) anyerror!void {
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
    pub fn tabProgramStatus(self: *TerminalApplication, node: anytype) anyerror!TabStatus {
        var leaf_counter: u8 = 0;
        return self.tabProgramStatusLeafOrder(node, &leaf_counter);
    }
    fn tabProgramStatusLeafOrder(self: *TerminalApplication, node: anytype, leaf_counter: *u8) anyerror!TabStatus {
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
    fn focusTabAttentionOrigin(self: *TerminalApplication, tab_index: usize) void {
        const model = if (self.model) |*model| model else return;
        if (tab_index >= model.tabs.items.len) return;
        const presentation = self.tabProgramStatus(model.tabs.items[tab_index].tree.root) catch return;
        const target_pane = presentation.attention_pane_id orelse
            (if (presentation.summary.state == .blocked) presentation.origin_pane_id else null);
        if (target_pane) |pane_id| {
            _ = model.focusPane(pane_id);
        }
    }
    pub fn onAction(self: *TerminalApplication, action: platform.WorkspaceAction, index: u32) void {
        if (self.health == .failed) return;
        self.applyAction(action, index) catch |err| self.fail(err);
    }
    pub fn applyAction(self: *TerminalApplication, action: platform.WorkspaceAction, index: u32) !void {
        if (try pointer.cancelPointerDrag(self)) try self.updateLayout();
        switch (action) {
            .settings => {
                self.commitTabRename();
                self.overlay = .settings;
                try modals.presentTerminalModal(self);
                platform.invalidateWindow();
                return;
            },
            .toggle_cursor_blink => {
                self.cursor_blink_enabled = !self.cursor_blink_enabled;
                self.cursor_blink.synchronize(null, self.cursorNowMs());
                try self.syncAnimationWake();
                platform.invalidateWindow();
                return;
            },
            .command_palette => {
                if (index > 0) {
                    dialogs.closeDialog();
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
                    dialogs.closeDialog();
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
                        dialogs.closeDialog();
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
                        dialogs.closeDialog();
                        self.overlay = .none;
                        platform.invalidateWindow();
                    } else {
                        self.openShellPicker(.new_tab);
                    }
                    return;
                }
                if (index == 99) {
                    if (self.overlay == .shell_picker) {
                        dialogs.closeDialog();
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
                const session = try self.addSession(self.startup_directory);
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
            .rename_tab, .command_palette, .theme_picker, .tab_peek, .path_completion, .zoom_in, .zoom_out, .zoom_reset, .request_close_tab, .toggle_cursor_blink, .shell_picker, .settings => unreachable,
        }
        for (self.sessions.items) |session| {
            if (model.activeTab().tree.containsPane(session.id)) session.needs_attention = false;
        }
        try self.updateLayout();
    }
    fn closeTab(self: *TerminalApplication, index: usize) !void {
        const model = if (self.model) |*model| model else return;
        if (index >= model.tabs.items.len or model.tabs.items.len <= 1) return;
        if (self.overlay == .close_confirm and self.overlay.close_confirm.target == .tab and self.overlay.close_confirm.target.tab == model.tabs.items[index].id) {
            dialogs.closeDialog();
            self.overlay = .none;
        }
        var closing: std.ArrayList(panes.PanePlacement) = .empty;
        defer closing.deinit(self.allocator);
        try model.tabs.items[index].tree.layout(self.allocator, &closing, .{ .x = 0, .y = 0, .width = 1, .height = 1 });
        for (closing.items) |placement| self.removeSession(placement.id);
        _ = model.closeTab(index);
    }
    pub fn onExited(self: *TerminalApplication, id: panes.PaneId, code: u32) void {
        if (self.health == .failed) return;
        self.handleProcessExit(id, code) catch |err| self.fail(err);
    }
    fn handleProcessExit(self: *TerminalApplication, id: panes.PaneId, code: u32) !void {
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
        _ = try pointer.cancelPointerDrag(self);
        if (self.overlay == .close_confirm) {
            const targets_exit = switch (self.overlay.close_confirm.target) {
                .pane => |pane_id| pane_id == id,
                .tab => |target_tab| target_tab == tab_id,
            };
            if (targets_exit) {
                dialogs.closeDialog();
                self.overlay = .none;
            }
        }
        switch (model.closeExitedPane(id)) {
            .stale => return error.MissingPaneSession,
            .retained_final => {
                // Leave room for the native exit card without covering the final output.
                if (self.metrics) |metrics| {
                    const gap_rows: usize = @intFromFloat(std.math.clamp(@ceil(84 / metrics.cell_height), 1, 16));
                    for (0..gap_rows) |_| try session.terminal.feedOutput("\r\n");
                }
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
                        try modals.presentTerminalModal(self);
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
};

/// Packs up to eight ASCII bytes of a diagnostic probe string without allocating.
pub fn windowContentBounds(metrics: platform.WindowMetrics) panes.PaneRect {
    return .{ .x = 0, .y = 44, .width = @max(0, metrics.width), .height = @max(0, metrics.height - 44) };
}

/// Retains the stable application/API owner until the window loop returns; teardown joins all session workers.
pub fn runApplication(allocator: std.mem.Allocator) !void {
    var app = try TerminalApplication.init(allocator);
    defer app.deinit();
    platform.runWindow(&app) catch |err| {
        if (app.health == .failed) return app.health.failed;
        return err;
    };
    if (app.health == .failed) return app.health.failed;
}
