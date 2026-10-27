const std = @import("std");
const panes = @import("pane_tree.zig");

/// Stable tab identity derives from its first fresh pane token and survives that pane's removal.
pub const TabId = enum(u32) { _ };
/// A tab owns its split tree; reordering transfers this owner without recreating terminals.
pub const WorkspaceTab = struct {
    id: TabId,
    tree: panes.PaneTree,
    active_pane: panes.PaneId,
    is_focus_mode: bool = false,
    custom_title: [64]u8 = undefined,
    custom_title_len: u8 = 0,

    /// Returns the user-assigned tab name if non-empty, overriding dynamic terminal OSC titles.
    pub fn customTitle(self: *const WorkspaceTab) ?[]const u8 {
        return if (self.custom_title_len > 0) self.custom_title[0..self.custom_title_len] else null;
    }

    /// Trims whitespace and stores a bounded valid UTF-8 custom tab title; empty input leaves the previous title intact.
    pub fn setCustomTitle(self: *WorkspaceTab, raw: []const u8) void {
        const trimmed = std.mem.trim(u8, raw, " \t\r\n");
        if (trimmed.len == 0) return;
        var limit: usize = @min(trimmed.len, self.custom_title.len);
        while (limit > 0 and !std.unicode.utf8ValidateSlice(trimmed[0..limit])) : (limit -= 1) {}
        if (limit == 0) return;
        @memcpy(self.custom_title[0..limit], trimmed[0..limit]);
        self.custom_title_len = @intCast(limit);
    }

    /// Clears any user-assigned tab name so dynamic terminal or status titles resume.
    pub fn clearCustomTitle(self: *WorkspaceTab) void {
        self.custom_title_len = 0;
    }
};

/// Result of applying a process exit to its owning tab; stale tokens never target a replacement pane.
pub const PaneExitAction = enum { stale, retained_final, pane_closed, tab_closed };

/// Owns the window's terminal tabs directly; there is no project or workspace grouping layer.
pub const WorkspaceModel = struct {
    allocator: std.mem.Allocator,
    tabs: std.ArrayList(WorkspaceTab),
    selected_tab: usize,
    pub const max_tabs = 8;

    /// Publishes a nonempty workspace only after both allocations succeed.
    pub fn init(allocator: std.mem.Allocator, first: panes.PaneId) !WorkspaceModel {
        var model: WorkspaceModel = .{ .allocator = allocator, .tabs = .empty, .selected_tab = 0 };
        errdefer model.tabs.deinit(allocator);
        try model.newTab(first);
        return model;
    }
    /// Consumes split trees; the application separately owns terminal/process resources.
    pub fn deinit(self: *WorkspaceModel) void {
        for (self.tabs.items) |*tab| tab.tree.deinit();
        self.tabs.deinit(self.allocator);
    }
    /// Returns the selected tab without optional state: a live workspace always has one.
    pub fn activeTab(self: *WorkspaceModel) *WorkspaceTab {
        return &self.tabs.items[self.selected_tab];
    }
    /// The caller creates the new terminal first and rolls it back if this transaction fails.
    pub fn newTab(self: *WorkspaceModel, id: panes.PaneId) !void {
        if (self.tabs.items.len >= max_tabs) return error.TabLimitReached;
        const tab_id: TabId = @enumFromInt(@intFromEnum(id));
        for (self.tabs.items) |tab| if (tab.id == tab_id or tab.tree.containsPane(id)) return error.DuplicatePaneId;
        var tree = try panes.PaneTree.init(self.allocator, id);
        errdefer tree.deinit();
        try self.tabs.append(self.allocator, .{ .id = tab_id, .tree = tree, .active_pane = id });
        self.selected_tab = self.tabs.items.len - 1;
    }
    /// Selects a tab only if its index exists; invalid UI messages leave selection intact.
    pub fn selectTab(self: *WorkspaceModel, index: usize) bool {
        if (index >= self.tabs.items.len) return false;
        self.selected_tab = index;
        return true;
    }
    /// Resolves a tab ID after array moves; no caller must retain a pointer into the tab array.
    pub fn tabIndex(self: *const WorkspaceModel, id: TabId) ?usize {
        for (self.tabs.items, 0..) |tab, index| if (tab.id == id) return index;
        return null;
    }
    /// Moves an existing tab to a clamped final index without allocation; selection follows the same tab owner.
    pub fn moveTab(self: *WorkspaceModel, id: TabId, destination: usize) bool {
        const source = self.tabIndex(id) orelse return false;
        const target = @min(destination, self.tabs.items.len - 1);
        if (source == target) return false;
        const moved = self.tabs.items[source];
        if (source < target) {
            std.mem.copyForwards(WorkspaceTab, self.tabs.items[source..target], self.tabs.items[source + 1 .. target + 1]);
        } else {
            std.mem.copyBackwards(WorkspaceTab, self.tabs.items[target + 1 .. source + 1], self.tabs.items[target..source]);
        }
        self.tabs.items[target] = moved;
        if (self.selected_tab == source) self.selected_tab = target else if (source < self.selected_tab and self.selected_tab <= target) self.selected_tab -= 1 else if (target <= self.selected_tab and self.selected_tab < source) self.selected_tab += 1;
        return true;
    }
    pub fn paneTabIndex(self: *const WorkspaceModel, id: panes.PaneId) ?usize {
        for (self.tabs.items, 0..) |tab, index| if (tab.tree.containsPane(id)) return index;
        return null;
    }
    /// Both trees remain untouched until the destination's allocations succeed. Sessions never move.
    pub fn movePane(self: *WorkspaceModel, source: panes.PaneId, target: panes.PaneId, zone: panes.DropZone, divider: panes.DividerId) !bool {
        if (source == target or zone == .none) return false;
        const from = self.paneTabIndex(source) orelse return false;
        const to = self.paneTabIndex(target) orelse return false;
        if (from == to) {
            if (!try self.tabs.items[to].tree.movePane(source, target, zone)) return false;
        } else {
            try self.tabs.items[to].tree.insertPane(target, source, if (zone == .left or zone == .right) .right else .down, zone == .left or zone == .top, divider);
            // No fallible operation may follow publication into the destination tree.
            if (self.tabs.items[from].tree.paneCount() == 1) {
                var removed = self.tabs.orderedRemove(from);
                removed.tree.deinit();
            } else {
                _ = self.closePaneInTab(from, source);
            }
        }
        self.selected_tab = self.paneTabIndex(source).?;
        self.activeTab().active_pane = source;
        self.activeTab().is_focus_mode = false;
        return true;
    }
    /// Drops the entire tab group, preserving its nested splits and all live sessions.
    pub fn mergeTab(self: *WorkspaceModel, id: TabId, target: panes.PaneId, zone: panes.DropZone, divider: panes.DividerId) !bool {
        const from = self.tabIndex(id) orelse return false;
        const to = self.paneTabIndex(target) orelse return false;
        if (from == to or zone == .none) return false;
        const active = self.tabs.items[from].active_pane;
        try self.tabs.items[to].tree.insertTree(target, &self.tabs.items[from].tree, zone, divider);
        // The destination now owns the source root; removing the tab must not destroy it.
        _ = self.tabs.orderedRemove(from);
        self.selected_tab = self.paneTabIndex(active).?;
        self.activeTab().active_pane = active;
        self.activeTab().is_focus_mode = false;
        return true;
    }
    /// Detaches a split into a new tab without relaunching its PTY or changing its pane token.
    pub fn detachPane(self: *WorkspaceModel, source: panes.PaneId, id: TabId) !bool {
        const from = self.paneTabIndex(source) orelse return false;
        if (self.tabs.items[from].tree.paneCount() == 1) return false;
        if (self.tabs.items.len >= max_tabs) return error.TabLimitReached;
        if (self.tabIndex(id) != null) return error.DuplicatePaneId;
        var tree = try panes.PaneTree.init(self.allocator, source);
        errdefer tree.deinit();
        try self.tabs.ensureUnusedCapacity(self.allocator, 1);
        _ = self.closePaneInTab(from, source);
        self.tabs.appendAssumeCapacity(.{ .id = id, .tree = tree, .active_pane = source });
        self.selected_tab = self.tabs.items.len - 1;
        return true;
    }
    /// Ctrl+Tab and Ctrl+Shift+Tab wrap, without signed modulo arithmetic.
    pub fn cycleTab(self: *WorkspaceModel, forward: bool) void {
        self.selected_tab = if (forward) (self.selected_tab + 1) % self.tabs.items.len else if (self.selected_tab == 0) self.tabs.items.len - 1 else self.selected_tab - 1;
    }
    /// Keeps the final tab; chooses the left neighbor (or right for tab zero) before removal.
    pub fn closeTab(self: *WorkspaceModel, index: usize) bool {
        if (index >= self.tabs.items.len or self.tabs.items.len == 1) return false;
        const selected = if (self.selected_tab == index) (if (index > 0) index - 1 else 1) else self.selected_tab;
        var removed = self.tabs.orderedRemove(index);
        removed.tree.deinit();
        self.selected_tab = if (selected > index) selected - 1 else selected;
        return true;
    }
    /// Toggles focus/zoom mode on the active tab when multiple panes exist, matching WorkspaceTab.ToggleFocusMode.
    pub fn toggleFocusMode(self: *WorkspaceModel) bool {
        const tab = self.activeTab();
        if (tab.tree.paneCount() <= 1) {
            tab.is_focus_mode = false;
            return false;
        }
        tab.is_focus_mode = !tab.is_focus_mode;
        return true;
    }
    /// Splits a specific leaf in the active tab, unzooming and focusing the fresh pane.
    pub fn splitPane(self: *WorkspaceModel, target: panes.PaneId, id: panes.PaneId, axis: panes.SplitAxis) !void {
        return self.splitPaneOrdered(target, id, axis, false);
    }
    pub fn splitPaneOrdered(self: *WorkspaceModel, target: panes.PaneId, id: panes.PaneId, axis: panes.SplitAxis, before: bool) !void {
        for (self.tabs.items) |existing| if (existing.tree.containsPane(id)) return error.DuplicatePaneId;
        const tab = self.activeTab();
        try tab.tree.splitPaneOrdered(target, id, axis, before);
        tab.active_pane = id;
        tab.is_focus_mode = false;
    }
    /// New splits focus the fresh pane, matching WorkspaceTab.Split.
    pub fn splitActivePane(self: *WorkspaceModel, id: panes.PaneId, axis: panes.SplitAxis) !void {
        return self.splitPane(self.activeTab().active_pane, id, axis);
    }
    /// Closes a specific leaf in the active tab; falls back to the first leaf if the active pane was closed.
    pub fn closePaneSplit(self: *WorkspaceModel, target: panes.PaneId) ?panes.PaneId {
        return self.closePaneInTab(self.selected_tab, target);
    }
    pub fn closePaneSplitAnywhere(self: *WorkspaceModel, target: panes.PaneId) ?panes.PaneId {
        for (self.tabs.items, 0..) |tab, index| {
            if (tab.tree.containsPane(target)) return self.closePaneInTab(index, target);
        }
        return null;
    }
    fn closePaneInTab(self: *WorkspaceModel, index: usize, target: panes.PaneId) ?panes.PaneId {
        const tab = &self.tabs.items[index];
        if (!tab.tree.closePane(target)) return null;
        if (tab.active_pane == target or !tab.tree.containsPane(tab.active_pane)) {
            tab.active_pane = tab.tree.firstLeaf();
        }
        if (tab.tree.paneCount() <= 1 or tab.is_focus_mode) tab.is_focus_mode = false;
        return target;
    }
    /// Removes an exited pane without selecting its tab; the final workspace leaf remains readable.
    pub fn closeExitedPane(self: *WorkspaceModel, id: panes.PaneId) PaneExitAction {
        for (self.tabs.items, 0..) |tab, index| {
            if (!tab.tree.containsPane(id)) continue;
            if (self.closePaneInTab(index, id) != null) return .pane_closed;
            return if (self.closeTab(index)) .tab_closed else .retained_final;
        }
        return .stale;
    }
    /// A split close falls back to the first leaf; the final leaf must be closed as a tab.
    pub fn closeActiveSplit(self: *WorkspaceModel) ?panes.PaneId {
        return self.closePaneSplit(self.activeTab().active_pane);
    }
    /// Resolve against the full tree even while zoomed; changing focus preserves zoom.
    pub fn navigatePane(self: *WorkspaceModel, bounds: panes.PaneRect, direction: @import("pane_navigation.zig").Direction) !bool {
        const tab = self.activeTab();
        var placements: std.ArrayList(panes.PanePlacement) = .empty;
        defer placements.deinit(self.allocator);
        try tab.tree.layout(self.allocator, &placements, bounds);
        const target = @import("pane_navigation.zig").target(placements.items, tab.active_pane, direction) orelse return false;
        return self.focusPane(target);
    }
    /// Focus changes only after hit testing an actual leaf of the selected tab.
    pub fn focusPane(self: *WorkspaceModel, id: panes.PaneId) bool {
        const tab = self.activeTab();
        if (!tab.tree.containsPane(id)) return false;
        tab.active_pane = id;
        return true;
    }
};

test "pane transfers preserve ownership and focus in every direction and tab order" {
    for ([_]panes.DropZone{ .left, .right, .top, .bottom }) |zone| {
        for ([_]bool{ false, true }) |reverse| {
            var model = try WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
            defer model.deinit();
            try model.splitActivePane(@enumFromInt(2), .right);
            try model.newTab(@enumFromInt(3));
            const source: panes.PaneId = @enumFromInt(@as(u32, if (reverse) 3 else 2));
            const target: panes.PaneId = @enumFromInt(@as(u32, if (reverse) 2 else 3));
            try std.testing.expect(try model.movePane(source, target, zone, @enumFromInt(10)));
            try std.testing.expectEqual(source, model.activeTab().active_pane);
            try std.testing.expect(model.activeTab().tree.containsPane(target));
            try std.testing.expectEqual(@as(usize, if (reverse) 1 else 2), model.tabs.items.len);
            var total: usize = 0;
            for (model.tabs.items) |tab| total += tab.tree.paneCount();
            try std.testing.expectEqual(@as(usize, 3), total);
            try std.testing.expect(!try model.movePane(source, source, zone, @enumFromInt(11)));
        }
    }
}

test "pane transfer allocation failures leave both tab trees and focus untouched" {
    for (0..2) |fail_index| {
        var model = try WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
        defer model.deinit();
        try model.newTab(@enumFromInt(2));
        var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{ .fail_index = fail_index });
        model.tabs.items[1].tree.allocator = failing.allocator();
        const result = model.movePane(@enumFromInt(1), @enumFromInt(2), .left, @enumFromInt(3));
        model.tabs.items[1].tree.allocator = std.testing.allocator;
        try std.testing.expectError(error.OutOfMemory, result);
        try std.testing.expectEqual(@as(usize, 2), model.tabs.items.len);
        try std.testing.expectEqual(@as(usize, 1), model.selected_tab);
        try std.testing.expectEqual(@as(usize, 1), model.tabs.items[0].tree.paneCount());
        try std.testing.expectEqual(@as(usize, 1), model.tabs.items[1].tree.paneCount());
    }
}

test "detach and merge round trip preserves nested groups and never duplicates pane owners" {
    var model = try WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
    defer model.deinit();
    try model.splitActivePane(@enumFromInt(2), .right);
    try std.testing.expect(try model.detachPane(@enumFromInt(2), @enumFromInt(10)));
    try model.splitActivePane(@enumFromInt(3), .down);
    try std.testing.expect(try model.mergeTab(@enumFromInt(10), @enumFromInt(1), .left, @enumFromInt(11)));
    try std.testing.expectEqual(@as(usize, 1), model.tabs.items.len);
    try std.testing.expectEqual(@as(usize, 3), model.activeTab().tree.paneCount());
    try std.testing.expectEqual(@as(panes.PaneId, @enumFromInt(3)), model.activeTab().active_pane);
    try std.testing.expect(!try model.detachPane(@enumFromInt(99), @enumFromInt(12)));
    try std.testing.expect(!try model.mergeTab(model.activeTab().id, @enumFromInt(1), .right, @enumFromInt(13)));
}

test "detach rolls back every allocation failure" {
    const Scenario = struct {
        fn run(allocator: std.mem.Allocator) !void {
            var model = try WorkspaceModel.init(allocator, @enumFromInt(1));
            defer model.deinit();
            try model.splitActivePane(@enumFromInt(2), .right);
            _ = try model.detachPane(@enumFromInt(2), @enumFromInt(10));
            _ = try model.mergeTab(@enumFromInt(10), @enumFromInt(1), .bottom, @enumFromInt(11));
        }
    };
    try std.testing.checkAllAllocationFailures(std.testing.allocator, Scenario.run, .{});
}

test "keyboard pane navigation stays in the selected tab and preserves zoom" {
    var model = try WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
    defer model.deinit();
    try model.newTab(@enumFromInt(2));
    try model.splitActivePane(@enumFromInt(3), .right);
    model.activeTab().is_focus_mode = true;
    const bounds: panes.PaneRect = .{ .x = 0, .y = 0, .width = 1000, .height = 600 };
    try std.testing.expect(try model.navigatePane(bounds, .left));
    try std.testing.expectEqual(@as(panes.PaneId, @enumFromInt(2)), model.activeTab().active_pane);
    try std.testing.expect(model.activeTab().is_focus_mode);
    try std.testing.expect(!(try model.navigatePane(bounds, .left)));
    try std.testing.expect(try model.navigatePane(bounds, .previous));
    try std.testing.expectEqual(@as(panes.PaneId, @enumFromInt(3)), model.activeTab().active_pane);
    try std.testing.expectEqual(@as(panes.PaneId, @enumFromInt(1)), model.tabs.items[0].active_pane);
}

test "process exit closes any tab with the same selection fallback and ignores duplicate tokens" {
    for (0..3) |closing| for (0..3) |selected| {
        var model = try WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
        defer model.deinit();
        try model.newTab(@enumFromInt(2));
        try model.newTab(@enumFromInt(3));
        try std.testing.expect(model.selectTab(selected));
        const closed_id = model.tabs.items[closing].active_pane;
        const expected = if (selected != closing) model.activeTab().id else model.tabs.items[if (closing == 0) 1 else closing - 1].id;
        var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{ .fail_index = 0 });
        model.allocator = failing.allocator();
        const result = model.closeExitedPane(closed_id);
        model.allocator = std.testing.allocator;
        try std.testing.expectEqual(PaneExitAction.tab_closed, result);
        try std.testing.expectEqual(expected, model.activeTab().id);
        try std.testing.expectEqual(@as(usize, 2), model.tabs.items.len);
        try std.testing.expectEqual(PaneExitAction.stale, model.closeExitedPane(closed_id));
    };
}

test "background pane exit collapses its tree and focus mode without stealing visible focus" {
    var model = try WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
    defer model.deinit();
    try model.splitActivePane(@enumFromInt(2), .right);
    try model.splitActivePane(@enumFromInt(3), .down);
    try std.testing.expect(model.toggleFocusMode());
    try model.newTab(@enumFromInt(4));
    try std.testing.expectEqual(PaneExitAction.pane_closed, model.closeExitedPane(@enumFromInt(3)));
    try std.testing.expectEqual(@as(usize, 1), model.selected_tab);
    try std.testing.expectEqual(@as(panes.PaneId, @enumFromInt(4)), model.activeTab().active_pane);
    try std.testing.expectEqual(@as(panes.PaneId, @enumFromInt(1)), model.tabs.items[0].active_pane);
    try std.testing.expect(!model.tabs.items[0].is_focus_mode);
    try std.testing.expectEqual(PaneExitAction.pane_closed, model.closeExitedPane(@enumFromInt(2)));
    try std.testing.expectEqual(PaneExitAction.tab_closed, model.closeExitedPane(@enumFromInt(1)));
    try std.testing.expectEqual(PaneExitAction.retained_final, model.closeExitedPane(@enumFromInt(4)));
    try std.testing.expectEqual(@as(usize, 1), model.tabs.items.len);
    try std.testing.expectEqual(@as(panes.PaneId, @enumFromInt(4)), model.activeTab().active_pane);
}

test "tab limit, cycling, closing neighbors and final tab match the Windows model" {
    var workspace = try WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
    defer workspace.deinit();
    for (2..9) |id| try workspace.newTab(@enumFromInt(@as(u32, @intCast(id))));
    try std.testing.expectError(error.TabLimitReached, workspace.newTab(@enumFromInt(9)));
    workspace.cycleTab(true);
    try std.testing.expectEqual(@as(usize, 0), workspace.selected_tab);
    workspace.cycleTab(false);
    try std.testing.expectEqual(@as(usize, 7), workspace.selected_tab);
    try std.testing.expect(workspace.closeTab(7));
    try std.testing.expectEqual(@as(usize, 6), workspace.selected_tab);
    try std.testing.expect(workspace.selectTab(0));
    try std.testing.expect(workspace.closeTab(0));
    try std.testing.expectEqual(@as(u32, 2), @intFromEnum(workspace.activeTab().active_pane));
    while (workspace.tabs.items.len > 1) try std.testing.expect(workspace.closeTab(0));
    try std.testing.expect(!workspace.closeTab(0));
    try std.testing.expect(!workspace.selectTab(1));
}

test "splitting and closing focus follow Windows first-leaf fallback" {
    var workspace = try WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
    defer workspace.deinit();
    try workspace.splitActivePane(@enumFromInt(2), .right);
    try workspace.splitActivePane(@enumFromInt(3), .down);
    try std.testing.expectEqual(@as(u32, 3), @intFromEnum(workspace.activeTab().active_pane));
    try std.testing.expectEqual(@as(?panes.PaneId, @enumFromInt(3)), workspace.closeActiveSplit());
    try std.testing.expectEqual(@as(u32, 1), @intFromEnum(workspace.activeTab().active_pane));
    try std.testing.expectEqual(@as(?panes.PaneId, @enumFromInt(1)), workspace.closeActiveSplit());
    try std.testing.expectEqual(@as(u32, 2), @intFromEnum(workspace.activeTab().active_pane));
    try std.testing.expectEqual(@as(?panes.PaneId, null), workspace.closeActiveSplit());
}

fn checkWorkspaceAllocationFailures(allocator: std.mem.Allocator) !void {
    var workspace = try WorkspaceModel.init(allocator, @enumFromInt(1));
    defer workspace.deinit();
    workspace.newTab(@enumFromInt(2)) catch |err| {
        try std.testing.expectEqual(@as(usize, 1), workspace.tabs.items.len);
        try std.testing.expectEqual(@as(usize, 0), workspace.selected_tab);
        return err;
    };
    try workspace.splitActivePane(@enumFromInt(3), .right);
}

test "all workspace allocation failures roll back and free owned trees" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkWorkspaceAllocationFailures, .{});
}

test "duplicate cross-tab IDs and stale focus targets leave ownership and selection intact" {
    var model = try WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
    defer model.deinit();
    try std.testing.expectError(error.DuplicatePaneId, model.newTab(@enumFromInt(1)));
    try model.newTab(@enumFromInt(2));
    try std.testing.expectError(error.DuplicatePaneId, model.splitActivePane(@enumFromInt(1), .right));
    try std.testing.expect(!model.focusPane(@enumFromInt(1)));
    try model.splitActivePane(@enumFromInt(3), .down);
    _ = model.closeActiveSplit();
    try std.testing.expect(!model.focusPane(@enumFromInt(3)));
    try std.testing.expectEqual(@as(u32, 2), @intFromEnum(model.activeTab().active_pane));
}

test "tab reorder preserves every selection across left/right/clamped moves without allocating" {
    for (0..3) |source| for (0..5) |destination| for (0..3) |selected| {
        var model = try WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
        defer model.deinit();
        try model.newTab(@enumFromInt(2));
        try model.newTab(@enumFromInt(3));
        try std.testing.expect(model.selectTab(selected));
        const selected_id = model.activeTab().id;
        const moving_id = model.tabs.items[source].id;
        const root = model.tabs.items[source].tree.root;
        var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{ .fail_index = 0 });
        model.allocator = failing.allocator();
        const target = @min(destination, 2);
        try std.testing.expectEqual(source != target, model.moveTab(moving_id, destination));
        model.allocator = std.testing.allocator;
        try std.testing.expectEqual(selected_id, model.activeTab().id);
        try std.testing.expectEqual(moving_id, model.tabs.items[target].id);
        try std.testing.expectEqual(root, model.tabs.items[target].tree.root);
        try std.testing.expectEqual(@as(usize, 3), model.tabs.items.len);
        try std.testing.expect(!model.moveTab(@enumFromInt(99), 0));
        for (model.tabs.items, 0..) |tab, index| try std.testing.expectEqual(@as(?usize, index), model.tabIndex(tab.id));
    };
}

test "tab identity survives removal of its first pane and close fallback follows reordered neighbors" {
    var model = try WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
    defer model.deinit();
    try model.splitActivePane(@enumFromInt(4), .right);
    try std.testing.expect(model.focusPane(@enumFromInt(1)));
    try std.testing.expectEqual(@as(?panes.PaneId, @enumFromInt(1)), model.closeActiveSplit());
    try std.testing.expectEqual(@as(TabId, @enumFromInt(1)), model.activeTab().id);
    try std.testing.expectError(error.DuplicatePaneId, model.newTab(@enumFromInt(1)));
    try model.newTab(@enumFromInt(2));
    try model.newTab(@enumFromInt(3));
    try std.testing.expect(model.moveTab(@enumFromInt(3), 0));
    try std.testing.expectEqual(@as(usize, 0), model.selected_tab);
    try std.testing.expect(model.closeTab(0));
    try std.testing.expectEqual(@as(TabId, @enumFromInt(1)), model.activeTab().id);
    try std.testing.expectEqual(@as(panes.PaneId, @enumFromInt(4)), model.activeTab().active_pane);
    try std.testing.expect(!model.moveTab(@enumFromInt(3), 1));
}

test "overview closes inactive splits without selecting their tab or removing the final leaf" {
    var model = try WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
    defer model.deinit();
    try model.splitActivePane(@enumFromInt(2), .right);
    try model.newTab(@enumFromInt(3));
    const selected = model.selected_tab;
    try std.testing.expectEqual(@as(?panes.PaneId, @enumFromInt(2)), model.closePaneSplitAnywhere(@enumFromInt(2)));
    try std.testing.expectEqual(selected, model.selected_tab);
    try std.testing.expectEqual(@as(panes.PaneId, @enumFromInt(1)), model.tabs.items[0].active_pane);
    try std.testing.expectEqual(@as(?panes.PaneId, null), model.closePaneSplitAnywhere(@enumFromInt(1)));
    try std.testing.expectEqual(@as(?panes.PaneId, null), model.closePaneSplitAnywhere(@enumFromInt(99)));
    try std.testing.expectEqual(@as(usize, 2), model.tabs.items.len);
}

test "focus mode requires multiple panes and unzooms when splitting or closing to a single leaf" {
    var model = try WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
    defer model.deinit();
    try std.testing.expect(!model.toggleFocusMode());
    try std.testing.expect(!model.activeTab().is_focus_mode);
    try model.splitPane(@enumFromInt(1), @enumFromInt(2), .right);
    try std.testing.expect(model.toggleFocusMode());
    try std.testing.expect(model.activeTab().is_focus_mode);
    try model.splitPane(@enumFromInt(1), @enumFromInt(3), .down);
    try std.testing.expect(!model.activeTab().is_focus_mode);
    try std.testing.expectEqual(@as(panes.PaneId, @enumFromInt(3)), model.activeTab().active_pane);
    try std.testing.expect(model.toggleFocusMode());
    try std.testing.expectEqual(@as(?panes.PaneId, @enumFromInt(2)), model.closePaneSplit(@enumFromInt(2)));
    try std.testing.expect(!model.activeTab().is_focus_mode);
    try std.testing.expectEqual(@as(panes.PaneId, @enumFromInt(3)), model.activeTab().active_pane);
    try std.testing.expect(model.toggleFocusMode());
    try std.testing.expectEqual(@as(?panes.PaneId, @enumFromInt(3)), model.closePaneSplit(@enumFromInt(3)));
    try std.testing.expect(!model.activeTab().is_focus_mode);
    try std.testing.expectEqual(@as(panes.PaneId, @enumFromInt(1)), model.activeTab().active_pane);
}

test "custom tab title trims whitespace, preserves non-empty overrides, and survives tab reorder" {
    var model = try WorkspaceModel.init(std.testing.allocator, @enumFromInt(1));
    defer model.deinit();
    try std.testing.expectEqual(@as(?[]const u8, null), model.activeTab().customTitle());
    model.activeTab().setCustomTitle("  Backend API  \r\n");
    try std.testing.expectEqualStrings("Backend API", model.activeTab().customTitle().?);
    model.activeTab().setCustomTitle("   ");
    try std.testing.expectEqualStrings("Backend API", model.activeTab().customTitle().?);
    try model.newTab(@enumFromInt(2));
    try std.testing.expect(model.moveTab(@enumFromInt(1), 1));
    try std.testing.expectEqualStrings("Backend API", model.tabs.items[1].customTitle().?);
}
