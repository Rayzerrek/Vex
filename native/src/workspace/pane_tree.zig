const std = @import("std");

/// Stable pane identity; IDs are never reused while native messages may be queued.
pub const PaneId = enum(u32) { _ };
/// Matches WPF Horizontal (right) and Vertical (down) split orientation.
pub const SplitAxis = enum { right, down };
/// Divider identity derives from the fresh pane ID that created it; the application must never reuse IDs.
pub const DividerId = enum(u32) { _ };
/// Action buttons in the 28 DIP pane header bar (Focus/Zoom, Split Right, Split Down, Close).
pub const PaneHeaderAction = enum(u8) {
    focus_toggle = 1,
    split_right = 2,
    split_down = 3,
    close = 4,
};
/// Edge drop zone for pane header drag-to-split gestures, matching MainWindow.PaneDrag.cs.
pub const DropZone = enum { none, left, right, top, bottom };
/// Pane bounds use device-independent pixels, never physical screen coordinates.
pub const PaneRect = struct {
    x: f32,
    y: f32,
    width: f32,
    height: f32,
    /// Reject non-finite geometry before converting floats to terminal dimensions.
    pub fn validate(self: PaneRect) !void {
        if (!std.math.isFinite(self.x) or !std.math.isFinite(self.y) or !std.math.isFinite(self.width) or !std.math.isFinite(self.height) or self.width < 0 or self.height < 0 or !std.math.isFinite(self.x + self.width) or !std.math.isFinite(self.y + self.height)) return error.InvalidPaneGeometry;
    }
    /// Hit testing excludes right/bottom edges so adjacent panes cannot both win.
    pub fn contains(self: PaneRect, x: f32, y: f32) bool {
        return x >= self.x and y >= self.y and x < self.x + self.width and y < self.y + self.height;
    }
    /// Hit-tests the right-aligned 20x20 DIP header buttons when the pane is at least 180 DIPs wide.
    pub fn hitHeaderAction(self: PaneRect, can_focus: bool, x: f32, y: f32) ?PaneHeaderAction {
        if (!self.contains(x, y) or self.width < 180 or self.height < 28) return null;
        if (y < self.y + 4 or y >= self.y + 24) return null;
        const right = self.x + self.width - 6;
        if (x >= right - 20 and x < right) return .close;
        if (x >= right - 42 and x < right - 22) return .split_down;
        if (x >= right - 64 and x < right - 44) return .split_right;
        if (can_focus and x >= right - 86 and x < right - 66) return .focus_toggle;
        return null;
    }
    /// True when the pointer is inside the 28 DIP pane title bar and not on a header action button.
    pub fn isHeaderDragArea(self: PaneRect, can_focus: bool, x: f32, y: f32) bool {
        return self.contains(x, y) and self.height >= 28 and y < self.y + 28 and self.hitHeaderAction(can_focus, x, y) == null;
    }
    /// Maps a point inside the pane to a non-corner 25% edge band, matching MainWindow.PaneDrag.GetDropZone.
    pub fn dropZoneAt(self: PaneRect, x: f32, y: f32) DropZone {
        if (!self.contains(x, y) or self.width <= 0 or self.height <= 0) return .none;
        const rx = (x - self.x) / self.width;
        const ry = (y - self.y) / self.height;
        const edge: f32 = 0.25;
        if (rx < edge and ry >= edge and ry <= 1.0 - edge) return .left;
        if (rx > 1.0 - edge and ry >= edge and ry <= 1.0 - edge) return .right;
        if (ry < edge and rx >= edge and rx <= 1.0 - edge) return .top;
        if (ry > 1.0 - edge and rx >= edge and rx <= 1.0 - edge) return .bottom;
        return .none;
    }
    /// Computes the 42% inset split preview overlay rectangle for an active drop zone.
    pub fn splitPreviewRect(self: PaneRect, zone: DropZone) ?PaneRect {
        const fraction: f32 = 0.42;
        const inset: f32 = 4.0;
        if (self.width <= inset * 2 or self.height <= inset * 2) return null;
        return switch (zone) {
            .none => null,
            .left => .{ .x = self.x + inset, .y = self.y + inset, .width = @max(0, self.width * fraction - inset), .height = @max(0, self.height - inset * 2) },
            .right => .{ .x = self.x + self.width * (1.0 - fraction), .y = self.y + inset, .width = @max(0, self.width * fraction - inset), .height = @max(0, self.height - inset * 2) },
            .top => .{ .x = self.x + inset, .y = self.y + inset, .width = @max(0, self.width - inset * 2), .height = @max(0, self.height * fraction - inset) },
            .bottom => .{ .x = self.x + inset, .y = self.y + self.height * (1.0 - fraction), .width = @max(0, self.width - inset * 2), .height = @max(0, self.height * fraction - inset) },
        };
    }
};
/// Layout result holds a stable identity rather than a pointer into a mutable tree.
pub const PanePlacement = struct { id: PaneId, bounds: PaneRect };

/// Divider geometry uses the same DIP partition as leaf layout; no borrowed node pointer escapes hit testing.
pub const DividerPlacement = struct { id: DividerId, axis: SplitAxis, container: PaneRect, handle: PaneRect, ratio: f32 };

const PaneSplit = struct { id: DividerId, axis: SplitAxis, ratio: f32, first: *PaneNode, second: *PaneNode };
const PaneNode = union(enum) {
    leaf: PaneId,
    split: PaneSplit,
};

/// Owns all split nodes. Copying this owner requires transferring, not duplicating, ownership.
pub const PaneTree = struct {
    allocator: std.mem.Allocator,
    root: *PaneNode,
    const divider_width: f32 = 6;
    const max_depth = 64;

    /// Creates one leaf; a failed allocation publishes no partial tree.
    pub fn init(allocator: std.mem.Allocator, id: PaneId) !PaneTree {
        const root = try allocator.create(PaneNode);
        root.* = .{ .leaf = id };
        return .{ .allocator = allocator, .root = root };
    }
    /// Consumes the tree; leaf IDs do not own native sessions.
    pub fn deinit(self: *PaneTree) void {
        destroyNode(self.allocator, self.root);
    }
    /// The first leaf is the WPF focus fallback after closing a split.
    pub fn firstLeaf(self: *const PaneTree) PaneId {
        var node = self.root;
        while (true) switch (node.*) {
            .leaf => |id| return id,
            .split => |split| node = split.first,
        };
    }
    /// Counts terminal leaves, not internal split nodes.
    pub fn paneCount(self: *const PaneTree) usize {
        return countLeaves(self.root);
    }
    /// A focus target must still be a leaf of this tree, not a stale layout entry.
    pub fn containsPane(self: *const PaneTree, id: PaneId) bool {
        return findLeaf(self.root, id, 0) != null;
    }
    /// Splitting is transactional even if either child allocation fails.
    pub fn splitPane(self: *PaneTree, target: PaneId, fresh: PaneId, axis: SplitAxis) !void {
        if (findLeaf(self.root, fresh, 0) != null) return error.DuplicatePaneId;
        const found = findLeaf(self.root, target, 0) orelse return error.PaneNotFound;
        if (found.depth >= max_depth) return error.PaneDepthLimit;
        const first = try self.allocator.create(PaneNode);
        errdefer self.allocator.destroy(first);
        const second = try self.allocator.create(PaneNode);
        first.* = found.node.*;
        second.* = .{ .leaf = fresh };
        found.node.* = .{ .split = .{ .id = @enumFromInt(@intFromEnum(fresh)), .axis = axis, .ratio = 0.5, .first = first, .second = second } };
    }
    /// Preserves the final leaf; removing a split promotes its surviving sibling.
    pub fn closePane(self: *PaneTree, id: PaneId) bool {
        return removeLeaf(self.allocator, self.root, id);
    }
    /// Sets the parent divider of a leaf, matching WPF's 0.1..0.9 ratio clamp.
    pub fn setParentRatio(self: *PaneTree, id: PaneId, ratio: f32) !void {
        if (!std.math.isFinite(ratio)) return error.InvalidSplitRatio;
        const parent = findParent(self.root, id) orelse return error.PaneNotFound;
        switch (parent.*) {
            .split => |*split| split.ratio = std.math.clamp(ratio, 0.1, 0.9),
            .leaf => return error.PaneNotFound,
        }
    }
    /// Returns the divider under a finite DIP point, excluding right/bottom edges as in pane hit testing.
    pub fn dividerAtPoint(self: *const PaneTree, bounds: PaneRect, x: f32, y: f32) !?DividerPlacement {
        try bounds.validate();
        if (!std.math.isFinite(x) or !std.math.isFinite(y)) return error.InvalidDividerPoint;
        return findDividerPlacement(self.root, bounds, .{ .point = .{ .x = x, .y = y } });
    }
    /// Resolves a stable divider ID against current geometry; removed IDs never become node pointers.
    pub fn dividerPlacement(self: *const PaneTree, bounds: PaneRect, id: DividerId) !?DividerPlacement {
        try bounds.validate();
        return findDividerPlacement(self.root, bounds, .{ .id = id });
    }
    /// Changes one live divider without allocation; stale IDs return false, and ratios retain the 0.1..0.9 contract.
    pub fn setDividerRatio(self: *PaneTree, id: DividerId, ratio: f32) !bool {
        if (!std.math.isFinite(ratio)) return error.InvalidSplitRatio;
        const split = findDividerNode(self.root, id) orelse return false;
        const value = std.math.clamp(ratio, 0.1, 0.9);
        if (split.ratio == value) return false;
        split.ratio = value;
        return true;
    }
    /// Reuses the caller's placement capacity; bounds must already be in DIPs.
    pub fn layout(self: *const PaneTree, allocator: std.mem.Allocator, placements: *std.ArrayList(PanePlacement), bounds: PaneRect) !void {
        try bounds.validate();
        try placements.ensureTotalCapacity(allocator, self.paneCount());
        placements.clearRetainingCapacity();
        appendPlacement(self.root, placements, bounds);
    }
};

fn destroyNode(allocator: std.mem.Allocator, node: *PaneNode) void {
    switch (node.*) {
        .leaf => {},
        .split => |split| {
            destroyNode(allocator, split.first);
            destroyNode(allocator, split.second);
        },
    }
    allocator.destroy(node);
}
fn countLeaves(node: *const PaneNode) usize {
    return switch (node.*) {
        .leaf => 1,
        .split => |split| countLeaves(split.first) + countLeaves(split.second),
    };
}
const FoundLeaf = struct { node: *PaneNode, depth: usize };
fn findLeaf(node: *PaneNode, id: PaneId, depth: usize) ?FoundLeaf {
    return switch (node.*) {
        .leaf => |leaf| if (leaf == id) .{ .node = node, .depth = depth } else null,
        .split => |split| findLeaf(split.first, id, depth + 1) orelse findLeaf(split.second, id, depth + 1),
    };
}
fn findParent(node: *PaneNode, id: PaneId) ?*PaneNode {
    switch (node.*) {
        .leaf => return null,
        .split => |split| {
            if ((split.first.* == .leaf and split.first.leaf == id) or (split.second.* == .leaf and split.second.leaf == id)) return node;
            return findParent(split.first, id) orelse findParent(split.second, id);
        },
    }
}
fn removeLeaf(allocator: std.mem.Allocator, node: *PaneNode, id: PaneId) bool {
    switch (node.*) {
        .leaf => return false,
        .split => |split| {
            const removed, const survivor = if (split.first.* == .leaf and split.first.leaf == id) .{ split.first, split.second } else if (split.second.* == .leaf and split.second.leaf == id) .{ split.second, split.first } else return removeLeaf(allocator, split.first, id) or removeLeaf(allocator, split.second, id);
            node.* = survivor.*;
            allocator.destroy(removed);
            allocator.destroy(survivor);
            return true;
        },
    }
}
const SplitGeometry = struct { first: PaneRect, second: PaneRect, divider: PaneRect };
fn splitGeometry(bounds: PaneRect, axis: SplitAxis, ratio: f32) SplitGeometry {
    var first = bounds;
    var second = bounds;
    var divider = bounds;
    switch (axis) {
        .right => {
            const gap = @min(PaneTree.divider_width, bounds.width);
            const available = bounds.width - gap;
            first.width = available * ratio;
            divider.x += first.width;
            divider.width = gap;
            second.x = divider.x + gap;
            second.width = available - first.width;
        },
        .down => {
            const gap = @min(PaneTree.divider_width, bounds.height);
            const available = bounds.height - gap;
            first.height = available * ratio;
            divider.y += first.height;
            divider.height = gap;
            second.y = divider.y + gap;
            second.height = available - first.height;
        },
    }
    return .{ .first = first, .second = second, .divider = divider };
}
const DividerSearch = union(enum) { id: DividerId, point: struct { x: f32, y: f32 } };
fn findDividerPlacement(node: *const PaneNode, bounds: PaneRect, search: DividerSearch) ?DividerPlacement {
    switch (node.*) {
        .leaf => return null,
        .split => |split| {
            const geometry = splitGeometry(bounds, split.axis, split.ratio);
            const matches = switch (search) {
                .id => |id| split.id == id,
                .point => |point| geometry.divider.contains(point.x, point.y),
            };
            if (matches) return .{ .id = split.id, .axis = split.axis, .container = bounds, .handle = geometry.divider, .ratio = split.ratio };
            return findDividerPlacement(split.first, geometry.first, search) orelse findDividerPlacement(split.second, geometry.second, search);
        },
    }
}
fn findDividerNode(node: *PaneNode, id: DividerId) ?*PaneSplit {
    return switch (node.*) {
        .leaf => null,
        .split => |*split| if (split.id == id) split else findDividerNode(split.first, id) orelse findDividerNode(split.second, id),
    };
}
fn appendPlacement(node: *const PaneNode, placements: *std.ArrayList(PanePlacement), bounds: PaneRect) void {
    switch (node.*) {
        .leaf => |id| placements.appendAssumeCapacity(.{ .id = id, .bounds = bounds }),
        .split => |split| {
            const geometry = splitGeometry(bounds, split.axis, split.ratio);
            appendPlacement(split.first, placements, geometry.first);
            appendPlacement(split.second, placements, geometry.second);
        },
    }
}

test "nested right/down layout and close promote the sibling without losing leaf IDs" {
    var tree = try PaneTree.init(std.testing.allocator, @enumFromInt(1));
    defer tree.deinit();
    try tree.splitPane(@enumFromInt(1), @enumFromInt(2), .right);
    try tree.splitPane(@enumFromInt(2), @enumFromInt(3), .down);
    var placements: std.ArrayList(PanePlacement) = .empty;
    defer placements.deinit(std.testing.allocator);
    try tree.layout(std.testing.allocator, &placements, .{ .x = 0, .y = 0, .width = 1000, .height = 600 });
    try std.testing.expectEqual(@as(usize, 3), placements.items.len);
    try std.testing.expectEqual(@as(f32, 497), placements.items[0].bounds.width);
    try std.testing.expectEqual(@as(f32, 503), placements.items[1].bounds.x);
    try std.testing.expectEqual(@as(f32, 297), placements.items[1].bounds.height);
    try std.testing.expectEqual(@as(f32, 303), placements.items[2].bounds.y);
    try std.testing.expect(tree.closePane(@enumFromInt(2)));
    try std.testing.expectEqual(@as(usize, 2), tree.paneCount());
    try std.testing.expect(tree.closePane(@enumFromInt(1)));
    try std.testing.expectEqual(@as(PaneId, @enumFromInt(3)), tree.firstLeaf());
    try std.testing.expect(!tree.closePane(@enumFromInt(3)));
}

fn checkTreeAllocationFailures(allocator: std.mem.Allocator) !void {
    var tree = try PaneTree.init(allocator, @enumFromInt(1));
    defer tree.deinit();
    tree.splitPane(@enumFromInt(1), @enumFromInt(2), .right) catch |err| {
        try std.testing.expectEqual(@as(usize, 1), tree.paneCount());
        try std.testing.expectEqual(@as(PaneId, @enumFromInt(1)), tree.firstLeaf());
        return err;
    };
    var placements: std.ArrayList(PanePlacement) = .empty;
    defer placements.deinit(allocator);
    try tree.layout(allocator, &placements, .{ .x = 0, .y = 0, .width = 800, .height = 600 });
    try std.testing.expectEqual(@as(usize, 2), placements.items.len);
}

test "split allocation failure leaves the prior tree intact and leaks no nodes" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkTreeAllocationFailures, .{});
}

test "duplicate IDs, invalid ratios/geometry and excessive depth fail without mutation" {
    var tree = try PaneTree.init(std.testing.allocator, @enumFromInt(1));
    defer tree.deinit();
    try std.testing.expectError(error.DuplicatePaneId, tree.splitPane(@enumFromInt(1), @enumFromInt(1), .down));
    try tree.splitPane(@enumFromInt(1), @enumFromInt(2), .right);
    try std.testing.expectError(error.InvalidSplitRatio, tree.setParentRatio(@enumFromInt(2), std.math.nan(f32)));
    try tree.setParentRatio(@enumFromInt(2), 2);
    var placements: std.ArrayList(PanePlacement) = .empty;
    defer placements.deinit(std.testing.allocator);
    try std.testing.expectError(error.InvalidPaneGeometry, tree.layout(std.testing.allocator, &placements, .{ .x = 0, .y = 0, .width = std.math.inf(f32), .height = 600 }));
    try tree.layout(std.testing.allocator, &placements, .{ .x = 0, .y = 0, .width = 1006, .height = 600 });
    try std.testing.expectApproxEqAbs(@as(f32, 900), placements.items[0].bounds.width, 0.001);
    for (3..66) |value| try tree.splitPane(@enumFromInt(@as(u32, @intCast(value - 1))), @enumFromInt(@as(u32, @intCast(value))), .right);
    try std.testing.expectError(error.PaneDepthLimit, tree.splitPane(@enumFromInt(65), @enumFromInt(66), .right));
}

test "pane header action hit testing aligns with right-anchored button slots and width guard" {
    const bounds: PaneRect = .{ .x = 100, .y = 50, .width = 400, .height = 300 };
    try std.testing.expectEqual(@as(?PaneHeaderAction, .close), bounds.hitHeaderAction(true, 485, 60));
    try std.testing.expectEqual(@as(?PaneHeaderAction, .split_down), bounds.hitHeaderAction(true, 463, 60));
    try std.testing.expectEqual(@as(?PaneHeaderAction, .split_right), bounds.hitHeaderAction(true, 441, 60));
    try std.testing.expectEqual(@as(?PaneHeaderAction, .focus_toggle), bounds.hitHeaderAction(true, 419, 60));
    try std.testing.expectEqual(@as(?PaneHeaderAction, null), bounds.hitHeaderAction(false, 419, 60));
    try std.testing.expectEqual(@as(?PaneHeaderAction, null), bounds.hitHeaderAction(true, 485, 90));
    const narrow: PaneRect = .{ .x = 0, .y = 0, .width = 179, .height = 300 };
    try std.testing.expectEqual(@as(?PaneHeaderAction, null), narrow.hitHeaderAction(true, 165, 14));
}

test "pane header drag area, edge drop zones, and split preview geometry match WPF PaneDrag" {
    const bounds: PaneRect = .{ .x = 100, .y = 50, .width = 400, .height = 300 };
    try std.testing.expect(bounds.isHeaderDragArea(true, 150, 60));
    try std.testing.expect(!bounds.isHeaderDragArea(true, 485, 60));
    try std.testing.expect(!bounds.isHeaderDragArea(true, 150, 85));

    try std.testing.expectEqual(DropZone.left, bounds.dropZoneAt(120, 200));
    try std.testing.expectEqual(DropZone.right, bounds.dropZoneAt(480, 200));
    try std.testing.expectEqual(DropZone.top, bounds.dropZoneAt(300, 70));
    try std.testing.expectEqual(DropZone.bottom, bounds.dropZoneAt(300, 330));
    try std.testing.expectEqual(DropZone.none, bounds.dropZoneAt(300, 200));
    try std.testing.expectEqual(DropZone.none, bounds.dropZoneAt(110, 60)); // top-left corner excluded

    const right_preview = bounds.splitPreviewRect(.right).?;
    try std.testing.expectApproxEqAbs(@as(f32, 332.0), right_preview.x, 0.01);
    try std.testing.expectApproxEqAbs(@as(f32, 54.0), right_preview.y, 0.01);
    try std.testing.expectApproxEqAbs(@as(f32, 164.0), right_preview.width, 0.01);
    try std.testing.expectApproxEqAbs(@as(f32, 292.0), right_preview.height, 0.01);
    try std.testing.expectEqual(@as(?PaneRect, null), bounds.splitPreviewRect(.none));
}

