const std = @import("std");
const wsl_paths = @import("../shell/wsl_paths.zig");

pub const ShellQuoteKind = enum {
    pwsh,
    cmd,
    nu,
    gitbash,
    wsl,

    pub fn fromProgram(program: []const u8) ShellQuoteKind {
        const base = std.fs.path.basename(program);
        const stem = if (std.ascii.endsWithIgnoreCase(base, ".exe")) base[0 .. base.len - 4] else base;
        if (std.ascii.eqlIgnoreCase(stem, "pwsh") or std.ascii.eqlIgnoreCase(stem, "powershell")) return .pwsh;
        if (std.ascii.eqlIgnoreCase(stem, "nu")) return .nu;
        if (std.ascii.eqlIgnoreCase(stem, "bash") or std.ascii.eqlIgnoreCase(stem, "git-bash")) return .gitbash;
        if (std.ascii.eqlIgnoreCase(stem, "wsl")) return .wsl;
        return .cmd;
    }
};

pub const Entry = struct {
    name: []u8,
    is_directory: bool,
};

pub const Match = struct {
    entry_index: usize,
    score: i32,
};

pub const max_entries: usize = 16_384;
pub const max_matches: usize = 50;

pub const PathCompletion = struct {
    allocator: std.mem.Allocator,
    directory: []u8 = &.{},
    query_buf: [128]u8 = undefined,
    query_len: usize = 0,
    entries: std.ArrayList(Entry) = .empty,
    matches: std.ArrayList(Match) = .empty,
    selected_index: usize = 0,
    hovered_index: ?usize = null,
    first_visible: usize = 0,
    scan_error: bool = false,
    is_truncated: bool = false,

    pub fn init(allocator: std.mem.Allocator) PathCompletion {
        return .{ .allocator = allocator };
    }

    pub fn deinit(self: *PathCompletion) void {
        self.clearEntries();
        self.entries.deinit(self.allocator);
        self.matches.deinit(self.allocator);
        if (self.directory.len > 0) self.allocator.free(self.directory);
        self.* = .{ .allocator = self.allocator };
    }

    fn clearEntries(self: *PathCompletion) void {
        for (self.entries.items) |entry| {
            self.allocator.free(entry.name);
        }
        self.entries.clearRetainingCapacity();
    }

    pub fn query(self: *const PathCompletion) []const u8 {
        return self.query_buf[0..self.query_len];
    }

    pub fn openDirectory(self: *PathCompletion, dir_path: []const u8, initial_query: []const u8) !void {
        const trimmed = std.mem.trimRight(u8, dir_path, "\\/");
        const effective = if (trimmed.len == 0 and dir_path.len > 0) dir_path else if (trimmed.len == 2 and trimmed[1] == ':') dir_path[0..3] else trimmed;
        const owned_dir = try self.allocator.dupe(u8, effective);
        if (self.directory.len > 0) self.allocator.free(self.directory);
        self.directory = owned_dir;

        const q_len = @min(initial_query.len, self.query_buf.len);
        @memcpy(self.query_buf[0..q_len], initial_query[0..q_len]);
        self.query_len = q_len;
        self.selected_index = 0;
        self.hovered_index = null;
        self.first_visible = 0;

        try self.scanCurrentDirectory();
        try self.refreshMatches();
    }

    pub fn navigateParent(self: *PathCompletion) !bool {
        if (self.directory.len == 0) return false;
        const parent = std.fs.path.dirname(self.directory) orelse return false;
        if (std.mem.eql(u8, parent, self.directory)) return false;
        const keep_query = try self.allocator.dupe(u8, self.query());
        defer self.allocator.free(keep_query);
        try self.openDirectory(parent, keep_query);
        return true;
    }

    pub fn enterSelectedDirectory(self: *PathCompletion) !bool {
        const entry = self.selectedEntry() orelse return false;
        if (!entry.is_directory) return false;
        const child = try std.fs.path.join(self.allocator, &.{ self.directory, entry.name });
        defer self.allocator.free(child);
        try self.openDirectory(child, "");
        return true;
    }

    pub fn selectedEntry(self: *const PathCompletion) ?Entry {
        if (self.selected_index >= self.matches.items.len) return null;
        const idx = self.matches.items[self.selected_index].entry_index;
        if (idx >= self.entries.items.len) return null;
        return self.entries.items[idx];
    }

    pub fn selectedFullPathAlloc(self: *const PathCompletion, allocator: std.mem.Allocator) !?[]u8 {
        const entry = self.selectedEntry() orelse return null;
        return try std.fs.path.join(allocator, &.{ self.directory, entry.name });
    }

    pub fn appendQuery(self: *PathCompletion, text: []const u8) !void {
        for (text) |c| {
            if (c < 0x20 or c == 0x7f) return;
        }
        const avail = self.query_buf.len - self.query_len;
        const copy_len = @min(avail, text.len);
        if (copy_len == 0) return;
        @memcpy(self.query_buf[self.query_len .. self.query_len + copy_len], text[0..copy_len]);
        self.query_len += copy_len;

        const current_q = self.query();
        if (std.mem.lastIndexOfAny(u8, current_q, "/\\")) |sep| {
            const prefix = current_q[0 .. sep + 1];
            const remainder = current_q[sep + 1 ..];
            if (self.resolveSubdirectoryAlloc(prefix)) |resolved| {
                defer self.allocator.free(resolved);
                var rem_buf: [128]u8 = undefined;
                const rem_len = remainder.len;
                @memcpy(rem_buf[0..rem_len], remainder);
                try self.openDirectory(resolved, rem_buf[0..rem_len]);
                return;
            } else |_| {}
        }

        self.selected_index = 0;
        self.first_visible = 0;
        try self.refreshMatches();
    }

    pub fn backspaceQuery(self: *PathCompletion) !bool {
        if (self.query_len == 0) {
            return try self.navigateParent();
        }
        var i = self.query_len - 1;
        while (i > 0 and (self.query_buf[i] & 0xC0) == 0x80) : (i -= 1) {}
        self.query_len = i;
        self.selected_index = 0;
        self.first_visible = 0;
        try self.refreshMatches();
        return true;
    }

    pub fn clearQuery(self: *PathCompletion) !void {
        if (self.query_len == 0) return;
        self.query_len = 0;
        self.selected_index = 0;
        self.first_visible = 0;
        try self.refreshMatches();
    }

    pub fn moveSelection(self: *PathCompletion, delta: isize, visible_rows: usize) void {
        if (self.matches.items.len == 0) {
            self.selected_index = 0;
            self.first_visible = 0;
            return;
        }
        const max_idx: isize = @intCast(self.matches.items.len - 1);
        const cur: isize = @intCast(self.selected_index);
        const next: usize = @intCast(std.math.clamp(cur + delta, 0, max_idx));
        self.selected_index = next;
        self.ensureVisible(visible_rows);
    }

    pub fn ensureVisible(self: *PathCompletion, visible_rows: usize) void {
        const rows = @max(@as(usize, 1), visible_rows);
        if (self.selected_index < self.first_visible) {
            self.first_visible = self.selected_index;
        } else if (self.selected_index >= self.first_visible + rows) {
            self.first_visible = self.selected_index - rows + 1;
        }
        if (self.matches.items.len > rows) {
            self.first_visible = @min(self.first_visible, self.matches.items.len - rows);
        } else {
            self.first_visible = 0;
        }
    }

    fn resolveSubdirectoryAlloc(self: *const PathCompletion, prefix: []const u8) ![]u8 {
        var normalized_buf: [512]u8 = undefined;
        if (prefix.len > normalized_buf.len) return error.NameTooLong;
        for (prefix, 0..) |c, idx| {
            normalized_buf[idx] = if (c == '/') '\\' else c;
        }
        const norm = normalized_buf[0..prefix.len];
        const candidate = try std.fs.path.resolve(self.allocator, &.{ self.directory, norm });
        errdefer self.allocator.free(candidate);
        var dir = try std.fs.openDirAbsolute(candidate, .{});
        dir.close();
        return candidate;
    }

    fn scanCurrentDirectory(self: *PathCompletion) !void {
        self.clearEntries();
        self.scan_error = false;
        self.is_truncated = false;
        if (self.directory.len == 0) return;

        var dir = std.fs.openDirAbsolute(self.directory, .{ .iterate = true }) catch {
            self.scan_error = true;
            return;
        };
        defer dir.close();

        var it = dir.iterate();
        while (true) {
            const next_entry = it.next() catch {
                self.scan_error = self.entries.items.len == 0;
                break;
            };
            const item = next_entry orelse break;
            if (item.name.len == 0) continue;
            const is_dir = item.kind == .directory;
            const owned_name = try self.allocator.dupe(u8, item.name);
            errdefer self.allocator.free(owned_name);
            try self.entries.append(self.allocator, .{
                .name = owned_name,
                .is_directory = is_dir,
            });
            if (self.entries.items.len >= max_entries) {
                self.is_truncated = true;
                break;
            }
        }
    }

    pub fn refreshMatches(self: *PathCompletion) !void {
        self.matches.clearRetainingCapacity();
        const q = self.query();
        for (self.entries.items, 0..) |entry, idx| {
            const score = scorePath(entry.name, entry.is_directory, q) orelse continue;
            if (self.matches.items.len < max_matches) {
                try self.matches.append(self.allocator, .{ .entry_index = idx, .score = score });
            } else {
                var worst_pos: usize = 0;
                var worst_score = self.matches.items[0].score;
                var worst_entry_idx = self.matches.items[0].entry_index;
                for (self.matches.items[1..], 1..) |m, m_pos| {
                    if (m.score < worst_score or (m.score == worst_score and m.entry_index > worst_entry_idx)) {
                        worst_pos = m_pos;
                        worst_score = m.score;
                        worst_entry_idx = m.entry_index;
                    }
                }
                if (score > worst_score) {
                    self.matches.items[worst_pos] = .{ .entry_index = idx, .score = score };
                }
            }
        }

        const Context = struct {
            entries: []const Entry,
            pub fn lessThan(ctx: @This(), a: Match, b: Match) bool {
                if (a.score != b.score) return a.score > b.score;
                const na = ctx.entries[a.entry_index].name;
                const nb = ctx.entries[b.entry_index].name;
                return std.ascii.lessThanIgnoreCase(na, nb);
            }
        };
        std.mem.sort(Match, self.matches.items, Context{ .entries = self.entries.items }, Context.lessThan);
        if (self.selected_index >= self.matches.items.len) {
            self.selected_index = 0;
        }
    }
};

fn normalizeQueryChar(c: u8) u8 {
    if (c == '/') return '\\';
    return std.ascii.toLower(c);
}

pub fn scorePath(relative_path: []const u8, is_directory: bool, query: []const u8) ?i32 {
    var filename_offset: usize = 0;
    if (std.mem.lastIndexOfAny(u8, relative_path, "\\/")) |sep| {
        filename_offset = sep + 1;
    }
    if (query.len == 0) {
        if (filename_offset != 0) return null;
        return if (is_directory) 100 else 0;
    }

    const name_score = scoreSubsequence(relative_path, query, filename_offset);
    var best: i32 = if (name_score) |ns|
        ns + 80
    else if (filename_offset > 0)
        (scoreSubsequence(relative_path, query, 0) orelse return null)
    else
        return null;

    const filename = relative_path[filename_offset..];
    if (startsWithNormalized(filename, query)) {
        best += 160;
        if (filename.len == query.len) {
            best += 320;
        }
    }
    if (is_directory) best += 10;
    return best;
}

fn startsWithNormalized(haystack: []const u8, query: []const u8) bool {
    if (haystack.len < query.len) return false;
    for (query, 0..) |qc, i| {
        if (normalizeQueryChar(haystack[i]) != normalizeQueryChar(qc)) return false;
    }
    return true;
}

fn scoreSubsequence(path: []const u8, query: []const u8, start: usize) ?i32 {
    var previous: i32 = -2;
    var score: i32 = 0;
    var offset: usize = start;

    for (query, 0..) |raw_qc, q_idx| {
        const qc = normalizeQueryChar(raw_qc);
        var found_index: ?usize = null;
        var idx = offset;
        while (idx < path.len) : (idx += 1) {
            if (normalizeQueryChar(path[idx]) == qc) {
                found_index = idx;
                break;
            }
        }
        var i = found_index orelse return null;
        const is_contiguous = previous >= 0 and @as(i32, @intCast(i)) == previous + 1;
        if (!is_contiguous and !isWordStart(path, i, start)) {
            var scan = i + 1;
            while (scan < path.len) : (scan += 1) {
                if (normalizeQueryChar(path[scan]) == qc and
                    isWordStart(path, scan, start) and
                    hasSubsequence(path, query[q_idx + 1 ..], scan + 1))
                {
                    i = scan;
                    break;
                }
            }
        }
        const i_i32: i32 = @intCast(i);
        score += 20;
        if (i_i32 == previous + 1) score += 24;
        if (i == start or isBoundaryChar(path[i - 1])) score += 32;
        if (i > 0 and std.ascii.isLower(path[i - 1]) and std.ascii.isUpper(path[i])) score += 28;
        if (previous >= 0) {
            score -= @min(@as(i32, 12), i_i32 - previous - 1);
        }
        previous = i_i32;
        offset = i + 1;
    }
    const tail_penalty: i32 = @intCast((path.len - start) / 4);
    return @max(@as(i32, 0), score - tail_penalty);
}

fn hasSubsequence(path: []const u8, query: []const u8, start: usize) bool {
    var offset = start;
    for (query) |raw_qc| {
        const qc = normalizeQueryChar(raw_qc);
        var found: ?usize = null;
        var idx = offset;
        while (idx < path.len) : (idx += 1) {
            if (normalizeQueryChar(path[idx]) == qc) {
                found = idx;
                break;
            }
        }
        offset = (found orelse return false) + 1;
    }
    return true;
}

fn isWordStart(path: []const u8, i: usize, start: usize) bool {
    if (i <= start) return true;
    if (isBoundaryChar(path[i - 1])) return true;
    return std.ascii.isLower(path[i - 1]) and std.ascii.isUpper(path[i]);
}

fn isBoundaryChar(c: u8) bool {
    return switch (c) {
        '\\', '/', '-', '_', '.', ' ' => true,
        else => false,
    };
}

pub fn formatPathAlloc(
    allocator: std.mem.Allocator,
    full_path: []const u8,
    shell_kind: ShellQuoteKind,
    raw: bool,
) ![]u8 {
    for (full_path) |c| {
        if (c < 0x20 or c == 0x7f) return error.ControlCharacterInPath;
    }

    var converted = try allocator.dupe(u8, full_path);
    defer allocator.free(converted);

    if (shell_kind == .wsl) {
        if (wsl_paths.networkDistribution(full_path) catch null) |distro| {
            if (wsl_paths.linuxDirectoryFromNetworkAlloc(allocator, full_path, distro) catch null) |linux_path| {
                allocator.free(converted);
                converted = linux_path;
            }
        } else if (full_path.len >= 3 and std.ascii.isAlphabetic(full_path[0]) and full_path[1] == ':' and (full_path[2] == '\\' or full_path[2] == '/')) {
            var list: std.ArrayList(u8) = .empty;
            errdefer list.deinit(allocator);
            try list.appendSlice(allocator, "/mnt/");
            try list.append(allocator, std.ascii.toLower(full_path[0]));
            for (full_path[2..]) |ch| {
                try list.append(allocator, if (ch == '\\') '/' else ch);
            }
            allocator.free(converted);
            converted = try list.toOwnedSlice(allocator);
        }
    } else if (shell_kind == .gitbash) {
        var list: std.ArrayList(u8) = .empty;
        errdefer list.deinit(allocator);
        if (full_path.len >= 3 and std.ascii.isAlphabetic(full_path[0]) and full_path[1] == ':' and (full_path[2] == '\\' or full_path[2] == '/')) {
            try list.append(allocator, '/');
            try list.append(allocator, std.ascii.toLower(full_path[0]));
            for (full_path[2..]) |ch| {
                try list.append(allocator, if (ch == '\\') '/' else ch);
            }
        } else {
            for (full_path) |ch| {
                try list.append(allocator, if (ch == '\\') '/' else ch);
            }
        }
        allocator.free(converted);
        converted = try list.toOwnedSlice(allocator);
    }

    if (raw or isSafeUnquotedPath(converted)) {
        return try allocator.dupe(u8, converted);
    }

    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(allocator);

    switch (shell_kind) {
        .pwsh => {
            try out.append(allocator, '\'');
            for (converted) |ch| {
                if (ch == '\'') {
                    try out.appendSlice(allocator, "''");
                } else {
                    try out.append(allocator, ch);
                }
            }
            try out.append(allocator, '\'');
        },
        .nu => {
            try out.append(allocator, '"');
            for (converted) |ch| {
                if (ch == '\\' or ch == '"') {
                    try out.append(allocator, '\\');
                }
                try out.append(allocator, ch);
            }
            try out.append(allocator, '"');
        },
        .gitbash, .wsl => {
            try out.append(allocator, '\'');
            for (converted) |ch| {
                if (ch == '\'') {
                    try out.appendSlice(allocator, "'\"'\"'");
                } else {
                    try out.append(allocator, ch);
                }
            }
            try out.append(allocator, '\'');
        },
        .cmd => {
            try out.append(allocator, '"');
            try out.appendSlice(allocator, converted);
            try out.append(allocator, '"');
        },
    }
    return try out.toOwnedSlice(allocator);
}

fn isSafeUnquotedPath(path: []const u8) bool {
    if (path.len == 0) return false;
    for (path) |c| {
        if (std.ascii.isAlphanumeric(c)) continue;
        switch (c) {
            '/', '\\', ':', '.', '_', '-' => continue,
            else => return false,
        }
    }
    return true;
}

pub fn readPromptDirectory(line: []const u8) ?[]const u8 {
    var trimmed = std.mem.trimLeft(u8, line, " \t");
    if (std.mem.startsWith(u8, trimmed, "PS ")) {
        trimmed = trimmed[3..];
    }
    const gt = std.mem.indexOfScalar(u8, trimmed, '>') orelse return null;
    const candidate = trimmed[0..gt];
    if (candidate.len < 3) return null;
    if (!std.ascii.isAlphabetic(candidate[0]) or candidate[1] != ':' or (candidate[2] != '\\' and candidate[2] != '/')) {
        return null;
    }
    for (candidate) |c| {
        if (c < 0x20 or c == 0x7f) return null;
    }
    return candidate;
}

test "scorePath ranks exact, prefix, contiguous, camelCase, and directories above weak matches" {
    const empty_dir = scorePath("src", true, "").?;
    const empty_file = scorePath("build.zig", false, "").?;
    try std.testing.expect(empty_dir > empty_file);
    try std.testing.expectEqual(@as(?i32, null), scorePath("src\\main.zig", false, ""));

    const exact = scorePath("main.zig", false, "main.zig").?;
    const prefix = scorePath("main_test.zig", false, "main").?;
    const subseq = scorePath("my_app_init.zig", false, "mai").?;
    try std.testing.expect(exact > prefix);
    try std.testing.expect(prefix > subseq);

    const camel = scorePath("WorkspaceApplication.zig", false, "wa").?;
    const non_boundary = scorePath("swamp.zig", false, "wa").?;
    try std.testing.expect(camel > non_boundary);
}

test "formatPathAlloc quotes per shell dialect and rejects control characters" {
    const allocator = std.testing.allocator;

    const safe = try formatPathAlloc(allocator, "C:\\code\\vex\\build.zig", .pwsh, false);
    defer allocator.free(safe);
    try std.testing.expectEqualStrings("C:\\code\\vex\\build.zig", safe);

    const pwsh_quoted = try formatPathAlloc(allocator, "C:\\Program Files\\My 'App'\\run.exe", .pwsh, false);
    defer allocator.free(pwsh_quoted);
    try std.testing.expectEqualStrings("'C:\\Program Files\\My ''App''\\run.exe'", pwsh_quoted);

    const nu_quoted = try formatPathAlloc(allocator, "C:\\Program Files\\App", .nu, false);
    defer allocator.free(nu_quoted);
    try std.testing.expectEqualStrings("\"C:\\\\Program Files\\\\App\"", nu_quoted);

    const bash_quoted = try formatPathAlloc(allocator, "C:\\Program Files\\App", .gitbash, false);
    defer allocator.free(bash_quoted);
    try std.testing.expectEqualStrings("'/c/Program Files/App'", bash_quoted);

    const wsl_converted = try formatPathAlloc(allocator, "C:\\Users\\Ziut\\My Project", .wsl, false);
    defer allocator.free(wsl_converted);
    try std.testing.expectEqualStrings("'/mnt/c/Users/Ziut/My Project'", wsl_converted);

    const raw_unquoted = try formatPathAlloc(allocator, "C:\\Program Files\\App", .pwsh, true);
    defer allocator.free(raw_unquoted);
    try std.testing.expectEqualStrings("C:\\Program Files\\App", raw_unquoted);

    try std.testing.expectError(
        error.ControlCharacterInPath,
        formatPathAlloc(allocator, "C:\\bad\npath", .cmd, false),
    );
}

test "readPromptDirectory extracts cmd and PowerShell prompt directories" {
    try std.testing.expectEqualStrings(
        "C:\\Users\\Ziut\\code\\vex",
        readPromptDirectory("PS C:\\Users\\Ziut\\code\\vex> git status").?,
    );
    try std.testing.expectEqualStrings(
        "D:\\work\\project",
        readPromptDirectory("D:\\work\\project>dir").?,
    );
    try std.testing.expectEqual(@as(?[]const u8, null), readPromptDirectory("user@host:~/code$ "));
}
