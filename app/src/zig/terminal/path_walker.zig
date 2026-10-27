const std = @import("std");

/// Breadth-first so shallow, most-searched paths enter a size-capped index before
/// a single deep tree can exhaust it. Directories are yielded before they are
/// opened, retaining unreadable paths in the index. Directory links remain
/// navigable, but never recurse into link cycles.
pub const Walker = struct {
    allocator: std.mem.Allocator,
    root: std.fs.Dir,
    /// Breadth-first traversal needs only one live iterator. Root is borrowed;
    /// after it finishes, each queued child owns its handle until consumed.
    iterator: ?std.fs.Dir.Iterator,
    root_done: bool = false,
    root_failed: bool = false,
    /// Current directory relative to root, and the buffer backing returned entries.
    dir_path: std.ArrayList(u8) = .empty,
    entry_path: std.ArrayList(u8) = .empty,
    /// NUL-separated relative directories awaiting a visit; consumed from queue_head.
    queue: std.ArrayList(u8) = .empty,
    queue_head: usize = 0,
    cancel_latest: ?*const std.atomic.Value(u64) = null,
    cancel_expected: u64 = 0,
    recursive: bool = true,
    /// WSL callers can defer link probing until Open instead of adding a 9P
    /// round-trip for every link during recursive indexing.
    probe_symlink_directories: bool = true,
    /// Directories still listed but never descended into: dependency/VCS trees
    /// would exhaust the entry budget (and, over the WSL 9P share, minutes of IO).
    skip_names: []const []const u8 = default_skip_names,
    /// Same, but only for children of the scan root (e.g. /mnt, /proc in a distro root).
    skip_root_names: []const []const u8 = &.{},

    pub const default_skip_names: []const []const u8 = &.{ ".git", "node_modules", ".cache", ".venv", "__pycache__", ".zig-cache", "zig-cache" };
    pub const wsl_root_skip_names: []const []const u8 = &.{ "mnt", "proc", "sys", "dev", "run", "snap", "lost+found" };

    pub const Entry = struct { path: []const u8, kind: std.fs.Dir.Entry.Kind };

    /// Borrows the root handle; only descendant handles belong to the walker.
    pub fn init(allocator: std.mem.Allocator, root: std.fs.Dir) Walker {
        return .{ .allocator = allocator, .root = root, .iterator = root.iterate() };
    }

    pub fn deinit(self: *Walker) void {
        if (self.root_done) {
            if (self.iterator) |*iter| iter.dir.close();
        }
        self.dir_path.deinit(self.allocator);
        self.entry_path.deinit(self.allocator);
        self.queue.deinit(self.allocator);
    }

    fn shouldSkip(self: *const Walker, name: []const u8, at_root: bool) bool {
        for (self.skip_names) |skip| if (std.mem.eql(u8, name, skip)) return true;
        if (at_root) for (self.skip_root_names) |skip| if (std.mem.eql(u8, name, skip)) return true;
        return false;
    }

    /// Returns false when no directories remain. A directory that cannot be
    /// opened is consumed before the error is returned, so callers may resume.
    fn openNextQueued(self: *Walker) !bool {
        if (self.queue_head >= self.queue.items.len) {
            self.queue.clearRetainingCapacity();
            self.queue_head = 0;
            return false;
        }
        const rest = self.queue.items[self.queue_head..];
        const end = std.mem.indexOfScalar(u8, rest, 0) orelse rest.len;
        const relative = rest[0..end];
        self.queue_head += end + 1;
        self.dir_path.clearRetainingCapacity();
        try self.dir_path.appendSlice(self.allocator, relative);
        var child = try self.root.openDir(relative, .{ .iterate = true, .no_follow = true });
        var adopted = false;
        defer if (!adopted) child.close();
        // Windows iterators classify directory reparse points as ordinary
        // directories. Inspect the no-follow handle before traversing them.
        const info = try child.stat();
        if (info.kind == .directory) {
            self.iterator = child.iterate();
            adopted = true;
        }
        return true;
    }

    /// A failed child is already consumed, so callers may resume with next().
    /// Returned path storage remains valid only until the next call.
    pub fn next(self: *Walker) !?Entry {
        while (!self.root_failed) {
            if (self.cancel_latest) |latest| {
                if (latest.load(.acquire) != self.cancel_expected) return error.StalePathIndex;
            }
            if (self.iterator == null) {
                if (!try self.openNextQueued()) return null;
                continue;
            }
            const in_child = self.root_done;
            const iter = &self.iterator.?;
            const entry = iter.next() catch |err| {
                if (in_child) iter.dir.close() else self.root_failed = true;
                self.iterator = null;
                return err;
            };
            const item = entry orelse {
                if (in_child) iter.dir.close() else self.root_done = true;
                self.iterator = null;
                continue;
            };
            self.entry_path.clearRetainingCapacity();
            if (self.dir_path.items.len > 0 and in_child) {
                try self.entry_path.appendSlice(self.allocator, self.dir_path.items);
                try self.entry_path.append(self.allocator, std.fs.path.sep);
            }
            try self.entry_path.appendSlice(self.allocator, item.name);
            var kind = item.kind;
            if (kind == .directory) {
                if (self.recursive and !self.shouldSkip(item.name, !in_child)) {
                    try self.queue.appendSlice(self.allocator, self.entry_path.items);
                    try self.queue.append(self.allocator, 0);
                }
            } else if (kind == .sym_link and self.probe_symlink_directories) {
                // Detect directory links for Tab/Open without traversing them.
                if (iter.dir.openDir(item.name, .{})) |target| {
                    var owned = target;
                    owned.close();
                    kind = .directory;
                } else |_| {}
            }
            return .{ .path = self.entry_path.items, .kind = kind };
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
    var walker = Walker.init(std.testing.allocator, fixture.dir);
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

test "walker lists but does not descend into skipped directories" {
    var fixture = std.testing.tmpDir(.{ .iterate = true });
    defer fixture.cleanup();
    try fixture.dir.makeDir("node_modules");
    try fixture.dir.makeDir("node_modules/pkg");
    try fixture.dir.makeDir("mnt");
    try fixture.dir.makeDir("mnt/c");
    try fixture.dir.makeDir("src");
    var walker = Walker.init(std.testing.allocator, fixture.dir);
    defer walker.deinit();
    walker.skip_root_names = Walker.wsl_root_skip_names;
    var seen_src = false;
    while (try walker.next()) |entry| {
        try std.testing.expect(!std.mem.eql(u8, entry.path, "node_modules" ++ [_]u8{std.fs.path.sep} ++ "pkg"));
        try std.testing.expect(!std.mem.eql(u8, entry.path, "mnt" ++ [_]u8{std.fs.path.sep} ++ "c"));
        seen_src = seen_src or std.mem.eql(u8, entry.path, "src");
    }
    try std.testing.expect(seen_src);
}

test "walker yields shallow entries before deeper ones" {
    var fixture = std.testing.tmpDir(.{ .iterate = true });
    defer fixture.cleanup();
    try fixture.dir.makeDir("a");
    try fixture.dir.makeDir("a/b");
    const deep = try fixture.dir.createFile("a/b/deep.txt", .{});
    deep.close();
    const shallow = try fixture.dir.createFile("z-shallow.txt", .{});
    shallow.close();
    var walker = Walker.init(std.testing.allocator, fixture.dir);
    defer walker.deinit();
    var shallow_index: ?usize = null;
    var deep_index: ?usize = null;
    var index: usize = 0;
    while (try walker.next()) |entry| : (index += 1) {
        if (std.mem.eql(u8, entry.path, "z-shallow.txt")) shallow_index = index;
        if (std.mem.endsWith(u8, entry.path, "deep.txt")) deep_index = index;
    }
    try std.testing.expect(shallow_index.? < deep_index.?);
}

test "empty walker needs no allocation and leaves the borrowed root open" {
    var fixture = std.testing.tmpDir(.{ .iterate = true });
    defer fixture.cleanup();
    var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{ .fail_index = 0 });
    var walker = Walker.init(failing.allocator(), fixture.dir);
    try std.testing.expectEqual(@as(?Walker.Entry, null), try walker.next());
    walker.deinit();
    try std.testing.expectEqual(@as(usize, 0), failing.allocations);
    _ = try fixture.dir.stat();
}

test "walker resumes after a queued directory disappears" {
    var fixture = std.testing.tmpDir(.{ .iterate = true });
    defer fixture.cleanup();
    try fixture.dir.makeDir("removed");
    try fixture.dir.makeDir("retained");
    const file = try fixture.dir.createFile("retained/file.txt", .{});
    file.close();
    var walker = Walker.init(std.testing.allocator, fixture.dir);
    defer walker.deinit();
    for (0..2) |_| {
        const entry = (try walker.next()).?;
        try std.testing.expectEqual(std.fs.Dir.Entry.Kind.directory, entry.kind);
    }
    try fixture.dir.deleteDir("removed");
    var saw_failure = false;
    var saw_file = false;
    while (true) {
        const entry = walker.next() catch |err| {
            try std.testing.expectEqual(error.FileNotFound, err);
            try std.testing.expect(!walker.root_failed);
            try std.testing.expect(!saw_failure);
            saw_failure = true;
            continue;
        } orelse break;
        saw_file = saw_file or std.mem.endsWith(u8, entry.path, "file.txt");
    }
    try std.testing.expect(saw_failure and saw_file);
}

/// Streaming WSL traversal. Reading whole directories in parallel batches bypasses
/// the indexer's limits and delays cancellation on large directories. Retain this
/// interface while using one iterator so each next() does bounded speculative work.
pub const ParallelWalker = struct {
    pub const Entry = Walker.Entry;
    walker: Walker,
    root_failed: bool = false,
    probe_symlink_directories: bool = false,
    skip_names: []const []const u8 = Walker.default_skip_names,
    skip_root_names: []const []const u8 = &.{},
    cancel_latest: ?*const std.atomic.Value(u64) = null,
    cancel_expected: u64 = 0,

    pub fn init(allocator: std.mem.Allocator, root: std.fs.Dir) ParallelWalker {
        return .{ .walker = Walker.init(allocator, root) };
    }

    pub fn deinit(self: *ParallelWalker) void {
        self.walker.deinit();
    }

    pub fn next(self: *ParallelWalker) !?Entry {
        self.walker.probe_symlink_directories = self.probe_symlink_directories;
        self.walker.skip_names = self.skip_names;
        self.walker.skip_root_names = self.skip_root_names;
        self.walker.cancel_latest = self.cancel_latest;
        self.walker.cancel_expected = self.cancel_expected;
        const entry = self.walker.next() catch |err| {
            self.root_failed = self.walker.root_failed;
            return err;
        };
        self.root_failed = self.walker.root_failed;
        return entry;
    }
};

test "parallel walker matches the sequential walker's breadth-first order" {
    var fixture = std.testing.tmpDir(.{ .iterate = true });
    defer fixture.cleanup();
    try fixture.dir.makeDir("node_modules");
    try fixture.dir.makeDir("node_modules/pkg");
    for (0..40) |i| {
        var name_buf: [16]u8 = undefined;
        const name = try std.fmt.bufPrint(&name_buf, "dir{d}", .{i});
        try fixture.dir.makeDir(name);
        var file_buf: [32]u8 = undefined;
        const file = try fixture.dir.createFile(try std.fmt.bufPrint(&file_buf, "{s}/f.txt", .{name}), .{});
        file.close();
    }
    // Drained one after the other: both walkers share the root handle's directory offset.
    var expected: std.ArrayList([]u8) = .empty;
    defer {
        for (expected.items) |path| std.testing.allocator.free(path);
        expected.deinit(std.testing.allocator);
    }
    {
        var sequential = Walker.init(std.testing.allocator, fixture.dir);
        defer sequential.deinit();
        while (try sequential.next()) |entry| try expected.append(std.testing.allocator, try std.testing.allocator.dupe(u8, entry.path));
    }
    var fresh = try fixture.dir.openDir(".", .{ .iterate = true });
    defer fresh.close();
    var parallel = ParallelWalker.init(std.testing.allocator, fresh);
    defer parallel.deinit();
    var count: usize = 0;
    for (expected.items) |path| {
        const actual = (try parallel.next()).?;
        try std.testing.expectEqualStrings(path, actual.path);
        count += 1;
    }
    try std.testing.expectEqual(@as(?ParallelWalker.Entry, null), try parallel.next());
    try std.testing.expect(count > 80);
}

test "parallel walker reports a vanished directory once and keeps going" {
    var fixture = std.testing.tmpDir(.{ .iterate = true });
    defer fixture.cleanup();
    try fixture.dir.makeDir("removed");
    try fixture.dir.makeDir("retained");
    const file = try fixture.dir.createFile("retained/file.txt", .{});
    file.close();
    var walker = ParallelWalker.init(std.testing.allocator, fixture.dir);
    defer walker.deinit();
    for (0..2) |_| _ = (try walker.next()).?;
    try fixture.dir.deleteDir("removed");
    var failures: usize = 0;
    var saw_file = false;
    while (true) {
        const entry = walker.next() catch |err| {
            try std.testing.expectEqual(error.FileNotFound, err);
            try std.testing.expect(!walker.root_failed);
            failures += 1;
            continue;
        } orelse break;
        saw_file = saw_file or std.mem.endsWith(u8, entry.path, "file.txt");
    }
    try std.testing.expectEqual(@as(usize, 1), failures);
    try std.testing.expect(saw_file);
}

test "parallel walker lists but does not enter skipped directories" {
    var fixture = std.testing.tmpDir(.{ .iterate = true });
    defer fixture.cleanup();
    try fixture.dir.makeDir("node_modules");
    try fixture.dir.makeDir("node_modules/pkg");
    var walker = ParallelWalker.init(std.testing.allocator, fixture.dir);
    defer walker.deinit();
    var listed = false;
    while (try walker.next()) |entry| {
        listed = listed or std.mem.eql(u8, entry.path, "node_modules");
        try std.testing.expect(!std.mem.endsWith(u8, entry.path, "pkg"));
    }
    try std.testing.expect(listed);
}

fn checkParallelAllocations(allocator: std.mem.Allocator, root_path: []const u8) !void {
    var dir = try std.fs.openDirAbsolute(root_path, .{ .iterate = true });
    defer dir.close();
    var walker = ParallelWalker.init(allocator, dir);
    defer walker.deinit();
    while (try walker.next()) |_| {}
}

test "parallel walker releases allocations on failure" {
    var fixture = std.testing.tmpDir(.{ .iterate = true });
    defer fixture.cleanup();
    try fixture.dir.makePath("child/nested");
    (try fixture.dir.createFile("child/file", .{})).close();
    const path = try fixture.dir.realpathAlloc(std.testing.allocator, ".");
    defer std.testing.allocator.free(path);
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkParallelAllocations, .{path});
}

test "parallel walker does not descend into directory links" {
    var fixture = std.testing.tmpDir(.{ .iterate = true });
    defer fixture.cleanup();
    try fixture.dir.makeDir("child");
    (try fixture.dir.createFile("child/file", .{})).close();
    fixture.dir.symLink(".", "loop", .{ .is_directory = true }) catch |err| switch (err) {
        error.AccessDenied => return error.SkipZigTest,
        else => return err,
    };
    var walker = ParallelWalker.init(std.testing.allocator, fixture.dir);
    defer walker.deinit();
    var count: usize = 0;
    while (try walker.next()) |_| {
        count += 1;
        try std.testing.expect(count <= 3);
    }
    try std.testing.expectEqual(@as(usize, 3), count);
}

test "streaming walker stops immediately on cancellation" {
    var fixture = std.testing.tmpDir(.{ .iterate = true });
    defer fixture.cleanup();
    for (0..128) |i| {
        var name: [32]u8 = undefined;
        (try fixture.dir.createFile(try std.fmt.bufPrint(&name, "file-{d}", .{i}), .{})).close();
    }
    var revision = std.atomic.Value(u64).init(1);
    var walker = ParallelWalker.init(std.testing.allocator, fixture.dir);
    defer walker.deinit();
    walker.cancel_latest = &revision;
    walker.cancel_expected = 1;
    _ = (try walker.next()).?;
    revision.store(2, .release);
    try std.testing.expectError(error.StalePathIndex, walker.next());
    revision.store(1, .release);
    try std.testing.expect((try walker.next()) != null);
}

test "streaming walker does not buffer a large directory" {
    var fixture = std.testing.tmpDir(.{ .iterate = true });
    defer fixture.cleanup();
    for (0..256) |i| {
        var name: [32]u8 = undefined;
        (try fixture.dir.createFile(try std.fmt.bufPrint(&name, "file-{d}", .{i}), .{})).close();
    }
    var storage: [256]u8 = undefined;
    var fixed = std.heap.FixedBufferAllocator.init(&storage);
    var walker = ParallelWalker.init(fixed.allocator(), fixture.dir);
    defer walker.deinit();
    var count: usize = 0;
    while (try walker.next()) |_| count += 1;
    try std.testing.expectEqual(@as(usize, 256), count);
}
