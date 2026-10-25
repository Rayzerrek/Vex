const std = @import("std");

/// Yields directories before opening them, retaining unreadable paths in the
/// index. Directory links remain navigable, but never recurse into link cycles.
pub const Walker = struct {
    allocator: std.mem.Allocator,
    stack: std.ArrayList(Frame) = .empty,
    path: std.ArrayList(u8) = .empty,
    pending_basename: ?usize = null,
    recursive: bool = true,

    const Frame = struct { iter: std.fs.Dir.Iterator, path_len: usize };
    pub const Entry = struct { path: []const u8, kind: std.fs.Dir.Entry.Kind };

    /// Borrows the root handle; only descendant handles belong to the walker.
    pub fn init(allocator: std.mem.Allocator, root: std.fs.Dir) !Walker {
        var self = Walker{ .allocator = allocator };
        try self.stack.append(allocator, .{ .iter = root.iterate(), .path_len = 0 });
        return self;
    }

    pub fn deinit(self: *Walker) void {
        if (self.stack.items.len > 1) {
            for (self.stack.items[1..]) |*frame| frame.iter.dir.close();
        }
        self.stack.deinit(self.allocator);
        self.path.deinit(self.allocator);
    }

    /// A failed child is already consumed, so callers may resume with next().
    /// Returned path storage remains valid only until the next call.
    pub fn next(self: *Walker) !?Entry {
        if (self.pending_basename) |start| {
            self.pending_basename = null;
            const parent = &self.stack.items[self.stack.items.len - 1];
            var child = try parent.iter.dir.openDir(self.path.items[start..], .{ .iterate = true, .no_follow = true });
            var adopted = false;
            defer if (!adopted) child.close();
            // Windows iterators classify directory reparse points as ordinary
            // directories. Inspect the no-follow handle before traversing them.
            const info = try child.stat();
            if (info.kind == .directory) {
                try self.stack.append(self.allocator, .{ .iter = child.iterate(), .path_len = self.path.items.len });
                adopted = true;
            }
        }
        while (self.stack.items.len > 0) {
            const top = &self.stack.items[self.stack.items.len - 1];
            const entry = top.iter.next() catch |err| {
                var failed = self.stack.pop().?;
                if (self.stack.items.len > 0) failed.iter.dir.close();
                return err;
            };
            const item = entry orelse {
                var finished = self.stack.pop().?;
                if (self.stack.items.len > 0) finished.iter.dir.close();
                continue;
            };
            self.path.shrinkRetainingCapacity(top.path_len);
            if (self.path.items.len > 0) try self.path.append(self.allocator, std.fs.path.sep);
            const basename_start = self.path.items.len;
            try self.path.appendSlice(self.allocator, item.name);
            var kind = item.kind;
            if (kind == .directory) {
                if (self.recursive) self.pending_basename = basename_start;
            } else if (kind == .sym_link) {
                // Detect directory links for Tab/Open without traversing them.
                if (top.iter.dir.openDir(item.name, .{})) |target| {
                    var owned = target;
                    owned.close();
                    kind = .directory;
                } else |_| {}
            }
            return .{ .path = self.path.items, .kind = kind };
        }
        return null;
    }
};

test "directory walker yields directory links without recursing into cycles" {
    var fixture = std.testing.tmpDir(.{ .iterate = true });
    defer fixture.cleanup();
    try fixture.dir.makeDir("child");
    const file = try fixture.dir.createFile("child/file.txt", .{});
    file.close();
    fixture.dir.symLink(".", "loop", .{ .is_directory = true }) catch |err| switch (err) {
        error.AccessDenied => return error.SkipZigTest,
        else => return err,
    };
    var walker = try Walker.init(std.testing.allocator, fixture.dir);
    defer walker.deinit();
    var count: usize = 0;
    var found_link = false;
    var found_file = false;
    while (try walker.next()) |entry| {
        count += 1;
        try std.testing.expect(count <= 3);
        if (std.mem.eql(u8, entry.path, "loop")) {
            found_link = true;
            try std.testing.expectEqual(std.fs.Dir.Entry.Kind.directory, entry.kind);
        }
        found_file = found_file or std.mem.endsWith(u8, entry.path, "file.txt");
    }
    try std.testing.expectEqual(@as(usize, 3), count);
    try std.testing.expect(found_link and found_file);
}
