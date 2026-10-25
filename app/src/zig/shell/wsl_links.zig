const std = @import("std");
const builtin = @import("builtin");
const paths = @import("wsl_paths.zig");

extern "kernel32" fn GetSystemDirectoryW(buffer: [*]u16, size: u32) callconv(.winapi) u32;

/// Windows 10's WSL share can list Linux links without allowing Windows to follow
/// them. Resolve only an explicitly opened link, never every link in an index.
/// A failed/file/broken link leaves Tab free to insert the original filename.
pub fn directoryTargetAlloc(allocator: std.mem.Allocator, network_path: []const u8) !?[]u8 {
    if (builtin.os.tag != .windows) return null;
    const distro = (try paths.networkDistribution(network_path)) orelse return null;
    const linux = try paths.linuxDirectoryFromNetworkAlloc(allocator, network_path, distro);
    defer allocator.free(linux);
    var system_buf: [260]u16 = undefined;
    const system_len = GetSystemDirectoryW(&system_buf, system_buf.len);
    if (system_len == 0 or system_len >= system_buf.len) return null;
    const system = try std.unicode.utf16LeToUtf8Alloc(allocator, system_buf[0..system_len]);
    defer allocator.free(system);
    const executable = try std.fs.path.join(allocator, &.{ system, "wsl.exe" });
    defer allocator.free(executable);
    // The filename is a positional argument, not interpolated shell source.
    var child = std.process.Child.init(&.{
        executable,                                         "--distribution", distro.bytes, "--exec", "/bin/sh", "-c",
        "if [ -d \"$1\" ]; then readlink -f -- \"$1\"; fi", "vex-path-link",  linux,
    }, allocator);
    child.create_no_window = true;
    child.stdin_behavior = .Ignore;
    child.stdout_behavior = .Pipe;
    child.stderr_behavior = .Ignore;
    child.spawn() catch |err| switch (err) {
        error.OutOfMemory => return err,
        else => return null,
    };
    var waited = false;
    defer if (!waited) {
        _ = child.kill() catch {};
    };
    // Bound a user-triggered fallback even when the distro is unavailable. Linux
    // directory paths fit in the pipe buffer, so waiting before reading is safe.
    std.os.windows.WaitForSingleObject(child.id, 3000) catch return null;
    var output: [8192]u8 = undefined;
    const len = child.stdout.?.readAll(&output) catch return null;
    const term = child.wait() catch return null;
    waited = true;
    if (term != .Exited or term.Exited != 0 or len == output.len) return null;
    const target = parseDirectoryTarget(output[0..len]) orelse return null;
    return try paths.networkDirectoryFromLinuxAlloc(allocator, distro, target);
}

fn parseDirectoryTarget(output: []const u8) ?paths.LinuxDirectory {
    if (output.len < 2 or output[output.len - 1] != '\n') return null;
    return paths.LinuxDirectory.parse(output[0 .. output.len - 1]) catch null;
}

test "WSL link targets require a single safe absolute Linux directory" {
    try std.testing.expectEqualStrings("/tmp/space ' ?", (parseDirectoryTarget("/tmp/space ' ?\n") orelse return error.MissingTarget).bytes);
    try std.testing.expectEqualStrings("/", (parseDirectoryTarget("/\n") orelse return error.MissingTarget).bytes);
    for ([_][]const u8{ "", "\n", "relative\n", "/missing-terminator", "/one\n/two\n", "/bad\x00path\n" }) |output| {
        try std.testing.expect(parseDirectoryTarget(output) == null);
    }
}
