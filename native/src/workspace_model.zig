const std = @import("std");
const panes = @import("pane_tree.zig");

/// A tab owns its split tree; the selected pane is always one of that tree's leaves.
pub const WorkspaceTab = struct { tree: panes.PaneTree, active_pane: panes.PaneId };

/// Owns one project's tabs, matching Project.MaxTabs and WPF close/focus behavior.
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
        for (self.tabs.items) |tab| if (tab.tree.containsPane(id)) return error.DuplicatePaneId;
        var tree = try panes.PaneTree.init(self.allocator, id);
        errdefer tree.deinit();
        try self.tabs.append(self.allocator, .{ .tree = tree, .active_pane = id });
        self.selected_tab = self.tabs.items.len - 1;
    }
    /// Selects a tab only if its index exists; invalid UI messages leave selection intact.
    pub fn selectTab(self: *WorkspaceModel, index: usize) bool {
        if (index >= self.tabs.items.len) return false;
        self.selected_tab = index;
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
    /// New splits focus the fresh pane, matching WorkspaceTab.Split.
    pub fn splitActivePane(self: *WorkspaceModel, id: panes.PaneId, axis: panes.SplitAxis) !void {
        for (self.tabs.items) |existing| if (existing.tree.containsPane(id)) return error.DuplicatePaneId;
        const tab = self.activeTab();
        try tab.tree.splitPane(tab.active_pane, id, axis);
        tab.active_pane = id;
    }
    /// A split close falls back to the first leaf; the final leaf must be closed as a tab.
    pub fn closeActiveSplit(self: *WorkspaceModel) ?panes.PaneId {
        const tab = self.activeTab();
        const closing = tab.active_pane;
        if (!tab.tree.closePane(closing)) return null;
        tab.active_pane = tab.tree.firstLeaf();
        return closing;
    }
    /// Focus changes only after hit testing an actual leaf of the selected tab.
    pub fn focusPane(self: *WorkspaceModel, id: panes.PaneId) bool {
        const tab = self.activeTab();
        if (!tab.tree.containsPane(id)) return false;
        tab.active_pane = id;
        return true;
    }
};

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
