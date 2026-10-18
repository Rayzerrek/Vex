const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{ .default_target = .{ .cpu_arch = .x86_64, .os_tag = .windows, .abi = .gnu } });
    const optimize = b.option(std.builtin.OptimizeMode, "optimize", "Debug or ReleaseSafe (runtime safety is required)") orelse .ReleaseSafe;
    if (optimize == .ReleaseFast or optimize == .ReleaseSmall) {
        std.debug.print("error: Vex native requires Debug or ReleaseSafe; runtime safety may not be disabled\n", .{});
        std.process.exit(1);
    }
    const module = b.createModule(.{ .root_source_file = b.path("src/main.zig"), .target = target, .optimize = optimize });
    module.addIncludePath(b.path("src/windows"));
    module.addIncludePath(b.path("vendor/include"));
    module.addIncludePath(b.path("src/app"));
    module.addIncludePath(b.path("vendor/lua/src"));
    module.addCSourceFile(.{ .file = b.path("src/app/lua_settings.c"), .flags = &.{ "-std=c11", "-Wall", "-Wextra", "-Werror" } });
    module.addCSourceFiles(.{ .root = b.path("vendor/lua/src"), .files = &.{
        "lapi.c", "lauxlib.c", "lbaselib.c", "lcode.c", "lctype.c", "ldebug.c", "ldo.c", "ldump.c", "lfunc.c", "lgc.c", "llex.c", "lmathlib.c", "lmem.c", "loadlib.c", "lobject.c", "lopcodes.c", "lparser.c", "lstate.c", "lstring.c", "lstrlib.c", "ltable.c", "ltablib.c", "ltm.c", "lundump.c", "lutf8lib.c", "lvm.c", "lzio.c",
    }, .flags = &.{"-std=c11"} });
    const native_flags = &.{ "-std=c++20", "-fno-exceptions", "-fno-rtti", "-Wall", "-Wextra", "-Werror" };
    module.addCSourceFile(.{ .file = b.path("src/windows/windows_window.cpp"), .flags = native_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/windows_chrome.cpp"), .flags = native_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/terminal_drawing.cpp"), .flags = native_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/windows_system.cpp"), .flags = native_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/windows_dialog.cpp"), .flags = native_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/windows_notification.cpp"), .flags = native_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/windows_pty.cpp"), .flags = native_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/windows_conpty.cpp"), .flags = native_flags });
    const renderer_flags: []const []const u8 = if (b.option(bool, "graphics-debug", "Require the installed D3D12 debug layer") orelse false)
        &(native_flags.* ++ .{"-DVEX_GRAPHICS_DEBUG=1"})
    else
        native_flags;
    module.addCSourceFile(.{ .file = b.path("src/windows/windows_renderer.cpp"), .flags = renderer_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/terminal_renderer.cpp"), .flags = native_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/ui_renderer.cpp"), .flags = native_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/app_icon_renderer.cpp"), .flags = native_flags });
    module.link_libc = true;
    for ([_][]const u8{ "user32", "kernel32", "gdi32", "d2d1", "dwrite", "d3d12", "dxgi", "dwmapi", "ole32", "windowscodecs", "uuid", "advapi32", "shell32", "comctl32", "gdiplus", "uxtheme" }) |lib| module.linkSystemLibrary(lib, .{});
    const exe = b.addExecutable(.{ .name = "vex-native", .root_module = module });
    exe.subsystem = .Windows;
    const dll = b.addInstallFileWithDir(b.path("../Vendor/libghostty/ghostty-vt.dll"), .bin, "ghostty-vt.dll");
    const conpty_dll = b.addInstallFileWithDir(b.path("../Vendor/conpty/conpty.dll"), .bin, "conpty.dll");
    const conpty_host = b.addInstallFileWithDir(b.path("../Vendor/conpty/OpenConsole.exe"), .bin, "OpenConsole.exe");
    const app_icon = b.addInstallFileWithDir(b.path("assets/app.ico"), .bin, "app.ico");
    b.installArtifact(exe);
    b.getInstallStep().dependOn(&dll.step);
    b.getInstallStep().dependOn(&conpty_dll.step);
    b.getInstallStep().dependOn(&conpty_host.step);
    b.getInstallStep().dependOn(&app_icon.step);
    const run = b.addSystemCommand(&.{b.getInstallPath(.bin, "vex-native.exe")});
    run.step.dependOn(b.getInstallStep());
    if (b.args) |args| run.addArgs(args);
    b.step("run", "Run the native Windows terminal").dependOn(&run.step);
    const tests = b.addTest(.{ .root_module = module });
    const install_tests = b.addInstallArtifact(tests, .{ .dest_sub_path = "vex-native-tests.exe" });
    const fixture_module = b.createModule(.{ .target = target, .optimize = optimize, .link_libc = true });
    fixture_module.addIncludePath(b.path("src/windows"));
    fixture_module.addCSourceFile(.{ .file = b.path("tests/conpty_fixture.cpp"), .flags = native_flags });
    fixture_module.addCSourceFile(.{ .file = b.path("tests/chrome_motion.cpp"), .flags = native_flags });
    fixture_module.addCSourceFile(.{ .file = b.path("src/windows/windows_conpty.cpp"), .flags = native_flags });
    fixture_module.linkSystemLibrary("kernel32", .{});
    const fixture = b.addExecutable(.{ .name = "vex-conpty-fixture", .root_module = fixture_module });
    const install_fixture = b.addInstallArtifact(fixture, .{});
    const test_build = b.step("test-build", "Compile native tests without running Windows tools");
    test_build.dependOn(&install_tests.step);
    test_build.dependOn(&dll.step);
    test_build.dependOn(&conpty_dll.step);
    test_build.dependOn(&conpty_host.step);
    test_build.dependOn(&install_fixture.step);
    const test_run = b.addSystemCommand(&.{b.getInstallPath(.bin, "vex-native-tests.exe")});
    test_run.step.dependOn(test_build);
    b.step("test", "Check native ownership, terminal tab/split state, behavior and allocation failures").dependOn(&test_run.step);
}
