const std = @import("std");
const paths = @import("path_completion.zig");

/// One lazily started directory-index worker; never reads workspace/session objects or publishes borrowed storage.
pub const PathIndex = struct {
    allocator: std.mem.Allocator,
    thread: ?std.Thread = null,
    mutex: std.Thread.Mutex = .{},
    wake: std.Thread.Condition = .{},
    stopping: bool = false,
    pending: ?Request = null,
    result: ?Result = null,
    latest_revision: std.atomic.Value(u64) = std.atomic.Value(u64).init(0),
    const Request = struct { revision: u64, directory: []u8 };
    pub const Result = struct {
        revision: u64,
        outcome: union(enum) { index: paths.PathCompletion, failure: anyerror },
        pub fn deinit(self: *Result) void {
            if (self.outcome == .index) self.outcome.index.deinit();
        }
    };

    /// Requires a thread-safe allocator; the worker is created only when the picker is first opened.
    pub fn create(allocator: std.mem.Allocator) !*PathIndex {
        const self = try allocator.create(PathIndex);
        errdefer allocator.destroy(self);
        self.* = .{ .allocator = allocator };
        self.thread = try std.Thread.spawn(.{}, run, .{self});
        return self;
    }
    /// Joins outstanding directory IO before freeing the stable worker owner.
    pub fn destroy(self: *PathIndex) void {
        self.mutex.lock();
        self.stopping = true;
        self.latest_revision.store(0, .release);
        self.wake.signal();
        self.mutex.unlock();
        if (self.thread) |thread| thread.join();
        if (self.pending) |pending| self.allocator.free(pending.directory);
        if (self.result) |*result| result.deinit();
        self.allocator.destroy(self);
    }
    /// Coalesces navigation to one latest request; failed allocation leaves the previous request intact.
    pub fn request(self: *PathIndex, directory: []const u8, revision: u64) !void {
        const owned = try self.allocator.dupe(u8, directory);
        self.mutex.lock();
        const previous = self.pending;
        self.pending = .{ .directory = owned, .revision = revision };
        self.latest_revision.store(revision, .release);
        self.wake.signal();
        self.mutex.unlock();
        if (previous) |pending| self.allocator.free(pending.directory);
    }
    /// Transfers a completed owner to the window thread without waiting for scanning to finish.
    pub fn takeResult(self: *PathIndex) ?Result {
        self.mutex.lock();
        defer self.mutex.unlock();
        const result = self.result;
        self.result = null;
        return result;
    }
    fn run(self: *PathIndex) void {
        while (true) {
            self.mutex.lock();
            while (!self.stopping and self.pending == null) self.wake.wait(&self.mutex);
            if (self.stopping) {
                self.mutex.unlock();
                return;
            }
            const request_value = self.pending orelse {
                self.mutex.unlock();
                continue;
            };
            self.pending = null;
            self.mutex.unlock();
            var model = paths.PathCompletion.init(self.allocator);
            model.recursive = true;
            model.scan_revision = .{ .latest = &self.latest_revision, .expected = request_value.revision };
            const outcome: Result = if (model.openDirectory(request_value.directory, "")) blk: {
                model.scan_revision = null;
                break :blk .{ .revision = request_value.revision, .outcome = .{ .index = model } };
            } else |err| blk: {
                model.deinit();
                break :blk .{ .revision = request_value.revision, .outcome = .{ .failure = err } };
            };
            self.allocator.free(request_value.directory);
            var completed = outcome;
            self.mutex.lock();
            if (self.stopping or self.latest_revision.load(.acquire) != completed.revision) {
                self.mutex.unlock();
                completed.deinit();
                continue;
            }
            var previous = self.result;
            self.result = completed;
            self.mutex.unlock();
            if (previous) |*result| result.deinit();
        }
    }
};

test "background path index publishes only owned revisioned results and preserves pending query" {
    var temp = std.testing.tmpDir(.{});
    defer temp.cleanup();
    try temp.dir.makePath("child/deep");
    var file = try temp.dir.createFile("child/deep/future-file.txt", .{});
    file.close();
    const directory = try temp.dir.realpathAlloc(std.testing.allocator, ".");
    defer std.testing.allocator.free(directory);
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkRecursiveIndexAllocations, .{directory});
    const worker = try PathIndex.create(std.testing.allocator);
    defer worker.destroy();
    try worker.request(directory, 1);
    try worker.request(directory, 2);
    var model = paths.PathCompletion.init(std.testing.allocator);
    defer model.deinit();
    model.index = .{ .deferred = .{} };
    model.recursive = true;
    try model.openDirectory(directory, "future-file");
    for (0..500) |_| {
        if (worker.takeResult()) |value| {
            var result = value;
            defer result.deinit();
            if (result.revision != 2) continue;
            switch (result.outcome) {
                .failure => |err| return err,
                .index => |*index| try model.applyDeferredIndex(index),
            }
            try std.testing.expectEqualStrings("future-file", model.query());
            try std.testing.expectEqual(@as(usize, 1), model.matches.items.len);
            try std.testing.expect(std.mem.endsWith(u8, (model.selectedEntry() orelse return error.MissingRecursivePath).name, "future-file.txt"));
            try std.testing.expect(!model.index.deferred.loading);
            return;
        }
        std.Thread.sleep(10 * std.time.ns_per_ms);
    }
    return error.PathIndexTimeout;
}

fn checkRecursiveIndexAllocations(allocator: std.mem.Allocator, directory: []const u8) !void {
    var model = paths.PathCompletion.init(allocator);
    defer model.deinit();
    model.recursive = true;
    try model.openDirectory(directory, "future-file");
    try std.testing.expectEqual(@as(usize, 1), model.matches.items.len);
}
