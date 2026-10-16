const Application = @import("terminal_application.zig").TerminalApplication;
const platform = @import("../windows/windows_ffi.zig");
const panes = @import("../workspace/pane_tree.zig");
const workspace = @import("../workspace/workspace_model.zig");
const status_presentation = @import("../terminal/status_presentation.zig");
const app_icon = @import("../terminal/app_icon.zig");
const terminal_theme = @import("../workspace/terminal_theme.zig");
const close_confirmation = @import("../workspace/close_confirmation.zig");
const palette = @import("../workspace/command_palette.zig");
const commands = @import("terminal_commands.zig");

const ThemePickerOverlay = struct {
    dark_mode: bool,
    selected_slot: usize,
    hovered_slot: ?usize = null,
    initial_theme_index: usize,
};

const TabPeekOverlay = struct {
    selected_index: usize = 0,
    hovered_index: ?usize = null,
};

pub const ShellPickerMode = enum(u8) {
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

pub const OverlayState = union(enum) {
    none,
    theme_picker: ThemePickerOverlay,
    command_palette: palette.CommandPalette,
    tab_peek: TabPeekOverlay,
    path_completion: struct { pane_id: panes.PaneId },
    close_confirm: CloseConfirmOverlay,
    shell_picker: ShellPickerOverlay,
};

pub fn drawActiveOverlay(self: *Application) !void {
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
        .command_palette => |*cp| {
            var matched: [commands.command_defs.len]commands.CommandDef = undefined;
            const count = commands.filterCommands(cp.query_buf[0..cp.query_len], &matched);
            var items: [commands.command_defs.len]platform.c.VexCommandItem = undefined;
            const metrics = self.metrics orelse return;
            const layout = palette.PaletteLayout.init(metrics.width, metrics.height, count);
            const first = cp.firstVisible(layout.rows, count);
            const visible = layout.rows;
            for (matched[first..][0..visible], 0..) |cmd, idx| {
                items[idx] = .{
                    .title = cmd.title.ptr,
                    .title_length = cmd.title.len,
                    .shortcut = if (cmd.shortcut.len == 0) null else cmd.shortcut.ptr,
                    .shortcut_length = cmd.shortcut.len,
                    .selected = first + idx == cp.selected_index,
                    .hovered = if (cp.hovered_index) |hi| hi == first + idx else false,
                };
            }
            platform.drawCommandPalette(cp.query_buf[0..cp.query_len], items[0..visible], .{ .x = layout.x, .y = layout.y, .width = layout.width, .height = layout.height }, first, count);
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
                self.path_completion.index == .deferred and self.path_completion.index.deferred.loading,
                self.path_completion.is_truncated,
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

pub fn handleOverlayKey(self: *Application, key: u32, modifiers: u32) !bool {
    defer self.syncPathIndex() catch |err| self.fail(err);
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
            var matched: [commands.command_defs.len]commands.CommandDef = undefined;
            const count = commands.filterCommands(cp.query_buf[0..cp.query_len], &matched);
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
                cp.moveSelection(-1, count);
                platform.invalidateWindow();
                return true;
            }
            if (key == 0x28 and count > 0) {
                cp.moveSelection(1, count);
                platform.invalidateWindow();
                return true;
            }
            if (key == 0x24 or key == 0x23 or key == 0x21 or key == 0x22) {
                if (key == 0x24) cp.selected_index = 0 else if (key == 0x23) cp.selected_index = count -| 1 else cp.moveSelection(if (key == 0x21) -8 else 8, count);
                cp.hovered_index = null;
                platform.invalidateWindow();
                return true;
            }
            if (key == 0x08 or key == 0x2E) {
                cp.eraseQuery((modifiers & 2) != 0);
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

pub fn handleOverlayPointer(self: *Application, event: platform.PointerEvent, x: f32, y: f32) !void {
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
            var matched: [commands.command_defs.len]commands.CommandDef = undefined;
            const count = commands.filterCommands(cp.query_buf[0..cp.query_len], &matched);
            const layout = palette.PaletteLayout.init(metrics.width, metrics.height, count);
            const first = cp.firstVisible(layout.rows, count);
            const hit_idx: ?usize = if (layout.hitRow(x, y)) |row| first + row else null;
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
                if (x < layout.x or x >= layout.x + layout.width or y < layout.y or y >= layout.y + layout.height) {
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

fn themeIndexForModeSlot(dark_mode: bool, target_slot: usize) usize {
    var slot: usize = 0;
    for (terminal_theme.themes, 0..) |t, idx| {
        if (t.is_dark != dark_mode) continue;
        if (slot == target_slot) return idx;
        slot += 1;
    }
    return if (dark_mode) terminal_theme.default_dark_index else terminal_theme.default_light_index;
}

pub fn themeSlotForIndex(theme_index: usize) struct { dark_mode: bool, slot: usize } {
    const clamped = @min(theme_index, terminal_theme.themes.len - 1);
    const dark_mode = terminal_theme.themes[clamped].is_dark;
    var slot: usize = 0;
    for (terminal_theme.themes[0..clamped]) |t| {
        if (t.is_dark == dark_mode) slot += 1;
    }
    return .{ .dark_mode = dark_mode, .slot = slot };
}
