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

/// Same breadth-first order and interface as `Walker`, but reads directories in
/// parallel chunks. Over the WSL 9P share every directory read is a round trip,
/// so overlapping them is the only way to hide that latency. Entries are still
/// yielded in queue order, which keeps truncation deterministic.
pub const ParallelWalker = struct {
    pub const Entry = Walker.Entry;
    const chunk_dirs = 32;
    const max_threads = 8;

    const DirEntry = struct { name: []const u8, kind: std.fs.Dir.Entry.Kind };
    /// Filled by exactly one worker; the arena owns names and the entry list.
    const DirResult = struct {
        arena: std.heap.ArenaAllocator,
        entries: []DirEntry = &.{},
        err: ?anyerror = null,
    };

    allocator: std.mem.Allocator,
    root: std.fs.Dir,
    root_failed: bool = false,
    probe_symlink_directories: bool = false,
    skip_names: []const []const u8 = Walker.default_skip_names,
    skip_root_names: []const []const u8 = &.{},
    /// Lets an abandoned scan stop between directories instead of finishing the chunk.
    cancel_latest: ?*const std.atomic.Value(u64) = null,
    cancel_expected: u64 = 0,

    /// Owned relative directory paths; the root is the empty path.
    queue: std.ArrayList([]u8) = .empty,
    queue_head: usize = 0,
    started: bool = false,
    chunk_paths: std.ArrayList([]u8) = .empty,
    results: []DirResult = &.{},
    chunk_dir: usize = 0,
    chunk_entry: usize = 0,
    entry_path: std.ArrayList(u8) = .empty,
    cursor: std.atomic.Value(usize) = std.atomic.Value(usize).init(0),

    pub fn init(allocator: std.mem.Allocator, root: std.fs.Dir) ParallelWalker {
        return .{ .allocator = allocator, .root = root };
    }

    pub fn deinit(self: *ParallelWalker) void {
        self.freeChunk();
        for (self.queue.items[self.queue_head..]) |path| self.allocator.free(path);
        self.queue.deinit(self.allocator);
        self.chunk_paths.deinit(self.allocator);
        self.entry_path.deinit(self.allocator);
    }

    fn shouldSkip(self: *const ParallelWalker, name: []const u8, at_root: bool) bool {
        for (self.skip_names) |skip| if (std.mem.eql(u8, name, skip)) return true;
        if (at_root) for (self.skip_root_names) |skip| if (std.mem.eql(u8, name, skip)) return true;
        return false;
    }

    fn freeChunk(self: *ParallelWalker) void {
        for (self.results) |*result| result.arena.deinit();
        if (self.results.len > 0) self.allocator.free(self.results);
        self.results = &.{};
        for (self.chunk_paths.items) |path| self.allocator.free(path);
        self.chunk_paths.clearRetainingCapacity();
        self.chunk_dir = 0;
        self.chunk_entry = 0;
    }

    fn isCancelled(self: *const ParallelWalker) bool {
        const latest = self.cancel_latest orelse return false;
        return latest.load(.acquire) != self.cancel_expected;
    }

    fn scanDirectory(self: *ParallelWalker, relative: []const u8, result: *DirResult) void {
        const arena = result.arena.allocator();
        var opened: ?std.fs.Dir = null;
        defer if (opened) |*child| child.close();
        var dir = self.root;
        if (relative.len > 0) {
            // no_follow: a link is listed by the parent but never entered.
            opened = self.root.openDir(relative, .{ .iterate = true, .no_follow = true }) catch |err| {
                result.err = err;
                return;
            };
            dir = opened.?;
            const info = dir.stat() catch |err| {
                result.err = err;
                return;
            };
            if (info.kind != .directory) return;
        }
        var list: std.ArrayList(DirEntry) = .empty;
        var iterator = dir.iterate();
        while (true) {
            const maybe = iterator.next() catch |err| {
                result.err = err;
                break;
            };
            const item = maybe orelse break;
            var kind = item.kind;
            if (kind == .sym_link and self.probe_symlink_directories) {
                if (dir.openDir(item.name, .{})) |target| {
                    var owned = target;
                    owned.close();
                    kind = .directory;
                } else |_| {}
            }
            const name = arena.dupe(u8, item.name) catch {
                result.err = error.OutOfMemory;
                break;
            };
            list.append(arena, .{ .name = name, .kind = kind }) catch {
                result.err = error.OutOfMemory;
                break;
            };
        }
        result.entries = list.items;
    }

    fn worker(self: *ParallelWalker) void {
        while (true) {
            const index = self.cursor.fetchAdd(1, .monotonic);
            if (index >= self.results.len) return;
            if (self.isCancelled()) {
                self.results[index].err = error.StalePathIndex;
                continue;
            }
            self.scanDirectory(self.chunk_paths.items[index], &self.results[index]);
        }
    }

    /// Returns false when no directories remain.
    fn startChunk(self: *ParallelWalker) !bool {
        if (!self.started) {
            self.started = true;
            const root_path = try self.allocator.alloc(u8, 0);
            errdefer self.allocator.free(root_path);
            try self.queue.append(self.allocator, root_path);
        }
        if (self.queue_head >= self.queue.items.len) {
            self.queue.clearRetainingCapacity();
            self.queue_head = 0;
            return false;
        }
        // The root alone first: its listing is the earliest visible result.
        const count = if (self.chunk_paths.items.len == 0 and self.queue_head == 0) 1 else @min(chunk_dirs, self.queue.items.len - self.queue_head);
        try self.chunk_paths.appendSlice(self.allocator, self.queue.items[self.queue_head .. self.queue_head + count]);
        self.queue_head += count;
        errdefer self.freeChunk();
        self.results = try self.allocator.alloc(DirResult, count);
        for (self.results) |*result| result.* = .{ .arena = std.heap.ArenaAllocator.init(self.allocator) };
        self.cursor.store(0, .monotonic);

        var threads: [max_threads - 1]std.Thread = undefined;
        var spawned: usize = 0;
        const wanted = @min(count, max_threads) - 1;
        while (spawned < wanted) : (spawned += 1) {
            threads[spawned] = std.Thread.spawn(.{}, worker, .{self}) catch break;
        }
        self.worker();
        for (threads[0..spawned]) |thread| thread.join();
        return true;
    }

    /// A failed child is already consumed, so callers may resume with next().
    /// Returned path storage remains valid only until the next call.
    pub fn next(self: *ParallelWalker) !?Entry {
        while (!self.root_failed) {
            if (self.chunk_dir >= self.results.len) {
                self.freeChunk();
                if (!try self.startChunk()) return null;
                continue;
            }
            const result = &self.results[self.chunk_dir];
            const relative = self.chunk_paths.items[self.chunk_dir];
            if (self.chunk_entry >= result.entries.len) {
                const failure = result.err;
                self.chunk_dir += 1;
                self.chunk_entry = 0;
                if (failure) |err| {
                    if (relative.len == 0 and err != error.OutOfMemory) self.root_failed = true;
                    return err;
                }
                continue;
            }
            const item = result.entries[self.chunk_entry];
            self.chunk_entry += 1;
            self.entry_path.clearRetainingCapacity();
            if (relative.len > 0) {
                try self.entry_path.appendSlice(self.allocator, relative);
                try self.entry_path.append(self.allocator, std.fs.path.sep);
            }
            try self.entry_path.appendSlice(self.allocator, item.name);
            if (item.kind == .directory and !self.shouldSkip(item.name, relative.len == 0)) {
                const path = try self.allocator.dupe(u8, self.entry_path.items);
                errdefer self.allocator.free(path);
                try self.queue.append(self.allocator, path);
            }
            return .{ .path = self.entry_path.items, .kind = item.kind };
        }
        return null;
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
