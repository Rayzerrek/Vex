//! Portable microbenchmarks; not a substitute for Windows startup/desktop measurements.
const std = @import("std");
const Snapshot = @import("app/copy_feedback.zig").Snapshot;

const Cell = struct {
    width: enum { narrow, wide, tail } = .narrow,
    text_start: usize,
    text_length: usize = 1,
};
const Frame = struct {
    columns: u16 = 160,
    rows: u16 = 60,
    generation: u64 = 1,
    cells: struct { items: []const Cell },
    utf8: struct { items: []const u8 },
};

pub fn main() !void {
    var cells: [160 * 60]Cell = undefined;
    const text: [160 * 60]u8 = @splat('x');
    for (&cells, 0..) |*cell, index| cell.* = .{ .text_start = index };
    const frame: Frame = .{ .cells = .{ .items = &cells }, .utf8 = .{ .items = &text } };
    var snapshot: Snapshot = .{};
    defer snapshot.deinit(std.heap.smp_allocator);
    try snapshot.captureVersioned(std.heap.smp_allocator, frame, 0);
    const iterations = 100_000;
    var timer = try std.time.Timer.start();
    for (0..iterations) |i| {
        try snapshot.capture(std.heap.smp_allocator, frame, @intCast(i));
        std.mem.doNotOptimizeAway(snapshot.cells.items.ptr);
    }
    const full_ns = timer.lap();
    for (0..iterations) |i| {
        try snapshot.captureVersioned(std.heap.smp_allocator, frame, @intCast(i));
        std.mem.doNotOptimizeAway(&snapshot);
    }
    const cached_ns = timer.lap();
    std.debug.print("copy snapshot (160x60, unchanged): full {d} ns/input, versioned {d} ns/input ({d:.1}x)\n", .{ full_ns / iterations, cached_ns / iterations, @as(f64, @floatFromInt(full_ns)) / @as(f64, @floatFromInt(cached_ns)) });
    var debug: std.heap.DebugAllocator(.{}) = .init;
    defer _ = debug.deinit();
    try allocatorBenchmark("DebugAllocator", debug.allocator());
    try allocatorBenchmark("smp_allocator", std.heap.smp_allocator);
    try feedBenchmark("plain", "ordinary terminal output 0123456789\r\n" ** 1800);
    try feedBenchmark("ANSI", "\x1b[32m" ++ "ordinary terminal output 0123456789\r\n" ** 1800 ++ "\x1b[0m");
    try feedBenchmark("dense ANSI", "\x1b[32mhello\x1b[0m cost $5 C++ file/path\r\n" ** 1800);
    try feedBenchmark("plain symbols", "cost $5 C++ file/path\r\n" ** 3000);
}

const FeedOwner = struct {
    bytes: usize = 0,
    pub fn writeTerminalOutput(self: *FeedOwner, bytes: []const u8) !void {
        self.bytes +%= bytes.len;
        std.mem.doNotOptimizeAway(bytes.ptr);
    }
    pub fn onTerminalReset(_: *FeedOwner) void {}
    pub fn onProgramStatusOsc(_: *FeedOwner, _: []const u8) !void {}
    pub fn onShellMarkerOsc(_: *FeedOwner, _: []const u8) !void {}
    pub fn onProgressOsc(_: *FeedOwner, _: []const u8) void {}
};

fn feedBenchmark(name: []const u8, bytes: []const u8) !void {
    var filter: @import("terminal/terminal_feed.zig").TerminalFeedFilter = .{};
    var owner: FeedOwner = .{};
    var timer = try std.time.Timer.start();
    for (0..10_000) |_| try filter.feedTerminalOutput(&owner, bytes);
    std.mem.doNotOptimizeAway(owner.bytes);
    const seconds = @as(f64, @floatFromInt(timer.read())) / std.time.ns_per_s;
    std.debug.print("{s} feed filter: {d:.1} MiB/s\n", .{ name, @as(f64, @floatFromInt(bytes.len * 10_000)) / (1024 * 1024) / seconds });
}

fn allocatorBenchmark(name: []const u8, allocator: std.mem.Allocator) !void {
    const iterations = 100_000;
    var timer = try std.time.Timer.start();
    for (0..iterations) |_| {
        const bytes = try allocator.alloc(u8, 128);
        @memset(bytes, 42);
        std.mem.doNotOptimizeAway(bytes.ptr);
        allocator.free(bytes);
    }
    std.debug.print("{s}: {d} ns/alloc+free (128 bytes)\n", .{ name, timer.read() / iterations });
}

test {
    _ = @import("app/copy_feedback.zig");
    _ = @import("terminal/terminal_feed.zig");
}
