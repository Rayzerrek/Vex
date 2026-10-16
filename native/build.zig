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
    const native_flags = &.{ "-std=c++20", "-fno-exceptions", "-fno-rtti", "-Wall", "-Wextra", "-Werror" };
    module.addCSourceFile(.{ .file = b.path("src/windows/windows_window.cpp"), .flags = native_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/windows_chrome.cpp"), .flags = native_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/terminal_drawing.cpp"), .flags = native_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/windows_system.cpp"), .flags = native_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/windows_dialog.cpp"), .flags = native_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/windows_pty.cpp"), .flags = native_flags });
    const renderer_flags: []const []const u8 = if (b.option(bool, "graphics-debug", "Require the installed D3D12 debug layer") orelse false)
        &(native_flags.* ++ .{"-DVEX_GRAPHICS_DEBUG=1"})
    else
        native_flags;
    module.addCSourceFile(.{ .file = b.path("src/windows/windows_renderer.cpp"), .flags = renderer_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/terminal_renderer.cpp"), .flags = native_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/ui_renderer.cpp"), .flags = native_flags });
    module.addCSourceFile(.{ .file = b.path("src/windows/app_icon_renderer.cpp"), .flags = native_flags });
    module.link_libc = true;
    for ([_][]const u8{ "user32", "kernel32", "gdi32", "d2d1", "dwrite", "d3d12", "dxgi", "dwmapi", "ole32", "windowscodecs", "uuid", "advapi32", "shell32", "comctl32" }) |lib| module.linkSystemLibrary(lib, .{});
    const exe = b.addExecutable(.{ .name = "vex-native", .root_module = module });
    exe.subsystem = .Windows;
    const dll = b.addInstallFileWithDir(b.path("../Vendor/libghostty/ghostty-vt.dll"), .bin, "ghostty-vt.dll");
    const app_icon = b.addInstallFileWithDir(b.path("assets/app.ico"), .bin, "app.ico");
    b.installArtifact(exe);
    b.getInstallStep().dependOn(&dll.step);
    b.getInstallStep().dependOn(&app_icon.step);
    const cmd_integration = b.addInstallFileWithDir(b.path("src/shell/integration/Vex.cmd"), .bin, "ShellIntegration/Vex.cmd");
    const nu_integration = b.addInstallFileWithDir(b.path("src/shell/integration/Vex.nu"), .bin, "ShellIntegration/Vex.nu");
    b.getInstallStep().dependOn(&cmd_integration.step);
    b.getInstallStep().dependOn(&nu_integration.step);
    const run = b.addSystemCommand(&.{b.getInstallPath(.bin, "vex-native.exe")});
    run.step.dependOn(b.getInstallStep());
    if (b.args) |args| run.addArgs(args);
    b.step("run", "Run the native Windows terminal").dependOn(&run.step);
    const tests = b.addTest(.{ .root_module = module });
    const install_tests = b.addInstallArtifact(tests, .{ .dest_sub_path = "vex-native-tests.exe" });
    const test_run = b.addSystemCommand(&.{b.getInstallPath(.bin, "vex-native-tests.exe")});
    test_run.step.dependOn(&install_tests.step);
    test_run.step.dependOn(&dll.step);
    const check_icons = b.addSystemCommand(&.{ "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File" });
    check_icons.addFileArg(b.path("tools/Generate-AppIconGeometry.ps1"));
    check_icons.addArg("-Check");
    test_run.step.dependOn(&check_icons.step);
    b.step("test", "Check native ownership, terminal tab/split state, behavior and allocation failures").dependOn(&test_run.step);
}
