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
    /// Uses the nearest 25% edge band, including corners; the center remains a safe cancellation zone.
    pub fn dropZoneAt(self: PaneRect, x: f32, y: f32) DropZone {
        if (!self.contains(x, y) or self.width <= 0 or self.height <= 0) return .none;
        const rx = (x - self.x) / self.width;
        const ry = (y - self.y) / self.height;
        const edge: f32 = 0.25;
        const horizontal = @min(rx, 1 - rx);
        const vertical = @min(ry, 1 - ry);
        if (horizontal < edge and horizontal <= vertical) return if (rx < 0.5) .left else .right;
        if (vertical < edge) return if (ry < 0.5) .top else .bottom;
        return .none;
    }
    /// Inset preview of the actual 50/50 layout, using the same divider geometry as committed splits.
    pub fn splitPreviewRect(self: PaneRect, zone: DropZone) ?PaneRect {
        if (zone == .none) return null;
        const geometry = splitGeometry(self, if (zone == .left or zone == .right) .right else .down, 0.5);
        var rect = if (zone == .left or zone == .top) geometry.first else geometry.second;
        const inset: f32 = 4;
        if (rect.width <= inset * 2 or rect.height <= inset * 2) return null;
        rect.x += inset;
        rect.y += inset;
        rect.width -= inset * 2;
        rect.height -= inset * 2;
        return rect;
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
        return self.splitPaneOrdered(target, fresh, axis, false);
    }
    /// Places the new leaf before or after the target without changing session ownership.
    pub fn splitPaneOrdered(self: *PaneTree, target: PaneId, fresh: PaneId, axis: SplitAxis, before: bool) !void {
        return self.insertPane(target, fresh, axis, before, @enumFromInt(@intFromEnum(fresh)));
    }
    /// Transfers use a fresh divider token: a moved leaf can still name a divider in its old tree.
    pub fn insertPane(self: *PaneTree, target: PaneId, fresh: PaneId, axis: SplitAxis, before: bool, divider: DividerId) !void {
        if (findLeaf(self.root, fresh, 0) != null) return error.DuplicatePaneId;
        if (findDividerNode(self.root, divider) != null) return error.DuplicateDividerId;
        const found = findLeaf(self.root, target, 0) orelse return error.PaneNotFound;
        if (found.depth >= max_depth) return error.PaneDepthLimit;
        const first = try self.allocator.create(PaneNode);
        errdefer self.allocator.destroy(first);
        const second = try self.allocator.create(PaneNode);
        first.* = if (before) .{ .leaf = fresh } else found.node.*;
        second.* = if (before) found.node.* else .{ .leaf = fresh };
        found.node.* = .{ .split = .{ .id = divider, .axis = axis, .ratio = 0.5, .first = first, .second = second } };
    }
    /// Consumes the other tree on success; one allocation is prepared before either owner changes.
    pub fn insertTree(self: *PaneTree, target: PaneId, other: *PaneTree, zone: DropZone, divider: DividerId) !void {
        const found = findLeaf(self.root, target, 0) orelse return error.PaneNotFound;
        if (zone == .none) return error.InvalidDropZone;
        if (found.depth + 1 + nodeDepth(other.root) > max_depth) return error.PaneDepthLimit;
        if (findDividerNode(self.root, divider) != null or findDividerNode(other.root, divider) != null) return error.DuplicateDividerId;
        const sibling = try self.allocator.create(PaneNode);
        sibling.* = found.node.*;
        const before = zone == .left or zone == .top;
        found.node.* = .{ .split = .{
            .id = divider,
            .axis = if (zone == .left or zone == .right) .right else .down,
            .ratio = 0.5,
            .first = if (before) other.root else sibling,
            .second = if (before) sibling else other.root,
        } };
    }
    /// Moves a leaf beside another leaf while retaining its session and divider identity.
    pub fn movePane(self: *PaneTree, source: PaneId, target: PaneId, zone: DropZone) !bool {
        if (source == target or zone == .none) return false;
        if (!self.containsPane(source) or !self.containsPane(target)) return false;
        const divider = parentDivider(self.root, source) orelse return false;
        const found = findLeaf(self.root, target, 0).?;
        if (found.depth >= max_depth) return error.PaneDepthLimit;
        const first = try self.allocator.create(PaneNode);
        errdefer self.allocator.destroy(first);
        const second = try self.allocator.create(PaneNode);
        const removed = removeLeaf(self.allocator, self.root, source);
        std.debug.assert(removed);
        const destination = findLeaf(self.root, target, 0).?.node;
        const before = zone == .left or zone == .top;
        first.* = if (before) .{ .leaf = source } else destination.*;
        second.* = if (before) destination.* else .{ .leaf = source };
        destination.* = .{ .split = .{ .id = divider, .axis = if (zone == .left or zone == .right) .right else .down, .ratio = 0.5, .first = first, .second = second } };
        return true;
    }
    /// Preserves the final leaf; removing a split promotes its surviving sibling.
    pub fn closePane(self: *PaneTree, id: PaneId) bool {
        return removeLeaf(self.allocator, self.root, id);
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
fn nodeDepth(node: *const PaneNode) usize {
    return switch (node.*) {
        .leaf => 0,
        .split => |split| 1 + @max(nodeDepth(split.first), nodeDepth(split.second)),
    };
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
fn parentDivider(node: *const PaneNode, id: PaneId) ?DividerId {
    return switch (node.*) {
        .leaf => null,
        .split => |split| if ((split.first.* == .leaf and split.first.leaf == id) or (split.second.* == .leaf and split.second.leaf == id)) split.id else parentDivider(split.first, id) orelse parentDivider(split.second, id),
    };
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
    try std.testing.expectError(error.InvalidSplitRatio, tree.setDividerRatio(@enumFromInt(2), std.math.nan(f32)));
    try std.testing.expectError(error.InvalidSplitRatio, tree.setDividerRatio(@enumFromInt(2), std.math.inf(f32)));
    const unchanged = try tree.dividerPlacement(.{ .x = 0, .y = 0, .width = 1006, .height = 600 }, @enumFromInt(2)) orelse return error.TestExpectedDivider;
    try std.testing.expectEqual(@as(f32, 0.5), unchanged.ratio);
    try std.testing.expect(try tree.setDividerRatio(@enumFromInt(2), 2));
    try std.testing.expect(!try tree.setDividerRatio(@enumFromInt(2), 2));
    try std.testing.expect(!try tree.setDividerRatio(@enumFromInt(99), 0.5));
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

test "moving a pane preserves all leaf and divider identities" {
    var tree = try PaneTree.init(std.testing.allocator, @enumFromInt(1));
    defer tree.deinit();
    try tree.splitPane(@enumFromInt(1), @enumFromInt(2), .right);
    try tree.splitPane(@enumFromInt(2), @enumFromInt(3), .down);
    try std.testing.expect(try tree.movePane(@enumFromInt(1), @enumFromInt(3), .bottom));
    try std.testing.expectEqual(@as(usize, 3), tree.paneCount());
    try std.testing.expect(tree.containsPane(@enumFromInt(1)) and tree.containsPane(@enumFromInt(2)) and tree.containsPane(@enumFromInt(3)));
    const bounds: PaneRect = .{ .x = 0, .y = 0, .width = 800, .height = 600 };
    try std.testing.expect((try tree.dividerPlacement(bounds, @enumFromInt(2))) != null);
    try std.testing.expect((try tree.dividerPlacement(bounds, @enumFromInt(3))) != null);
    var placements: std.ArrayList(PanePlacement) = .empty;
    defer placements.deinit(std.testing.allocator);
    try tree.layout(std.testing.allocator, &placements, bounds);
    try std.testing.expectEqual(@as(PaneId, @enumFromInt(3)), placements.items[1].id);
    try std.testing.expectEqual(@as(PaneId, @enumFromInt(1)), placements.items[2].id);
}

test "pane moves reject invalid targets and roll back on allocation failure" {
    for (0..2) |fail_index| {
        var tree = try PaneTree.init(std.testing.allocator, @enumFromInt(1));
        defer tree.deinit();
        try tree.splitPane(@enumFromInt(1), @enumFromInt(2), .right);
        try tree.splitPane(@enumFromInt(2), @enumFromInt(3), .down);
        var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{ .fail_index = fail_index });
        tree.allocator = failing.allocator();
        try std.testing.expectError(error.OutOfMemory, tree.movePane(@enumFromInt(1), @enumFromInt(3), .top));
        tree.allocator = std.testing.allocator;
        try std.testing.expectEqual(@as(usize, 3), tree.paneCount());
        try std.testing.expect(tree.containsPane(@enumFromInt(1)) and tree.containsPane(@enumFromInt(2)) and tree.containsPane(@enumFromInt(3)));
        try std.testing.expect(!try tree.movePane(@enumFromInt(1), @enumFromInt(1), .top));
        try std.testing.expect(!try tree.movePane(@enumFromInt(1), @enumFromInt(9), .top));
        try std.testing.expect(!try tree.movePane(@enumFromInt(1), @enumFromInt(2), .none));
    }
}

test "ordered splits put the fresh pane on the requested side" {
    for ([_]SplitAxis{ .right, .down }) |axis| {
        for ([_]bool{ false, true }) |before| {
            var tree = try PaneTree.init(std.testing.allocator, @enumFromInt(1));
            defer tree.deinit();
            try tree.splitPaneOrdered(@enumFromInt(1), @enumFromInt(2), axis, before);
            var placements: std.ArrayList(PanePlacement) = .empty;
            defer placements.deinit(std.testing.allocator);
            try tree.layout(std.testing.allocator, &placements, .{ .x = 0, .y = 0, .width = 800, .height = 600 });
            try std.testing.expectEqual(@as(PaneId, @enumFromInt(@as(u32, if (before) 2 else 1))), placements.items[0].id);
            try std.testing.expectEqual(@as(PaneId, @enumFromInt(@as(u32, if (before) 1 else 2))), placements.items[1].id);
        }
    }
}

test "pane drag targets include corners and previews match committed split geometry" {
    const bounds: PaneRect = .{ .x = 100, .y = 50, .width = 400, .height = 300 };
    try std.testing.expect(bounds.isHeaderDragArea(true, 150, 60));
    try std.testing.expect(!bounds.isHeaderDragArea(true, 485, 60));
    try std.testing.expect(!bounds.isHeaderDragArea(true, 150, 85));

    try std.testing.expectEqual(DropZone.left, bounds.dropZoneAt(120, 200));
    try std.testing.expectEqual(DropZone.right, bounds.dropZoneAt(480, 200));
    try std.testing.expectEqual(DropZone.top, bounds.dropZoneAt(300, 70));
    try std.testing.expectEqual(DropZone.bottom, bounds.dropZoneAt(300, 330));
    try std.testing.expectEqual(DropZone.none, bounds.dropZoneAt(300, 200));
    try std.testing.expectEqual(DropZone.left, bounds.dropZoneAt(110, 60)); // nearest edge wins corners

    const right_preview = bounds.splitPreviewRect(.right).?;
    try std.testing.expectApproxEqAbs(@as(f32, 307.0), right_preview.x, 0.01);
    try std.testing.expectApproxEqAbs(@as(f32, 54.0), right_preview.y, 0.01);
    try std.testing.expectApproxEqAbs(@as(f32, 189.0), right_preview.width, 0.01);
    try std.testing.expectApproxEqAbs(@as(f32, 292.0), right_preview.height, 0.01);
    try std.testing.expectEqual(@as(?PaneRect, null), bounds.splitPreviewRect(.none));
}
