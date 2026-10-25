const std = @import("std");
const binding = @import("../terminal/api.zig");
const platform = @import("../windows/windows_ffi.zig");
const panes = @import("../workspace/pane_tree.zig");
const workspace = @import("../workspace/workspace_model.zig");
const directory = @import("../shell/directory.zig");
const status = @import("../terminal/program_status.zig");
const status_presentation = @import("../terminal/status_presentation.zig");
const terminal_paint = @import("terminal_paint.zig");
const terminal_shells = @import("terminal_shells.zig");
const shell = @import("../shell/shell_launch.zig");
const TerminalSession = @import("terminal_session.zig").TerminalSession;
const wsl_registry = @import("../windows/wsl_registry.zig");
const tab_strip = @import("../workspace/tab_strip.zig");
const app_icon = @import("../terminal/app_icon.zig");
const terminal_theme = @import("../workspace/terminal_theme.zig");
const path_completion = @import("../terminal/path_completion.zig");
const completion_popup = @import("../workspace/completion_popup.zig");
const PathIndex = @import("../terminal/path_index.zig").PathIndex;
const PathIndexCache = @import("../terminal/path_index.zig").IndexCache;
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
const custom_profiles = @import("../shell/custom_profiles.zig");

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
    startup_preferences: union(enum) { warning_pending: anyerror, applied },
    shell_launches: std.ArrayList(*shell.ShellLaunch) = .empty,
    custom_shells: custom_profiles.Catalog,
    custom_shell_load_error: ?anyerror = null,
    available_shells: [63]shell.ShellKind = undefined,
    available_shell_count: u8 = 0,
    sessions: std.ArrayList(*TerminalSession) = .empty,
    model: ?workspace.WorkspaceModel = null,
    placements: std.ArrayList(panes.PanePlacement) = .empty,
    preview_placements: std.ArrayList(panes.PanePlacement) = .empty,
    metrics: ?platform.WindowMetrics = null,
    pointer_drag: pointer.PointerDrag = .none,
    editing_tab: ?keyboard.TabRename = null,
    overlay: overlays.OverlayState = .none,
    exit_review: close_confirmation.ExitReview = .{},
    update_restart_pending: bool = false,
    path_completion: path_completion.PathCompletion,
    path_index: ?*PathIndex = null,
    path_index_revision: u64 = 0,
    path_index_cache: PathIndexCache = .{},
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
    last_process_exit_code: u32 = 0,
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
        var custom_shell_load_error: ?anyerror = null;
        var custom_shells = custom_profiles.Catalog.load(allocator, settings_path, &settings_diagnostic) catch |err| blk: {
            if (err == error.OutOfMemory) return err;
            custom_shell_load_error = err;
            settings_load_error = err;
            break :blk custom_profiles.Catalog.init(allocator);
        };
        errdefer custom_shells.deinit();
        const machine_name = try platform.machineNameAlloc(allocator);
        errdefer allocator.free(machine_name);
        const argv = try std.process.argsAlloc(allocator);
        defer std.process.argsFree(allocator, argv);
        var parsed_request = try shell.ShellRequest.parseArguments(if (argv.len > 0) argv[1..] else &.{});
        var selected_custom: ?custom_profiles.Profile = null;
        if (argv.len <= 1) {
            parsed_request = switch (settings.default_shell) {
                .startup => parsed_request,
                .wsl => .{ .wsl = .{ .distribution = .automatic, .command = .default } },
                .cmd, .powershell, .pwsh, .nu, .gitbash => .{ .builtin = std.meta.stringToEnum(shell.BuiltinShellProfile, @tagName(settings.default_shell)).? },
                else => blk: {
                    selected_custom = custom_shells.find(@intFromEnum(settings.default_shell));
                    if (selected_custom) |profile| break :blk try profile.request();
                    settings_load_error = error.UnavailableShellSelection;
                    break :blk parsed_request;
                },
            };
        }
        var request = try parsed_request.forWorkingDirectory(parsed);
        const default_distribution = if (request == .wsl and request.wsl.distribution == .automatic) try wsl_registry.readDefaultWslDistributionAlloc(allocator) else null;
        defer if (default_distribution) |name| allocator.free(name.bytes);
        if (default_distribution) |name| request.wsl.distribution = .{ .named = name };
        const system_directory = try platform.systemDirectoryAlloc(allocator);
        defer allocator.free(system_directory);
        var environment = try platform.shellEnvironment(allocator);
        defer environment.deinit();
        var launch = shell.ShellLaunch.resolve(allocator, request, system_directory, &environment) catch |err| blk: {
            if (selected_custom == null or err == error.OutOfMemory) return err;
            settings_load_error = err;
            selected_custom = null;
            break :blk try shell.ShellLaunch.resolve(allocator, .{ .builtin = .system }, system_directory, &environment);
        };
        errdefer launch.deinit();
        if (selected_custom) |profile| try launch.identifyProfile(profile.id, profile.name, profile.program, profile.arguments);
        const default_kind = launch.kind;
        try platform.setShellName(launch.shellName());
        platform.setChromePalette(terminal_theme.themes[settings.theme_index].chromePalette());
        try platform.setFontPreferences(&settings.font_family, @floatFromInt(settings.font_size));
        return .{
            .allocator = allocator,
            .api = api,
            .startup_directory = parsed,
            .machine_name = machine_name,
            .launch = launch,
            .default_shell_kind = default_kind,
            .settings = settings,
            .settings_path = settings_path,
            .custom_shells = custom_shells,
            .custom_shell_load_error = custom_shell_load_error,
            .settings_diagnostic = settings_diagnostic,
            .startup_preferences = if (settings_load_error) |err| .{ .warning_pending = err } else .applied,
            .theme_index = settings.theme_index,
            .last_dark_theme = if (terminal_theme.themes[settings.theme_index].is_dark) settings.theme_index else terminal_theme.default_dark_index,
            .last_light_theme = if (!terminal_theme.themes[settings.theme_index].is_dark) settings.theme_index else terminal_theme.default_light_index,
            .cursor_blink_enabled = settings.cursor_blink,
            .path_completion = path_completion.PathCompletion.init(allocator),
            .cursor_clock = try std.time.Timer.start(),
        };
    }
    fn deinit(self: *TerminalApplication) void {
        // Callbacks have stopped; detached native workers never borrow terminal or allocator storage.
        for (self.sessions.items) |session| session.destroy(self.allocator);
        platform.drainPtyClosures();
        self.sessions.deinit(self.allocator);
        self.exit_review.deinit(self.allocator);
        if (self.model) |*model| model.deinit();
        self.placements.deinit(self.allocator);
        self.preview_placements.deinit(self.allocator);
        if (self.path_index) |index| index.destroy();
        self.path_index_cache.clear();
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
        self.custom_shells.deinit();
    }
    pub fn fail(self: *TerminalApplication, err: anyerror) void {
        if (self.health == .failed) return;
        if (@import("error_policy.zig").recoverable(err)) {
            if (err == error.NativeDialogFailure) {
                dialogs.closeDialog();
                self.overlay = .none;
            }
            platform.reportOperationFailure(err);
            return;
        }
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
    pub fn syncAnimationWake(self: *TerminalApplication) !void {
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
        if (!active) for (self.sessions.items) |session| session.cancelApplicationCopy();
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
    pub fn scrollbarActive(self: *const TerminalApplication, id: panes.PaneId) bool {
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
            session.terminal.setColorScheme(t.is_dark) catch |err| {
                if (err == error.PtyReplyWriteFailed) self.onSessionError(session.id, 29) else return err;
            };
        }
        platform.invalidateWindow();
    }
    /// Captures runtime values without serializing any live session or launch command.
    pub fn currentPreferences(self: *TerminalApplication) preferences.UserSettings {
        return .{ .theme_index = @intCast(self.theme_index), .font_size = @intFromFloat(@round(platform.fontSize())), .cursor_blink = self.cursor_blink_enabled, .copy_on_select = self.settings.copy_on_select, .confirm_close = self.settings.confirm_close, .default_shell = self.settings.default_shell, .font_family = self.settings.font_family };
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
        var catalog = custom_profiles.Catalog.load(self.allocator, self.settings_path, &self.settings_diagnostic) catch |err| {
            dialogs.showSettingsLoadFailure(err, &self.settings_diagnostic, .reload);
            return;
        };
        if (@intFromEnum(settings.default_shell) >= 256 and catalog.find(@intFromEnum(settings.default_shell)) == null) {
            catalog.deinit();
            dialogs.showSettingsLoadFailure(error.UnavailableShellSelection, &self.settings_diagnostic, .reload);
            return;
        }
        var previous = self.custom_shells;
        self.custom_shells = catalog;
        self.discoverAvailableShells();
        if (!std.meta.eql(self.currentPreferences(), settings)) {
            self.updatePreferences(settings, .lua) catch |err| {
                self.custom_shells.deinit();
                self.custom_shells = previous;
                self.discoverAvailableShells();
                dialogs.showSettingsLoadFailure(err, &self.settings_diagnostic, .reload);
                return;
            };
        } else self.settings = settings;
        previous.deinit();
        self.custom_shell_load_error = null;
    }
    fn updatePreferences(self: *TerminalApplication, settings: preferences.UserSettings, source: enum { gui, lua }) !void {
        try settings.validate();
        const kind: shell.ShellKind = if (settings.default_shell == .startup) self.launch.kind else @enumFromInt(@intFromEnum(settings.default_shell));
        if (settings.default_shell != .startup and settings.default_shell != self.settings.default_shell) {
            if (source == .lua or self.available_shell_count == 0) self.discoverAvailableShells();
            var available = false;
            for (self.available_shells[0..self.available_shell_count]) |candidate| if (candidate == kind) {
                available = true;
            };
            if (!available) return error.UnavailableShellSelection;
            if (@intFromEnum(kind) >= 256) _ = try terminal_shells.resolveShellLaunch(self, kind);
        }
        const previous = self.settings;
        const previous_kind = self.default_shell_kind;
        self.applyPreferences(settings, kind) catch |err| {
            self.applyPreferences(previous, previous_kind) catch |rollback| self.fail(rollback);
            return err;
        };
        if (source == .gui and !std.meta.eql(previous, settings)) preferences.saveUserSettings(self.allocator, self.settings_path, settings) catch |err| {
            self.applyPreferences(previous, previous_kind) catch |rollback| self.fail(rollback);
            dialogs.showSettingsFailure(err);
            return;
        };
    }
    fn applyPreferences(self: *TerminalApplication, settings: preferences.UserSettings, kind: shell.ShellKind) !void {
        try platform.setFontPreferences(&settings.font_family, @floatFromInt(settings.font_size));
        self.settings = settings;
        if (self.theme_index != settings.theme_index) try self.applyTheme(settings.theme_index);
        self.cursor_blink_enabled = settings.cursor_blink;
        self.cursor_blink.synchronize(null, self.cursorNowMs());
        self.default_shell_kind = kind;
        try platform.setShellName(if (settings.default_shell == .startup) self.launch.shellName() else self.shellLabel(kind));
        try self.syncAnimationWake();
        platform.invalidateWindow();
    }
    fn toggleDarkLightTheme(self: *TerminalApplication) !void {
        const current = self.currentTheme();
        const target = if (current.is_dark) self.last_light_theme else self.last_dark_theme;
        try self.applyTheme(target);
        if (self.overlay != .theme_picker) try self.commitPreferences(self.currentPreferences());
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
        const reported = if (self.tabProgramStatus(tab.tree.root)) |presentation|
            status_presentation.statusTitle(presentation.summary, active_session.displayTitle())
        else |_|
            active_session.displayTitle();
        const initial = tab.customTitle() orelse reported;
        self.editing_tab = keyboard.TabRename.init(tab.id, initial);
        platform.invalidateWindow();
    }
    pub fn commitTabRename(self: *TerminalApplication) void {
        const edit = self.editing_tab orelse return;
        self.editing_tab = null;
        const model = if (self.model) |*model| model else return;
        if (model.tabIndex(edit.tab_id)) |idx| {
            const raw = edit.buf[0..edit.len];
            if (std.mem.trim(u8, raw, " \t\r\n").len == 0) {
                model.tabs.items[idx].clearCustomTitle();
            } else {
                model.tabs.items[idx].setCustomTitle(raw);
            }
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
        terminal_shells.discoverAvailableShells(self);
    }
    pub fn shellLabel(self: *const TerminalApplication, kind: shell.ShellKind) []const u8 {
        return terminal_shells.shellLabel(self, kind);
    }
    pub fn shellDetail(self: *const TerminalApplication, kind: shell.ShellKind) []const u8 {
        return terminal_shells.shellDetail(self, kind);
    }
    pub fn addCustomShell(self: *TerminalApplication, name: []const u8, program: []const u8, arguments: []const u8) !void {
        try terminal_shells.addCustomShell(self, name, program, arguments);
    }
    pub fn addSession(self: *TerminalApplication, path: directory.DirectoryPath) !*TerminalSession {
        return self.addSessionWithKind(path, self.default_shell_kind);
    }
    fn addHomeSessionWithKind(self: *TerminalApplication, kind: shell.ShellKind) !*TerminalSession {
        platform.resetPtyLaunchError();
        const launch_ptr = terminal_shells.resolveShellLaunch(self, kind) catch |err| return if (err == error.OutOfMemory) err else error.ShellStartFailed;
        const home = try std.process.getEnvVarOwned(self.allocator, "USERPROFILE");
        defer self.allocator.free(home);
        var path = try directory.DirectoryPath.parse(home);
        path.shell_home = launch_ptr.backend == .wsl;
        return self.addSessionWithKind(path, kind);
    }
    fn addSessionWithKind(self: *TerminalApplication, path: directory.DirectoryPath, kind: shell.ShellKind) !*TerminalSession {
        platform.resetPtyLaunchError();
        const launch_ptr = terminal_shells.resolveShellLaunch(self, kind) catch |err| return if (err == error.OutOfMemory) err else error.ShellStartFailed;
        // Reserving first prevents an allocation failure from stranding a live process.
        try self.sessions.ensureUnusedCapacity(self.allocator, 1);
        const token = self.next_token;
        self.next_token = try std.math.add(u32, token, 1);
        const session = TerminalSession.create(self.allocator, &self.api, @enumFromInt(token), path, self.machine_name, launch_ptr) catch |err| return if (err == error.OutOfMemory) err else error.ShellStartFailed;
        errdefer session.destroy(self.allocator);
        const t = self.currentTheme();
        try session.terminal.applyPalette(t.foreground, t.background, t.cursor, &t.ansi);
        try session.terminal.setColorScheme(t.is_dark);
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
        const first = try self.addHomeSessionWithKind(self.default_shell_kind);
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
        for (self.sessions.items) |session| {
            if (session.process_probe.noteVisibility(now, self.findPlacement(session.id) != null))
                session.pty.noteProcessInput();
        }
        for (self.placements.items) |placement| {
            const session = self.findSession(placement.id) orelse return error.MissingPaneSession;
            const size = try metrics.terminalSize(placement.bounds);
            session.resize(size) catch |err| {
                if (err == error.PtyResizeFailed) {
                    self.onSessionError(session.id, 29); // ERROR_WRITE_FAULT
                    try session.resize(size);
                } else if (err == error.PtyInputFull or err == error.SessionTransportFailed) {
                    self.fail(err);
                } else return err;
            };
            _ = session.refreshAppIconIfDue(now);
            if (session.process_probe.nextFastCheck(now)) |delay| platform.scheduleDeferredTick(delay);
        }
        platform.invalidateWindow();
    }
    pub fn onResize(self: *TerminalApplication, metrics: platform.WindowMetrics) void {
        if (self.health == .failed) return;
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
        // Natural process exit can precede its last queued output block.
        if (session.pty.handle == null) return false;
        const previous = status_presentation.statusPresentationKey(session.terminal.program_status.programStatus());
        const previous_title = session.terminal.title.generation;
        const previous_attention = session.needs_attention;
        session.terminal.feedOutput(bytes) catch |err| {
            if (err == error.PtyReplyWriteFailed) {
                self.onSessionError(id, 29);
                return true;
            }
            self.fail(err);
            return false;
        };
        const model = if (self.model) |*model| model else return false;
        const visible = model.activeTab().tree.containsPane(id);
        session.deferCopyFeedback();
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
        session.process_probe.noteOutput(now_probe);
        if (chrome_changed) _ = session.refreshAppIcon(false);
        _ = session.refreshAppIconIfDue(now_probe);
        if (session.process_probe.nextFastCheck(now_probe)) |delay| platform.scheduleDeferredTick(delay);
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
    pub fn onProcessChanged(self: *TerminalApplication, id: panes.PaneId) void {
        if (self.health == .failed) return;
        const session = self.findSession(id) orelse return;
        if (session.state != .running) return;
        session.process_probe.last_probe_ms = std.time.milliTimestamp();
        if (session.refreshAppIcon(true)) platform.invalidateWindow();
    }

    pub fn onClipboard(self: *TerminalApplication, token: u32, text: []const u8) void {
        if (self.health == .failed or !self.cursor_window_active or self.overlay != .none or self.editing_tab != null) return;
        const session = self.activeSession() orelse return;
        if (token != 0 and token != @intFromEnum(session.id)) return;
        if (token == 0 and !session.copy_source.acceptsUnattributed(std.time.milliTimestamp(), text)) return;
        session.queueCopyFeedback(text);
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
        const active = self.activeSession();
        for (self.sessions.items) |session| {
            const visible = active == session and self.cursor_window_active and self.overlay == .none and self.editing_tab == null;
            if (session.finishCopyFeedback(self.cursorNowMs(), visible)) any_changed = true;
        }
        for (self.placements.items) |placement| {
            if (self.findSession(placement.id)) |session| {
                if (session.refreshAppIconIfDue(now)) any_changed = true;
                if (session.state == .running) {
                    if (session.process_probe.nextFastCheck(now)) |delay| platform.scheduleDeferredTick(delay);
                }
            }
        }
        if (any_changed) platform.invalidateWindow();
    }
    pub fn onPaint(self: *TerminalApplication) void {
        if (self.health == .failed) return;
        terminal_paint.paintWorkspace(self) catch |err| self.fail(err);
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
    pub fn onCloseRequested(self: *TerminalApplication) bool {
        if (self.health == .failed or !self.settings.confirm_close) return true;
        return self.reviewWindowClose() catch |err| {
            var buffer: [256]u8 = undefined;
            const message = std.fmt.bufPrint(&buffer, "Could not review running programs ({s}). Vex was not closed. Please try again.", .{@errorName(err)}) catch "Could not review running programs. Vex was not closed.";
            dialogs.showOperationFailure(message);
            return false;
        };
    }
    fn reviewWindowClose(self: *TerminalApplication) !bool {
        if (self.overlay == .exit_confirm) return false;
        self.commitTabRename();
        self.exit_review.rows.clearRetainingCapacity();
        const model = if (self.model) |*model| model else return true;
        for (model.tabs.items, 0..) |tab, index| {
            var pane_index: usize = 0;
            try self.reviewExitNode(tab.tree.root, index, &pane_index, tab.customTitle() orelse "");
        }
        if (self.exit_review.rows.items.len == 0) return true;
        if (self.overlay == .theme_picker) try self.applyTheme(self.overlay.theme_picker.initial_theme_index);
        dialogs.closeDialog();
        self.overlay = .exit_confirm;
        errdefer self.overlay = .none;
        try modals.presentTerminalModal(self);
        platform.invalidateWindow();
        return false;
    }
    fn reviewExitNode(self: *TerminalApplication, node: anytype, tab_index: usize, pane_index: *usize, tab_title: []const u8) anyerror!void {
        switch (node.*) {
            .leaf => |id| {
                const session = self.findSession(id) orelse return error.MissingPaneSession;
                try self.exit_review.includePane(self.allocator, session.closeSnapshot(true), tab_index, pane_index.*, tab_title);
                pane_index.* += 1;
            },
            .split => |split| {
                try self.reviewExitNode(split.first, tab_index, pane_index, tab_title);
                try self.reviewExitNode(split.second, tab_index, pane_index, tab_title);
            },
        }
    }
    /// Update restarts use the same live-process review as an ordinary window close.
    pub fn requestUpdateRestart(self: *TerminalApplication) void {
        if (self.overlay == .exit_confirm) return;
        if (!platform.c.vex_update_prepared()) {
            if (platform.c.vex_update_ready()) {
                if (!platform.c.vex_update_prepare()) dialogs.showOperationFailure("Could not prepare the updater. Vex and your terminals were not closed. Please try again.");
            } else platform.c.vex_update_check();
            return;
        }
        self.update_restart_pending = true;
        if (self.onCloseRequested()) {
            self.completeWindowExit();
        } else if (self.overlay != .exit_confirm) {
            self.update_restart_pending = false;
            platform.c.vex_update_cancel_install();
        }
    }
    fn completeWindowExit(self: *TerminalApplication) void {
        if (self.update_restart_pending) {
            self.update_restart_pending = false;
            if (!platform.c.vex_update_install()) {
                platform.c.vex_update_cancel_install();
                dialogs.showOperationFailure("Could not start the updater. Check for updates and try again. Vex and your terminals were not closed.");
                return;
            }
        }
        platform.closeWindow();
    }
    pub fn confirmExit(self: *TerminalApplication) void {
        if (self.overlay != .exit_confirm) return;
        dialogs.closeDialog();
        self.overlay = .none;
        self.exit_review.rows.clearRetainingCapacity();
        self.completeWindowExit();
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
        if (if (self.settings.confirm_close) close_confirmation.evaluatePane(session.closeSnapshot(true), .pane) else null) |confirmation| {
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
        if (self.settings.confirm_close) try self.reviewTabClose(model.tabs.items[index].tree.root, &review);
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
        const dir = try path_completion.initialDirectoryAlloc(self.allocator, session.directory.current(), session.directory.context);
        defer self.allocator.free(dir);
        if (self.path_completion.index == .synchronous) self.path_completion.index = .{ .deferred = .{} };
        self.path_completion.recursive = true;
        // Folder contents may have changed since the previous session.
        self.path_index_cache.clear();
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
            if (self.path_index_cache.cloneFor(self.path_completion.directory) catch null) |cached| {
                var hit = cached;
                defer hit.deinit();
                self.path_index_revision = state.revision;
                if (self.path_index) |running| running.supersede(state.revision);
                self.path_completion.applyDeferredIndex(&hit) catch |err| {
                    self.path_completion.failDeferredIndex();
                    if (self.overlay == .path_completion) platform.invalidateWindow();
                    return err;
                };
                if (self.overlay == .path_completion) platform.invalidateWindow();
                return;
            }
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
                .failure => |err| {
                    self.path_completion.failDeferredIndex();
                    if (self.overlay == .path_completion) platform.invalidateWindow();
                    return err;
                },
                .index => |*completed| {
                    const applied = if (result.partial)
                        self.path_completion.applyPartialIndex(completed)
                    else
                        self.path_completion.applyDeferredIndex(completed);
                    applied catch |err| {
                        self.path_completion.failDeferredIndex();
                        if (self.overlay == .path_completion) platform.invalidateWindow();
                        return err;
                    };
                    if (!result.partial) self.path_index_cache.put(&self.path_completion) catch {};
                },
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
        const quote_kind = if (session.directory.context == .wsl and session.foreground_is_shell and !session.foreground_is_transport)
            path_completion.ShellQuoteKind.forWslShell(session.active_process_title[0..session.active_process_title_len])
        else
            path_completion.ShellQuoteKind.fromProgram(session.shell_program);
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
                settings.default_shell = if (kind == .custom) .startup else @enumFromInt(@intFromEnum(kind));
                try self.commitPreferences(settings);
            },
            .new_tab => {
                if (model.tabs.items.len >= workspace.WorkspaceModel.max_tabs) {
                    platform.invalidateWindow();
                    return;
                }
                const session = try self.addHomeSessionWithKind(kind);
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
            .check_for_updates => platform.c.vex_update_check(),
            .update_restart => self.requestUpdateRestart(),
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
            .update_restart => {
                self.requestUpdateRestart();
                return;
            },
            .settings => {
                self.commitTabRename();
                self.discoverAvailableShells();
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
                const mode: overlays.ShellPickerMode = switch (index) {
                    1...7 => .new_tab,
                    11...17 => .split_right,
                    21...27 => .split_down,
                    31...37 => .set_default,
                    else => return,
                };
                try self.executeShellPickerChoice(mode, @enumFromInt(index % 10));
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
                const session = try self.addHomeSessionWithKind(self.default_shell_kind);
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
            .rename_tab, .command_palette, .theme_picker, .tab_peek, .path_completion, .zoom_in, .zoom_out, .zoom_reset, .request_close_tab, .toggle_cursor_blink, .shell_picker, .settings, .update_restart => unreachable,
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
    pub fn onSessionError(self: *TerminalApplication, id: panes.PaneId, code: u32) void {
        if (self.health == .failed) return;
        const session = self.findSession(id) orelse return;
        if (session.pty.handle == null) return;
        // Do not remove a failed pane or its history, even when other tabs exist.
        session.stopAfterTransportFailure(code);
        platform.reportOperationFailure(if (code == 0xc0000142) error.ProcessInitializationFailed else error.SessionTransportFailed);
        platform.invalidateWindow();
    }
    pub fn onExited(self: *TerminalApplication, id: panes.PaneId, code: u32) void {
        if (self.health == .failed) return;
        self.last_process_exit_code = code;
        if (code == 0xc0000142) {
            self.onSessionError(id, code);
            return;
        }
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
                dialogs.showSessionExit(code);
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

/// Keeps workspace layout below the custom title bar.
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

/// Real DLL/ConPTY owners, but no desktop window or user configuration access.
fn recoveryTestApplication(config_path: []const u8) !TerminalApplication {
    const allocator = std.testing.allocator;
    const api = try binding.Api.load();
    const cwd = try std.process.getCwdAlloc(allocator);
    errdefer allocator.free(cwd);
    const settings_path = try allocator.dupe(u8, config_path);
    errdefer allocator.free(settings_path);
    const machine = try allocator.dupe(u8, "VEX-RECOVERY-TEST");
    errdefer allocator.free(machine);
    const system = try platform.systemDirectoryAlloc(allocator);
    defer allocator.free(system);
    var environment = try platform.shellEnvironment(allocator);
    defer environment.deinit();
    var launch = try shell.ShellLaunch.resolve(allocator, .{ .builtin = .cmd }, system, &environment);
    errdefer launch.deinit();
    try platform.setFontPreferences(&preferences.default_font, 14);
    return .{
        .allocator = allocator,
        .api = api,
        .startup_directory = try directory.DirectoryPath.parse(cwd),
        .machine_name = machine,
        .launch = launch,
        .default_shell_kind = .cmd,
        .settings = .{},
        .settings_path = settings_path,
        .settings_diagnostic = .{},
        .startup_preferences = .applied,
        .custom_shells = custom_profiles.Catalog.init(allocator),
        .path_completion = path_completion.PathCompletion.init(allocator),
        .cursor_clock = try std.time.Timer.start(),
    };
}

test "OSC identities bypass process polling in visible and hidden panes and late metadata is harmless" {
    var app = try recoveryTestApplication("unused-recovery-test.lua");
    defer app.deinit();
    const first = try app.addSession(app.startup_directory);
    const hidden = try app.addSession(app.startup_directory);
    const hidden_id = hidden.id;
    app.model = try workspace.WorkspaceModel.init(app.allocator, first.id);
    try app.model.?.newTab(hidden_id);
    try std.testing.expect(app.model.?.selectTab(0));
    const future = std.time.milliTimestamp() + 10000;
    first.process_probe.last_probe_ms = future;
    hidden.process_probe.last_probe_ms = future;
    _ = app.onOutput(first.id, "\x1b]7501;state=working:app=node\x07");
    _ = app.onOutput(hidden.id, "\x1b]7501;state=working:app=python\x07");
    try std.testing.expectEqual(app_icon.GlyphId.nodedotjs, first.icon.glyph);
    try std.testing.expectEqual(app_icon.GlyphId.python, hidden.icon.glyph);
    try std.testing.expectEqual(future, first.process_probe.last_probe_ms);
    try std.testing.expectEqual(future, hidden.process_probe.last_probe_ms);
    app.onProcessChanged(hidden.id);
    try std.testing.expectEqual(app_icon.GlyphId.python, hidden.icon.glyph);
    app.onExited(hidden_id, 0);
    app.onProcessChanged(hidden_id);
    app.onProcessChanged(@enumFromInt(0xfffffff0));
    try std.testing.expect(app.findSession(hidden_id) == null);
    try std.testing.expect(app.health == .running);
}

test "final session exit preserves terminal output and uses a persistent header instead of injecting popup padding" {
    for ([_]u32{ 0, 7 }) |code| {
        var app = try recoveryTestApplication("unused-recovery-test.lua");
        defer app.deinit();
        const session = try app.addSession(app.startup_directory);
        app.model = try workspace.WorkspaceModel.init(app.allocator, session.id);
        try session.terminal.resize(40, 4, 8, 16);
        try session.terminal.feedOutput("final output");
        const cursor = (try session.terminal.readFrame()).cursor;
        app.onExited(session.id, code);
        try std.testing.expect(app.health == .running);
        try std.testing.expect(session.state == .exited);
        try std.testing.expectEqual(code, session.state.exited);
        try std.testing.expect(try session.terminal.containsViewportText("final output"));
        try std.testing.expectEqual(cursor.row, (try session.terminal.readFrame()).cursor.row);
        try std.testing.expectEqual(cursor.column, (try session.terminal.readFrame()).cursor.column);
        try std.testing.expectEqualStrings(if (code == 0) "Session finished" else "Process exited", session.displayTitle());
    }
}

test "cancelling update restart clears the install intent without closing sessions" {
    var app = try recoveryTestApplication("unused-recovery-test.lua");
    defer app.deinit();
    const session = try app.addSession(app.startup_directory);
    app.model = try workspace.WorkspaceModel.init(app.allocator, session.id);
    app.overlay = .exit_confirm;
    app.update_restart_pending = true;
    modals.onDialogEvent(&app, .cancel);
    try std.testing.expect(!app.update_restart_pending);
    try std.testing.expect(app.overlay == .none);
    try std.testing.expect(app.health == .running);
    try std.testing.expectEqual(@as(usize, 1), app.sessions.items.len);
    try std.testing.expect(session.state == .running);
    try session.writePty("echo update-cancelled\r");
}

test "copy clears selection and rendered highlights while pointer copies preserve multi-click history" {
    var app = try recoveryTestApplication("unused-recovery-test.lua");
    defer app.deinit();
    const session = try app.addSession(app.startup_directory);
    app.model = try workspace.WorkspaceModel.init(app.allocator, session.id);
    try session.terminal.resize(20, 4, 8, 16);
    try session.terminal.feedOutput("word rest");

    // Manual copy shortcuts all clear both the logical range and cached highlights.
    for ([_]struct { key: u32, modifiers: u32 }{
        .{ .key = 'C', .modifiers = 2 },
        .{ .key = 'C', .modifiers = 3 },
        .{ .key = 0x2D, .modifiers = 2 },
    }) |shortcut| {
        try session.terminal.setKeyboardSelection(0, 0, 4, 0);
        try std.testing.expect((try session.terminal.readFrame()).cells.items[0].selected);
        try std.testing.expect(app.onKey(shortcut.key, shortcut.modifiers));
        try std.testing.expect(app.health == .running);
        try std.testing.expect(!try session.terminal.hasSelection());
        for ((try session.terminal.readFrame()).cells.items) |cell| try std.testing.expect(!cell.selected);
    }

    // Clearing after an automatic copy must not restart the click counter.
    try session.terminal.clearSelection(true);
    _ = try session.terminal.selectionPress(1, 0, 12, 8);
    try session.terminal.selectionRelease(1, 0);
    try session.terminal.clearSelection(false);
    for (2..4) |click_count| {
        try std.testing.expectEqual(@as(u8, @intCast(click_count)), try session.terminal.selectionPress(1, 0, 12, 8));
        try session.terminal.selectionRelease(1, 0);
        try std.testing.expect(try session.copyAndClearSelection(app.allocator, app.cursorNowMs(), false));
        try std.testing.expect(!try session.terminal.hasSelection());
        for ((try session.terminal.readFrame()).cells.items) |cell| try std.testing.expect(!cell.selected);
    }
}

test "typing, encoded keys and paste reveal live input but viewport navigation and output preserve history" {
    var app = try recoveryTestApplication("unused-recovery-test.lua");
    defer app.deinit();
    const session = try app.addSession(app.startup_directory);
    app.model = try workspace.WorkspaceModel.init(app.allocator, session.id);
    try session.terminal.resize(20, 2, 8, 16);
    try session.terminal.feedOutput("old\r\nmiddle\r\nrecent\r\nlive");
    try std.testing.expect(app.onKey(0x24, 2));
    try std.testing.expectEqual(@as(u64, 0), (try session.terminal.scrollbar()).offset);
    try session.terminal.feedOutput(" output");
    try std.testing.expectEqual(@as(u64, 0), (try session.terminal.scrollbar()).offset);
    app.onText("x");
    var sb = try session.terminal.scrollbar();
    try std.testing.expectEqual(sb.scrollable(), sb.offset);
    session.terminal.scrollToTop();
    try std.testing.expect(app.onKey(0x25, 0));
    sb = try session.terminal.scrollbar();
    try std.testing.expectEqual(sb.scrollable(), sb.offset);
    session.terminal.scrollToTop();
    app.onPaste("pasted");
    sb = try session.terminal.scrollbar();
    try std.testing.expectEqual(sb.scrollable(), sb.offset);
    session.terminal.scrollToTop();
    try session.writeUserInput("");
    try std.testing.expectEqual(@as(u64, 0), (try session.terminal.scrollbar()).offset);
    try std.testing.expect(app.health == .running);
}

test "overview accepts during its entrance and never swallows follow-up typing or paste" {
    var app = try recoveryTestApplication("unused-recovery-test.lua");
    defer app.deinit();
    const first = try app.addSession(app.startup_directory);
    const second = try app.addSession(app.startup_directory);
    app.model = try workspace.WorkspaceModel.init(app.allocator, first.id);
    try app.model.?.newTab(second.id);
    app.metrics = .{ .width = 1120, .height = 700, .cell_width = 8, .cell_height = 16, .scale = 1 };
    app.openTabPeek();
    app.overlay.tab_peek.selected_index = 0;
    try std.testing.expect(app.onKey(0x0D, 0));
    try std.testing.expect(app.overlay == .none);
    try std.testing.expectEqual(@as(usize, 0), app.model.?.selected_tab);
    app.onText("x");
    app.openTabPeek();
    app.overlay.tab_peek.selected_index = 1;
    try std.testing.expect(!app.onKey('A', 0));
    try std.testing.expect(app.overlay == .none);
    try std.testing.expectEqual(@as(usize, 1), app.model.?.selected_tab);
    app.onText("a");
    app.openTabPeek();
    app.overlay.tab_peek.selected_index = 0;
    app.onPaste("text");
    try std.testing.expect(app.overlay == .none);
    try std.testing.expectEqual(@as(usize, 0), app.model.?.selected_tab);
    app.openTabPeek();
    app.overlay.tab_peek.selected_index = 1;
    try std.testing.expect(app.onKey(0x1B, 0));
    try std.testing.expect(app.overlay == .none);
    try std.testing.expectEqual(@as(usize, 0), app.model.?.selected_tab);
    try std.testing.expect(app.health == .running);
}

test "oversized paste rejects atomically without closing the workspace or poisoning subsequent input" {
    var app = try recoveryTestApplication("unused-recovery-test.lua");
    defer app.deinit();
    const session = try app.addSession(app.startup_directory);
    app.model = try workspace.WorkspaceModel.init(app.allocator, session.id);
    try session.terminal.feedOutput("\x1b[?2004h");
    const oversized = try app.allocator.alloc(u8, 1024 * 1024 + 1);
    defer app.allocator.free(oversized);
    @memset(oversized, 'x');
    try std.testing.expectError(error.PtyInputFull, session.paste(app.allocator, oversized));
    // Exercise the actual event catch path as well as the native atomic enqueue.
    app.onPaste(oversized);
    try std.testing.expect(app.health == .running);
    try std.testing.expect(session.state == .running);
    try std.testing.expect(session.pty.handle != null);
    try session.writePty("\r");
    try std.testing.expectEqual(@as(usize, 1), app.sessions.items.len);
}

test "maximum clipboard paste fits with DEC 2004 framing" {
    var app = try recoveryTestApplication("unused-recovery-test.lua");
    defer app.deinit();
    const session = try app.addSession(app.startup_directory);
    try session.terminal.feedOutput("\x1b[?2004h");
    const payload = try app.allocator.alloc(u8, 1024 * 1024);
    defer app.allocator.free(payload);
    @memset(payload, 'x');
    const encoded = try session.terminal.encodePasteAlloc(app.allocator, payload);
    defer app.allocator.free(encoded);
    try std.testing.expectEqual(@as(usize, 1024 * 1024 + 12), encoded.len);
    try session.writePty(encoded);
    const sink = session.pty.responseSink();
    const reply = try app.allocator.alloc(u8, 64 * 1024);
    defer app.allocator.free(reply);
    @memset(reply, 'r');
    // Protocol traffic retains capacity even if the writer has not drained any paste bytes.
    try sink.write(sink.context, reply);
}

test "transport failure preserves the pane screen and unrelated sessions; late events are harmless" {
    var app = try recoveryTestApplication("unused-recovery-test.lua");
    defer app.deinit();
    const first = try app.addSession(app.startup_directory);
    const second = try app.addSession(app.startup_directory);
    try first.terminal.feedOutput("retain this history");
    app.onSessionError(first.id, 5);
    try std.testing.expect(app.health == .running);
    try std.testing.expectEqual(@as(usize, 2), app.sessions.items.len);
    try std.testing.expect(first.state == .exited);
    try std.testing.expectEqual(@as(u32, 5), first.state.exited);
    try std.testing.expect(first.pty.handle == null);
    try std.testing.expect(!platform.c.vex_pty_exists(@intFromEnum(first.id)));
    platform.drainPtyClosures();
    try std.testing.expectEqual(@as(usize, 0), platform.c.vex_pty_pending_closures());
    try std.testing.expect(try first.terminal.containsViewportText("retain this history"));
    try std.testing.expect(!app.onOutput(first.id, "must not reach the stopped terminal"));
    app.onSessionError(first.id, 6);
    app.onSessionError(@enumFromInt(9999), 6);
    app.onExited(first.id, 0);
    try std.testing.expectEqual(@as(u32, 5), first.state.exited);
    _ = app.onOutput(second.id, "still running");
    try std.testing.expect(second.state == .running);
    try std.testing.expect(try second.terminal.containsViewportText("still running"));
    try second.writePty("\r");
    try first.resize(.{ .columns = 100, .rows = 30, .cell_width_px = 8, .cell_height_px = 16 });
}

test "late output after natural exit is retained while loader failures stay isolated" {
    var app = try recoveryTestApplication("unused-recovery-test.lua");
    defer app.deinit();
    const first = try app.addSession(app.startup_directory);
    app.model = try workspace.WorkspaceModel.init(app.allocator, first.id);
    app.onExited(first.id, 0);
    _ = app.onOutput(first.id, "last output before EOF");
    try std.testing.expect(app.health == .running);
    try std.testing.expect(try first.terminal.containsViewportText("last output before EOF"));
    const second = try app.addSession(app.startup_directory);
    try app.model.?.newTab(second.id);
    app.onExited(second.id, 0xc0000142);
    try std.testing.expect(app.health == .running);
    try std.testing.expectEqual(@as(usize, 2), app.model.?.tabs.items.len);
    try std.testing.expectEqual(@as(u32, 0xc0000142), second.state.exited);
    try std.testing.expect(second.pty.handle == null);
}

test "failed reply in late output after process exit does not poison retained history" {
    var app = try recoveryTestApplication("unused-recovery-test.lua");
    defer app.deinit();
    const session = try app.addSession(app.startup_directory);
    app.model = try workspace.WorkspaceModel.init(app.allocator, session.id);
    app.onExited(session.id, 0);
    const Denied = struct {
        fn write(_: *anyopaque, _: []const u8) !void {
            return error.PtyReplyWriteFailed;
        }
    };
    var context: u8 = 0;
    try session.terminal.attachResponseSink(.{ .context = &context, .write = Denied.write });
    _ = app.onOutput(session.id, "final output\x1b[6n");
    try std.testing.expect(app.health == .running);
    try std.testing.expect(session.pty.handle == null);
    try std.testing.expect(session.terminal.callback_error == null);
    try std.testing.expectEqual(@as(u32, 0), session.state.exited);
    try std.testing.expect(try session.terminal.containsViewportText("final output"));
}

test "recoverable event and dialog errors preserve health but invariant violations remain fatal" {
    var app = try recoveryTestApplication("unused-recovery-test.lua");
    defer app.deinit();
    app.fail(error.InvalidPasteUtf8);
    try std.testing.expect(app.health == .running);
    app.overlay = .settings;
    app.fail(error.NativeDialogFailure);
    try std.testing.expect(app.health == .running);
    try std.testing.expect(app.overlay == .none);
    app.fail(error.MissingPaneSession);
    try std.testing.expect(app.health == .failed);
    try std.testing.expectEqual(error.MissingPaneSession, app.health.failed);
}

test "unavailable shell launch is a rejected action, not a lost workspace" {
    var app = try recoveryTestApplication("unused-recovery-test.lua");
    defer app.deinit();
    const first = try app.addSession(app.startup_directory);
    app.model = try workspace.WorkspaceModel.init(app.allocator, first.id);
    app.default_shell_kind = @enumFromInt(9876);
    app.onAction(.new_tab, 0);
    try std.testing.expect(app.health == .running);
    try std.testing.expectEqual(@as(usize, 1), app.sessions.items.len);
    try std.testing.expectEqual(@as(usize, 1), app.model.?.tabs.items.len);
    try std.testing.expect(first.state == .running);
}

test "failed protocol reply quarantines its pane instead of terminating other sessions" {
    var app = try recoveryTestApplication("unused-recovery-test.lua");
    defer app.deinit();
    const first = try app.addSession(app.startup_directory);
    const second = try app.addSession(app.startup_directory);
    const Denied = struct {
        fn write(_: *anyopaque, _: []const u8) !void {
            return error.PtyReplyWriteFailed;
        }
    };
    var context: u8 = 0;
    try first.terminal.attachResponseSink(.{ .context = &context, .write = Denied.write });
    try std.testing.expect(app.onOutput(first.id, "visible before failure\x1b[6n"));
    try std.testing.expect(app.health == .running);
    try std.testing.expect(first.state == .exited);
    try std.testing.expect(first.terminal.callback_error == null);
    try std.testing.expect(try first.terminal.containsViewportText("visible before failure"));
    try std.testing.expect(second.state == .running);
    try second.writePty("\r");
    // Unsolicited theme reports use the same transport, outside feedOutput.
    try second.terminal.feedOutput("\x1b[?2031h");
    try second.terminal.attachResponseSink(.{ .context = &context, .write = Denied.write });
    try app.applyTheme(terminal_theme.default_light_index);
    try std.testing.expect(app.health == .running);
    try std.testing.expect(second.state == .exited);
    try std.testing.expect(second.pty.handle == null);
}

test "GUI and Lua stay synchronized, malformed reloads and failed saves retain committed preferences" {
    var temp = std.testing.tmpDir(.{});
    defer temp.cleanup();
    const root = try temp.dir.realpathAlloc(std.testing.allocator, ".");
    defer std.testing.allocator.free(root);
    const path = try std.fs.path.join(std.testing.allocator, &.{ root, "init.lua" });
    defer std.testing.allocator.free(path);
    var app = try recoveryTestApplication(path);
    defer app.deinit();
    defer platform.setFontPreferences(&preferences.default_font, 14) catch {};
    var gui = app.settings;
    gui.font_size = 21;
    gui.cursor_blink = false;
    try app.commitPreferences(gui);
    try std.testing.expectEqualDeep(gui, try preferences.loadUserSettings(app.allocator, path));
    try temp.dir.writeFile(.{ .sub_path = "init.lua", .data = "-- edited by hand\nvex.config.font_size = 19" });
    app.reloadUserConfiguration();
    try std.testing.expectEqual(@as(u8, 19), app.settings.font_size);
    try std.testing.expectEqual(@as(f32, 19), platform.fontSize());
    const committed = app.settings;
    try temp.dir.writeFile(.{ .sub_path = "init.lua", .data = "vex.config.font_size = " });
    app.reloadUserConfiguration();
    try std.testing.expectEqualDeep(committed, app.settings);
    try std.testing.expect(app.health == .running);
    // Backup replacement failure must prevent both disk replacement and live GUI changes.
    try temp.dir.makeDir("init.backup.lua");
    gui.font_size = 23;
    try app.commitPreferences(gui);
    try std.testing.expectEqualDeep(committed, app.settings);
    try std.testing.expectEqual(@as(f32, 19), platform.fontSize());
    const source = try temp.dir.readFileAlloc(app.allocator, "init.lua", 65536);
    defer app.allocator.free(source);
    try std.testing.expectEqualStrings("vex.config.font_size = ", source);
    try std.testing.expect(app.health == .running);
}

test "tab rename edits displayed title with cursor navigation and clearing resets custom title" {
    var app = try recoveryTestApplication("unused-recovery-test.lua");
    defer app.deinit();
    const session = try app.addSession(app.startup_directory);
    app.model = try workspace.WorkspaceModel.init(app.allocator, session.id);
    try session.terminal.feedOutput("\x1b]7501;state=working:title=QnVpbGQgcGlwZWxpbmU=\x07");

    app.onAction(.rename_tab, 0);
    try std.testing.expect(app.editing_tab != null);
    try std.testing.expect(app.editing_tab.?.select_all);
    try std.testing.expectEqualStrings("Build pipeline", app.editing_tab.?.slice());

    // Collapse selection to start with Left arrow, insert prefix, then commit with Enter.
    try std.testing.expect(app.onKey(0x25, 0));
    try std.testing.expect(!app.editing_tab.?.select_all);
    app.onText("CI: ");
    try std.testing.expect(app.onKey(0x0D, 0));
    try std.testing.expect(app.editing_tab == null);
    try std.testing.expectEqualStrings("CI: Build pipeline", app.model.?.activeTab().customTitle().?);

    // Reopen rename, clear text with Backspace while all-selected, and commit to restore automatic title.
    app.onAction(.rename_tab, 0);
    try std.testing.expect(app.editing_tab.?.select_all);
    try std.testing.expect(app.onKey(0x08, 0));
    try std.testing.expectEqualStrings("", app.editing_tab.?.slice());
    try std.testing.expect(app.onKey(0x0D, 0));
    try std.testing.expectEqual(@as(?[]const u8, null), app.model.?.activeTab().customTitle());
}
