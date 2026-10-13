const std = @import("std");

/// Stable pane identity; IDs are never reused while native messages may be queued.
pub const PaneId = enum(u32) { _ };
/// Matches WPF Horizontal (right) and Vertical (down) split orientation.
pub const SplitAxis = enum { right, down };
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
};
/// Layout result holds a stable identity rather than a pointer into a mutable tree.
pub const PanePlacement = struct { id: PaneId, bounds: PaneRect };

const PaneNode = union(enum) {
    leaf: PaneId,
    split: struct { axis: SplitAxis, ratio: f32, first: *PaneNode, second: *PaneNode },
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
        found.node.* = .{ .split = .{ .axis = axis, .ratio = 0.5, .first = first, .second = second } };
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
fn appendPlacement(node: *const PaneNode, placements: *std.ArrayList(PanePlacement), bounds: PaneRect) void {
    switch (node.*) {
        .leaf => |id| placements.appendAssumeCapacity(.{ .id = id, .bounds = bounds }),
        .split => |split| {
            var first = bounds;
            var second = bounds;
            switch (split.axis) {
                .right => {
                    const gap = @min(PaneTree.divider_width, bounds.width);
                    const available = bounds.width - gap;
                    first.width = available * split.ratio;
                    second.x += first.width + gap;
                    second.width = available - first.width;
                },
                .down => {
                    const gap = @min(PaneTree.divider_width, bounds.height);
                    const available = bounds.height - gap;
                    first.height = available * split.ratio;
                    second.y += first.height + gap;
                    second.height = available - first.height;
                },
            }
            appendPlacement(split.first, placements, first);
            appendPlacement(split.second, placements, second);
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
