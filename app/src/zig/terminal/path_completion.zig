const std = @import("std");
const wsl_paths = @import("../shell/wsl_paths.zig");
const directory = @import("../shell/directory.zig");

pub const ShellQuoteKind = enum {
    literal,
    pwsh,
    cmd,
    nu,
    gitbash,
    wsl,
    wsl_nu,
    wsl_pwsh,

    pub fn fromProgram(program: []const u8) ShellQuoteKind {
        const base = std.fs.path.basename(program);
        const stem = if (std.ascii.endsWithIgnoreCase(base, ".exe")) base[0 .. base.len - 4] else base;
        if (std.ascii.eqlIgnoreCase(stem, "pwsh") or std.ascii.eqlIgnoreCase(stem, "powershell")) return .pwsh;
        if (std.ascii.eqlIgnoreCase(stem, "nu")) return .nu;
        if (std.ascii.eqlIgnoreCase(stem, "bash") or std.ascii.eqlIgnoreCase(stem, "git-bash")) return .gitbash;
        if (std.ascii.eqlIgnoreCase(stem, "wsl")) return .wsl;
        if (std.ascii.eqlIgnoreCase(stem, "cmd")) return .cmd;
        return .literal;
    }

    /// WSL is a transport, not a quoting dialect. Use trusted foreground-shell
    /// metadata when its Linux shell does not speak POSIX quoting.
    pub fn forWslShell(program: []const u8) ShellQuoteKind {
        return switch (fromProgram(program)) {
            .nu => .wsl_nu,
            .pwsh => .wsl_pwsh,
            else => .wsl,
        };
    }
};

/// Without a report or process cwd, WSL's launch-home sentinel is not a Windows cwd.
/// Browse the known distribution root instead of guessing the Linux user's home.
pub fn initialDirectoryAlloc(allocator: std.mem.Allocator, path: directory.DirectoryPath, context: directory.DirectoryContext) ![]u8 {
    if (path.shell_home and context == .wsl) {
        return wsl_paths.networkDirectoryFromLinuxAlloc(allocator, context.wsl, .{ .bytes = "/" });
    }
    return allocator.dupe(u8, path.bytes);
}

pub const Entry = struct {
    name: []u8,
    display_name: ?[]u8 = null,
    is_directory: bool,
    is_symbolic_link: bool = false,

    pub fn displayName(self: Entry) []const u8 {
        return self.display_name orelse self.name;
    }
};

pub const Match = struct {
    entry_index: usize,
    score: i32,
};

pub const max_entries: usize = 16_384;
pub const max_matches: usize = 50;
pub const max_index_bytes: usize = 8 * 1024 * 1024;

pub const Snapshot = struct {
    context: *anyopaque,
    publish: *const fn (context: *anyopaque, model: *PathCompletion) void,
    every: usize,
    next_at: usize,
};

pub const PathCompletion = struct {
    allocator: std.mem.Allocator,
    directory: []u8 = &.{},
    display_directory: ?[]u8 = null,
    query_buf: [128]u8 = undefined,
    query_len: usize = 0,
    entries: std.ArrayList(Entry) = .empty,
    matches: std.ArrayList(Match) = .empty,
    /// Candidate entry indices that matched `candidate_query_buf[0..candidate_query_len]`.
    /// Extending the query filters only this subset instead of scanning all entries.
    candidates: std.ArrayList(u32) = .empty,
    candidate_query_buf: [128]u8 = undefined,
    candidate_query_len: usize = 0,
    candidate_entries_len: usize = 0,
    selected_index: usize = 0,
    hovered_index: ?usize = null,
    first_visible: usize = 0,
    scan_error: bool = false,
    is_truncated: bool = false,
    has_skipped_entries: bool = false,
    index: union(enum) { synchronous, deferred: struct { revision: u64 = 0, loading: bool = false } } = .synchronous,
    recursive: bool = false,
    /// Cached once per directory: re-parsing the UNC prefix for every indexed entry is wasted work.
    is_wsl: bool = false,
    indexed_name_bytes: usize = 0,
    scan_revision: ?struct { latest: *const std.atomic.Value(u64), expected: u64 } = null,
    /// Lets a background scan publish usable partial indexes so results appear while the walk continues.
    snapshot: ?Snapshot = null,

    pub fn init(allocator: std.mem.Allocator) PathCompletion {
        return .{ .allocator = allocator };
    }

    pub fn deinit(self: *PathCompletion) void {
        self.clearEntries();
        self.entries.deinit(self.allocator);
        self.matches.deinit(self.allocator);
        self.candidates.deinit(self.allocator);
        if (self.directory.len > 0) self.allocator.free(self.directory);
        if (self.display_directory) |text| self.allocator.free(text);
        self.* = .{ .allocator = self.allocator };
    }

    fn clearEntries(self: *PathCompletion) void {
        for (self.entries.items) |entry| {
            self.allocator.free(entry.name);
            if (entry.display_name) |name| self.allocator.free(name);
        }
        self.entries.clearRetainingCapacity();
        self.candidates.clearRetainingCapacity();
        self.candidate_query_len = 0;
        self.candidate_entries_len = 0;
        self.indexed_name_bytes = 0;
    }

    pub fn directoryLabel(self: *const PathCompletion) []const u8 {
        return self.display_directory orelse self.directory;
    }

    pub fn query(self: *const PathCompletion) []const u8 {
        return self.query_buf[0..self.query_len];
    }

    /// Replaces the path picker atomically; inputs may borrow the current directory or query.
    /// Empty paths clear indexing; unavailable directories produce an explicit scan-error view.
    pub fn openDirectory(self: *PathCompletion, dir_path: []const u8, initial_query: []const u8) !void {
        const prefix = try boundedQueryPrefix(initial_query, self.query_buf.len);
        if (!isUsablePathText(initial_query)) return error.InvalidPathQuery;
        const root_len = if (dir_path.len > 0) try directoryRootLength(dir_path) else 0;
        var end = dir_path.len;
        while (end > root_len and isSeparator(dir_path[end - 1])) : (end -= 1) {}
        const effective = dir_path[0..end];
        var next = PathCompletion.init(self.allocator);
        errdefer next.deinit();
        next.directory = try self.allocator.dupe(u8, effective);
        if (try wsl_paths.networkDistribution(effective)) |distro| {
            next.is_wsl = true;
            const linux = try wsl_paths.linuxDirectoryFromNetworkAlloc(self.allocator, effective, distro);
            defer self.allocator.free(linux);
            next.display_directory = try std.fmt.allocPrint(self.allocator, "{s}:{s}", .{ distro.bytes, linux });
        }
        next.recursive = self.recursive;
        next.scan_revision = self.scan_revision;
        next.snapshot = self.snapshot;
        next.index = switch (self.index) {
            .synchronous => .synchronous,
            .deferred => |state| .{ .deferred = .{ .revision = try std.math.add(u64, state.revision, 1), .loading = true } },
        };
        @memcpy(next.query_buf[0..prefix.len], prefix);
        next.query_len = prefix.len;
        if (next.index == .synchronous) try next.scanCurrentDirectory();
        try next.refreshMatches();

        self.deinit();
        self.* = next;
    }

    pub fn navigateParent(self: *PathCompletion) !bool {
        if (self.directory.len == 0) return false;
        const root_len = try directoryRootLength(self.directory);
        if (self.directory.len <= root_len) return false;
        const separator = std.mem.lastIndexOfAny(u8, self.directory, "\\/") orelse return false;
        const parent = self.directory[0..@max(root_len, separator)];
        try self.openDirectory(parent, self.query());
        return true;
    }

    pub fn enterSelectedDirectory(self: *PathCompletion) !bool {
        const entry = self.selectedEntry() orelse return false;
        if (!entry.is_directory and !(self.is_wsl and entry.is_symbolic_link)) return false;
        const child = try std.fs.path.join(self.allocator, &.{ self.directory, entry.name });
        defer self.allocator.free(child);
        if (!entry.is_directory) {
            if (std.fs.openDirAbsolute(child, .{})) |dir| {
                var owned = dir;
                owned.close();
            } else |_| {
                const target = try @import("../shell/wsl_links.zig").directoryTargetAlloc(self.allocator, child) orelse return false;
                defer self.allocator.free(target);
                try self.openDirectory(target, "");
                return true;
            }
        }
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

    /// Appends complete UTF-8 codepoints within the byte limit; malformed input leaves the query unchanged.
    pub fn appendQuery(self: *PathCompletion, text: []const u8) !void {
        if (!std.unicode.utf8ValidateSlice(text)) return error.InvalidUtf8;
        if (!isUsablePathText(text) or text.len == 0) return;
        const total = std.math.add(usize, self.query_len, text.len) catch return error.InvalidTextInput;
        if (total > directory.DirectoryPath.max_bytes + self.query_buf.len) return error.InvalidTextInput;
        var short_buf: [128]u8 = undefined;
        const current_q = if (total <= short_buf.len) short_buf[0..total] else try self.allocator.alloc(u8, total);
        defer if (total > short_buf.len) self.allocator.free(current_q);
        @memcpy(current_q[0..self.query_len], self.query());
        @memcpy(current_q[self.query_len..], text);
        if (std.mem.lastIndexOfAny(u8, current_q, "/\\")) |sep| {
            // Keep the first backslash long enough to type a UNC prefix. A lone
            // forward slash still opens the drive/distribution root immediately.
            if (current_q.len == 1 and current_q[0] == '\\') {
                try self.setQuery(current_q);
                return;
            }
            const directory_prefix = current_q[0 .. sep + 1];
            const remainder = current_q[sep + 1 ..];
            if (self.resolveSubdirectoryAlloc(directory_prefix)) |resolved| {
                defer self.allocator.free(resolved);
                var rem_buf: [128]u8 = undefined;
                const rem_len = (try boundedQueryPrefix(remainder, rem_buf.len)).len;
                @memcpy(rem_buf[0..rem_len], remainder[0..rem_len]);
                try self.openDirectory(resolved, rem_buf[0..rem_len]);
                return;
            } else |err| {
                if (err == error.OutOfMemory) return err;
            }
        }

        try self.setQuery(try boundedQueryPrefix(current_q, self.query_buf.len));
    }

    pub fn backspaceQuery(self: *PathCompletion) !bool {
        if (self.query_len == 0) {
            return try self.navigateParent();
        }
        var i = self.query_len - 1;
        while (i > 0 and (self.query_buf[i] & 0xC0) == 0x80) : (i -= 1) {}
        try self.setQuery(self.query_buf[0..i]);
        return true;
    }

    pub fn clearQuery(self: *PathCompletion) !void {
        if (self.query_len == 0) return;
        try self.setQuery("");
    }

    fn setQuery(self: *PathCompletion, text: []const u8) !void {
        const previous_buf = self.query_buf;
        const previous_len = self.query_len;
        errdefer {
            self.query_buf = previous_buf;
            self.query_len = previous_len;
        }
        std.mem.copyForwards(u8, &self.query_buf, text);
        self.query_len = text.len;
        try self.refreshMatches();
        self.selected_index = 0;
        self.hovered_index = null;
        self.first_visible = 0;
    }

    pub fn moveSelection(self: *PathCompletion, delta: isize, visible_rows: usize) void {
        if (self.matches.items.len == 0) {
            self.selected_index = 0;
            self.first_visible = 0;
            return;
        }
        const max_idx: isize = @intCast(self.matches.items.len - 1);
        const cur: isize = @intCast(self.selected_index);
        const next: usize = @intCast(std.math.clamp(cur +| delta, 0, max_idx));
        self.selected_index = next;
        self.hovered_index = null;
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
        if (prefix.len > directory.DirectoryPath.max_bytes) return error.NameTooLong;
        const norm = try self.allocator.dupe(u8, prefix);
        defer self.allocator.free(norm);
        std.mem.replaceScalar(u8, norm, '/', '\\');
        // resolveWindows treats incomplete UNC prefixes as relative paths.
        if (std.mem.startsWith(u8, norm, "\\\\")) _ = try directoryRootLength(norm);
        const candidate = if (try wsl_paths.networkDistribution(self.directory)) |distro| blk: {
            if (std.mem.startsWith(u8, norm, "\\\\") or (norm.len >= 3 and std.ascii.isAlphabetic(norm[0]) and norm[1] == ':' and isSeparator(norm[2]))) break :blk try self.allocator.dupe(u8, norm);
            // Resolve in the Linux domain before encoding Windows-reserved names.
            // Preserve link/.. semantics instead of normalizing them on Windows.
            std.mem.replaceScalar(u8, norm, '\\', '/');
            if (norm.len > 0 and norm[0] == '/') break :blk try wsl_paths.networkDirectoryFromLinuxAlloc(self.allocator, distro, try wsl_paths.LinuxDirectory.parse(norm));
            const current = try wsl_paths.linuxDirectoryFromNetworkAlloc(self.allocator, self.directory, distro);
            defer self.allocator.free(current);
            const linux = try std.fmt.allocPrint(self.allocator, "{s}/{s}", .{ std.mem.trimRight(u8, current, "/"), norm });
            defer self.allocator.free(linux);
            break :blk try wsl_paths.networkDirectoryFromLinuxAlloc(self.allocator, distro, try wsl_paths.LinuxDirectory.parse(linux));
        } else try std.fs.path.resolveWindows(self.allocator, &.{ self.directory, norm });
        errdefer self.allocator.free(candidate);
        _ = try directoryRootLength(candidate);
        if (self.index == .synchronous) {
            var dir = try std.fs.openDirAbsolute(candidate, .{});
            dir.close();
        }
        return candidate;
    }

    fn scanCurrentDirectory(self: *PathCompletion) !void {
        self.clearEntries();
        self.scan_error = false;
        self.is_truncated = false;
        self.has_skipped_entries = false;
        if (self.directory.len == 0) return;
        try self.checkScanRevision();

        var dir = std.fs.openDirAbsolute(self.directory, .{ .iterate = true }) catch {
            self.scan_error = true;
            return;
        };
        defer dir.close();

        const path_walker = @import("path_walker.zig");
        if (self.is_wsl and self.recursive) {
            // 9P round trips dominate; read directories concurrently.
            var walker = path_walker.ParallelWalker.init(self.allocator, dir);
            defer walker.deinit();
            if (self.scan_revision) |revision| {
                walker.cancel_latest = revision.latest;
                walker.cancel_expected = revision.expected;
            }
            if (self.display_directory) |label| {
                if (std.mem.endsWith(u8, label, ":/")) walker.skip_root_names = path_walker.Walker.wsl_root_skip_names;
            }
            try self.scanWalker(&walker);
            return;
        }
        var walker = path_walker.Walker.init(self.allocator, dir);
        defer walker.deinit();
        walker.recursive = self.recursive;
        walker.probe_symlink_directories = !(self.is_wsl and self.recursive);
        if (self.is_wsl and self.display_directory != null) {
            const label = self.display_directory.?;
            if (std.mem.endsWith(u8, label, ":/")) walker.skip_root_names = path_walker.Walker.wsl_root_skip_names;
        }
        try self.scanWalker(&walker);
    }

    fn scanWalker(self: *PathCompletion, walker: anytype) !void {
        var attempts: usize = 0;
        while (true) {
            try self.checkScanRevision();
            // Bound work even when every child is unreadable or has an unusable name.
            if (attempts == max_entries * 2) {
                self.is_truncated = true;
                break;
            }
            attempts += 1;
            const entry = walker.next() catch |err| {
                if (err == error.OutOfMemory) return err;
                // Walker advances past failed children. WSL trees often contain folders
                // Windows cannot open; one such folder must not discard the whole index.
                if (walker.root_failed) {
                    self.scan_error = true;
                    break;
                }
                self.has_skipped_entries = true;
                continue;
            } orelse break;
            const previous_len = self.entries.items.len;
            if (!try self.appendIndexedEntry(entry.path, entry.kind == .directory)) break;
            if (self.entries.items.len > previous_len) self.entries.items[previous_len].is_symbolic_link = entry.kind == .sym_link;
            if (self.snapshot) |*hook| {
                if (self.entries.items.len >= hook.next_at) {
                    // Geometric ramp-up: the first rows show almost immediately, while clone cost stays amortized.
                    hook.next_at = self.entries.items.len + @min(hook.every, self.entries.items.len);
                    hook.publish(hook.context, self);
                }
            }
        }
    }

    fn checkScanRevision(self: *const PathCompletion) !void {
        if (self.scan_revision) |revision| {
            if (revision.latest.load(.acquire) != revision.expected) return error.StalePathIndex;
        }
    }
    fn appendIndexedEntry(self: *PathCompletion, name: []const u8, is_directory: bool) !bool {
        const is_wsl = self.is_wsl;
        if (!isUsablePathText(name) or (is_wsl and !wsl_paths.networkNameIsUsable(name))) {
            self.has_skipped_entries = true;
            return true;
        }
        if (self.entries.items.len >= max_entries or name.len > max_index_bytes - self.indexed_name_bytes) {
            self.is_truncated = true;
            return false;
        }
        const display = if (is_wsl and wsl_paths.networkNameNeedsDecoding(name)) try wsl_paths.linuxPathFromNetworkSuffixAlloc(self.allocator, name) else null;
        errdefer if (display) |text| self.allocator.free(text);
        const display_bytes = if (display) |text| text.len else 0;
        if (display_bytes > max_index_bytes - self.indexed_name_bytes - name.len) {
            if (display) |text| self.allocator.free(text);
            self.is_truncated = true;
            return false;
        }
        const owned = try self.allocator.dupe(u8, name);
        errdefer self.allocator.free(owned);
        try self.entries.append(self.allocator, .{ .name = owned, .display_name = display, .is_directory = is_directory });
        self.indexed_name_bytes += name.len + display_bytes;
        return true;
    }

    pub fn failDeferredIndex(self: *PathCompletion) void {
        if (self.index == .deferred) self.index.deferred.loading = false;
        self.scan_error = true;
    }

    /// Adopts a completed index transactionally while retaining text typed during background scanning.
    pub fn applyDeferredIndex(self: *PathCompletion, result: *PathCompletion) !void {
        try self.adoptIndex(result, false);
    }

    /// Adopts a partial index; the picker keeps reporting that loading continues.
    pub fn applyPartialIndex(self: *PathCompletion, result: *PathCompletion) !void {
        try self.adoptIndex(result, true);
    }

    fn adoptIndex(self: *PathCompletion, result: *PathCompletion, loading: bool) !void {
        const state = switch (self.index) {
            .synchronous => return error.NotDeferredPathIndex,
            .deferred => |value| value,
        };
        if (!std.mem.eql(u8, self.directory, result.directory)) return error.MismatchedPathIndex;
        @memcpy(result.query_buf[0..self.query_len], self.query());
        result.query_len = self.query_len;
        try result.refreshMatches();
        result.index = .{ .deferred = .{ .revision = state.revision, .loading = loading } };
        // New entries can reorder matches. Preserve the path, not its old row,
        // even when the last snapshot finishes loading while the user navigates.
        result.selected_index = @min(self.selected_index, result.matches.items.len -| 1);
        if (self.selectedEntry()) |selected| {
            for (result.matches.items, 0..) |match, i| {
                if (std.mem.eql(u8, selected.name, result.entries.items[match.entry_index].name)) {
                    result.selected_index = i;
                    break;
                }
            }
        }
        result.first_visible = @min(self.first_visible, result.selected_index);
        // A hovered row may now refer to a different path until the next pointer move.
        result.hovered_index = null;
        self.deinit();
        self.* = result.*;
        result.* = PathCompletion.init(self.allocator);
    }

    /// Copies the scanned index (not query or selection) so it can outlive or race the original.
    pub fn cloneIndexAlloc(self: *const PathCompletion) !PathCompletion {
        var next = PathCompletion.init(self.allocator);
        errdefer next.deinit();
        next.directory = try self.allocator.dupe(u8, self.directory);
        if (self.display_directory) |text| next.display_directory = try self.allocator.dupe(u8, text);
        next.recursive = self.recursive;
        next.is_wsl = self.is_wsl;
        next.scan_error = self.scan_error;
        next.is_truncated = self.is_truncated;
        next.has_skipped_entries = self.has_skipped_entries;
        next.index = self.index;
        try next.entries.ensureTotalCapacity(self.allocator, self.entries.items.len);
        for (self.entries.items) |entry| {
            const name = try self.allocator.dupe(u8, entry.name);
            errdefer self.allocator.free(name);
            const display = if (entry.display_name) |text| try self.allocator.dupe(u8, text) else null;
            next.entries.appendAssumeCapacity(.{ .name = name, .display_name = display, .is_directory = entry.is_directory, .is_symbolic_link = entry.is_symbolic_link });
            next.indexed_name_bytes += entry.name.len + (if (display) |text| text.len else 0);
        }
        return next;
    }

    /// Retains the best fifty path matches using bounded scratch space; warm queries reuse output storage.
    pub fn refreshMatches(self: *PathCompletion) !void {
        const ranking = MatchRanking{ .entries = self.entries.items };
        var scratch: [@sizeOf(Match) * max_matches]u8 align(@alignOf(Match)) = undefined;
        var scratch_allocator = std.heap.FixedBufferAllocator.init(&scratch);
        var best = std.PriorityQueue(Match, MatchRanking, MatchRanking.worstFirst).init(scratch_allocator.allocator(), ranking);
        defer best.deinit();
        try best.ensureTotalCapacityPrecise(max_matches);

        const q = self.query();
        var folded_buf: [128]u8 = undefined;
        const folded_q = normalizeQuerySlice(q, &folded_buf);

        const entries_len = self.entries.items.len;
        const prev_q = self.candidate_query_buf[0..self.candidate_query_len];
        const can_filter_candidates = self.candidate_entries_len == entries_len and
            self.candidate_query_len > 0 and
            q.len >= self.candidate_query_len and
            std.mem.startsWith(u8, q, prev_q);

        if (can_filter_candidates) {
            var write: usize = 0;
            for (self.candidates.items) |entry_idx| {
                const idx: usize = entry_idx;
                const entry = self.entries.items[idx];
                const score = scorePathImpl(entry.displayName(), entry.is_directory, folded_q, true) orelse continue;
                self.candidates.items[write] = entry_idx;
                write += 1;
                const candidate = Match{ .entry_index = idx, .score = score };
                if (best.count() < max_matches) {
                    try best.add(candidate);
                } else if (best.peek()) |worst| {
                    if (ranking.bestFirst(candidate, worst)) {
                        _ = best.remove();
                        try best.add(candidate);
                    }
                }
            }
            self.candidates.items.len = write;
            @memcpy(self.candidate_query_buf[0..q.len], q);
            self.candidate_query_len = q.len;
        } else {
            const record_candidates = if (q.len > 0 and q.len <= self.candidate_query_buf.len)
                if (self.candidates.ensureTotalCapacity(self.allocator, entries_len)) |_| true else |_| false
            else
                false;
            if (record_candidates) self.candidates.clearRetainingCapacity();

            for (self.entries.items, 0..) |entry, idx| {
                const score = scorePathImpl(entry.displayName(), entry.is_directory, folded_q, true) orelse continue;
                if (record_candidates) self.candidates.appendAssumeCapacity(@intCast(idx));
                const candidate = Match{ .entry_index = idx, .score = score };
                if (best.count() < max_matches) {
                    try best.add(candidate);
                } else if (best.peek()) |worst| {
                    if (ranking.bestFirst(candidate, worst)) {
                        _ = best.remove();
                        try best.add(candidate);
                    }
                }
            }

            if (record_candidates) {
                @memcpy(self.candidate_query_buf[0..q.len], q);
                self.candidate_query_len = q.len;
                self.candidate_entries_len = entries_len;
            } else {
                self.candidate_query_len = 0;
                self.candidate_entries_len = 0;
            }
        }

        // Reserve before replacing visible results so allocation failure preserves the prior list.
        try self.matches.ensureTotalCapacity(self.allocator, best.count());
        self.matches.clearRetainingCapacity();
        self.matches.appendSliceAssumeCapacity(best.items);
        std.mem.sort(Match, self.matches.items, ranking, MatchRanking.bestFirst);
        if (self.selected_index >= self.matches.items.len) {
            self.selected_index = 0;
        }
    }
};

fn isSeparator(c: u8) bool {
    return c == '\\' or c == '/';
}

/// UNC shares (including WSL distributions) are navigation roots, not ordinary folders.
fn directoryRootLength(path: []const u8) !usize {
    _ = try directory.DirectoryPath.parse(path);
    if (path[1] == ':') return 3;
    const server_end = std.mem.indexOfScalarPos(u8, path, 2, '\\') orelse return error.InvalidDirectoryPath;
    if (server_end == 2 or server_end + 1 == path.len) return error.InvalidDirectoryPath;
    const share_end = std.mem.indexOfAnyPos(u8, path, server_end + 1, "\\/") orelse path.len;
    if (share_end == server_end + 1) return error.InvalidDirectoryPath;
    return share_end;
}

fn isUsablePathText(text: []const u8) bool {
    if (!std.unicode.utf8ValidateSlice(text)) return false;
    var scalars = std.unicode.Utf8View.initUnchecked(text).iterator();
    while (scalars.nextCodepoint()) |scalar| {
        if (scalar < 0x20 or (scalar >= 0x7f and scalar <= 0x9f)) return false;
    }
    return true;
}

fn boundedQueryPrefix(text: []const u8, capacity: usize) ![]const u8 {
    if (!std.unicode.utf8ValidateSlice(text)) return error.InvalidUtf8;
    var len = @min(text.len, capacity);
    if (len < text.len) {
        while (len > 0 and text[len] & 0xc0 == 0x80) : (len -= 1) {}
    }
    return text[0..len];
}

const MatchRanking = struct {
    entries: []const Entry,

    fn worstFirst(self: MatchRanking, a: Match, b: Match) std.math.Order {
        if (a.score != b.score) return std.math.order(a.score, b.score);
        const na = self.entries[a.entry_index].displayName();
        const nb = self.entries[b.entry_index].displayName();
        const folded = std.ascii.orderIgnoreCase(nb, na);
        if (folded != .eq) return folded;
        const exact = std.mem.order(u8, nb, na);
        if (exact != .eq) return exact;
        return std.math.order(b.entry_index, a.entry_index);
    }

    fn bestFirst(self: MatchRanking, a: Match, b: Match) bool {
        return self.worstFirst(a, b) == .gt;
    }
};

/// Table lookup keeps the per-character fold in the hot scoring loops branch-free.
const fold_table = blk: {
    var table: [256]u8 = undefined;
    for (&table, 0..) |*slot, i| {
        const c: u8 = @intCast(i);
        slot.* = if (c == '/') '\\' else std.ascii.toLower(c);
    }
    break :blk table;
};

fn normalizeQuerySlice(query: []const u8, buf: *[128]u8) []const u8 {
    const len = @min(query.len, buf.len);
    for (query[0..len], 0..) |c, i| buf[i] = fold_table[c];
    return buf[0..len];
}

pub fn scorePath(relative_path: []const u8, is_directory: bool, query: []const u8) ?i32 {
    if (query.len <= 128) {
        var folded_buf: [128]u8 = undefined;
        return scorePathImpl(relative_path, is_directory, normalizeQuerySlice(query, &folded_buf), true);
    }
    return scorePathImpl(relative_path, is_directory, query, false);
}

fn scorePathImpl(relative_path: []const u8, is_directory: bool, query: []const u8, comptime folded: bool) ?i32 {
    var filename_offset: usize = 0;
    if (std.mem.lastIndexOfAny(u8, relative_path, "\\/")) |sep| {
        filename_offset = sep + 1;
    }
    if (query.len == 0) {
        if (filename_offset != 0) return null;
        return if (is_directory) 100 else 0;
    }

    const name_score = scoreSubsequence(relative_path, query, filename_offset, folded);
    var best: i32 = if (name_score) |ns|
        ns + 80
    else if (filename_offset > 0)
        (scoreSubsequence(relative_path, query, 0, folded) orelse return null)
    else
        return null;

    const filename = relative_path[filename_offset..];
    if (startsWithQuery(filename, query, folded)) {
        best += 160;
        if (filename.len == query.len) {
            best += 320;
        }
    }
    if (is_directory) best += 10;
    return best;
}

/// The picker folds once per query; public callers may pass arbitrarily long
/// raw queries. Specialize the same scorer so both paths retain identical ranking.
fn queryChar(byte: u8, comptime folded: bool) u8 {
    return if (folded) byte else fold_table[byte];
}

fn startsWithQuery(haystack: []const u8, query: []const u8, comptime folded: bool) bool {
    if (haystack.len < query.len) return false;
    for (query, 0..) |qc, i| {
        if (fold_table[haystack[i]] != queryChar(qc, folded)) return false;
    }
    return true;
}

fn scoreSubsequence(path: []const u8, query: []const u8, start: usize, comptime folded: bool) ?i32 {
    if (path.len - start < query.len) return null;
    var previous: i32 = -2;
    var score: i32 = 0;
    var offset: usize = start;

    for (query, 0..) |raw_qc, q_idx| {
        const qc = queryChar(raw_qc, folded);
        var found_index: ?usize = null;
        var idx = offset;
        while (idx < path.len) : (idx += 1) {
            if (fold_table[path[idx]] == qc) {
                found_index = idx;
                break;
            }
        }
        var i = found_index orelse return null;
        const is_contiguous = previous >= 0 and @as(i32, @intCast(i)) == previous + 1;
        if (!is_contiguous and !isWordStart(path, i, start)) {
            var scan = i + 1;
            const remaining = query[q_idx + 1 ..];
            while (scan + remaining.len < path.len) : (scan += 1) {
                if (fold_table[path[scan]] == qc and
                    isWordStart(path, scan, start) and
                    hasSubsequence(path, remaining, scan + 1, folded))
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

fn hasSubsequence(path: []const u8, query: []const u8, start: usize, comptime folded: bool) bool {
    var offset = start;
    for (query) |raw_qc| {
        const qc = queryChar(raw_qc, folded);
        var found: ?usize = null;
        var idx = offset;
        while (idx < path.len) : (idx += 1) {
            if (fold_table[path[idx]] == qc) {
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
    if (!std.unicode.utf8ValidateSlice(full_path)) return error.InvalidUtf8;
    if (!isUsablePathText(full_path)) return error.ControlCharacterInPath;

    var converted = try allocator.dupe(u8, full_path);
    defer allocator.free(converted);

    if (shell_kind == .wsl or shell_kind == .wsl_nu or shell_kind == .wsl_pwsh) {
        if (wsl_paths.networkDistribution(full_path) catch null) |distro| {
            const linux_path = try wsl_paths.linuxDirectoryFromNetworkAlloc(allocator, full_path, distro);
            allocator.free(converted);
            converted = linux_path;
        } else if (full_path.len >= 3 and std.ascii.isAlphabetic(full_path[0]) and full_path[1] == ':' and (full_path[2] == '\\' or full_path[2] == '/')) {
            var list: std.ArrayList(u8) = .empty;
            errdefer list.deinit(allocator);
            try list.appendSlice(allocator, "/mnt/");
            try list.append(allocator, std.ascii.toLower(full_path[0]));
            for (full_path[2..]) |ch| {
                try list.append(allocator, if (ch == '\\') '/' else ch);
            }
            const replacement = try list.toOwnedSlice(allocator);
            allocator.free(converted);
            converted = replacement;
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
        const replacement = try list.toOwnedSlice(allocator);
        allocator.free(converted);
        converted = replacement;
    }

    // cmd expands environment/delayed variables even inside double quotes.
    // No path-only quoting works in both expansion modes; reject rather than
    // silently inserting a different path. Explicit raw insertion stays available.
    if (!raw and shell_kind == .cmd and std.mem.indexOfAny(u8, converted, "%!") != null) return error.UnquotableCmdPath;

    if (raw or isSafeUnquotedPath(converted)) {
        return try allocator.dupe(u8, converted);
    }

    var out: std.ArrayList(u8) = .empty;
    errdefer out.deinit(allocator);

    switch (shell_kind) {
        .literal => try out.appendSlice(allocator, converted),
        .pwsh, .wsl_pwsh => {
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
        .nu, .wsl_nu => {
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

test "path formatting allocation failures release converted buffers and unknown shells remain literal" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkPathFormattingAllocations, .{});
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkWslNetworkPathFormattingAllocations, .{});
    const kind = ShellQuoteKind.fromProgram("C:\\tools\\future-shell.exe");
    try std.testing.expectEqual(ShellQuoteKind.literal, kind);
    const literal = try formatPathAlloc(std.testing.allocator, "C:\\A Folder\\future.txt", kind, false);
    defer std.testing.allocator.free(literal);
    try std.testing.expectEqualStrings("C:\\A Folder\\future.txt", literal);
}
fn checkWslNetworkPathFormattingAllocations(allocator: std.mem.Allocator) !void {
    const formatted = try formatPathAlloc(allocator, "\\\\wsl.localhost\\Ubuntu\\home\\future user", .wsl, false);
    allocator.free(formatted);
}
fn checkPathFormattingAllocations(allocator: std.mem.Allocator) !void {
    for ([_]ShellQuoteKind{ .gitbash, .wsl, .wsl_nu, .wsl_pwsh, .pwsh, .literal }) |kind| {
        const formatted = try formatPathAlloc(allocator, "C:\\A Folder\\future.txt", kind, false);
        allocator.free(formatted);
    }
}
test "WSL shell dialects convert Linux paths independently of shell quoting" {
    const allocator = std.testing.allocator;
    try std.testing.expectEqual(ShellQuoteKind.wsl_nu, ShellQuoteKind.forWslShell("/usr/bin/nu"));
    try std.testing.expectEqual(ShellQuoteKind.wsl_pwsh, ShellQuoteKind.forWslShell("pwsh"));
    try std.testing.expectEqual(ShellQuoteKind.wsl, ShellQuoteKind.forWslShell("bash"));
    for ([_]struct { shell: ShellQuoteKind, expected: []const u8 }{
        .{ .shell = .wsl_nu, .expected = "\"/tmp/a 'quote\\\"\"" },
        .{ .shell = .wsl_pwsh, .expected = "'/tmp/a ''quote\"'" },
        .{ .shell = .wsl, .expected = "'/tmp/a '\"'\"'quote\"'" },
    }) |fixture| {
        const formatted = try formatPathAlloc(allocator, "\\\\wsl.localhost\\NixOS\\tmp\\a 'quote\xef\x80\xa2", fixture.shell, false);
        defer allocator.free(formatted);
        try std.testing.expectEqualStrings(fixture.expected, formatted);
    }
}

test "cmd variable expansion cannot silently change inserted filenames" {
    for ([_][]const u8{ "C:\\file%TEMP%.txt", "C:\\file!TEMP!.txt" }) |path| {
        try std.testing.expectError(error.UnquotableCmdPath, formatPathAlloc(std.testing.allocator, path, .cmd, false));
        const raw = try formatPathAlloc(std.testing.allocator, path, .cmd, true);
        defer std.testing.allocator.free(raw);
        try std.testing.expectEqualStrings(path, raw);
    }
}

test "index budget and stale revisions stop scanning without replacing current state" {
    var model = PathCompletion.init(std.testing.allocator);
    defer model.deinit();
    model.indexed_name_bytes = max_index_bytes - 2;
    try std.testing.expect(!try model.appendIndexedEntry("long-name", false));
    try std.testing.expect(model.is_truncated);
    try std.testing.expectEqual(@as(usize, 0), model.entries.items.len);
    model.clearEntries();
    var latest = std.atomic.Value(u64).init(2);
    model.scan_revision = .{ .latest = &latest, .expected = 1 };
    try std.testing.expectError(error.StalePathIndex, model.openDirectory("C:\\unopened-future-directory", "query"));
    try std.testing.expectEqualStrings("", model.query());
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

test "recursive path indexing skips unreadable children and retains readable siblings" {
    const Walker = struct {
        step: usize = 0,
        failure: anyerror = error.AccessDenied,
        root_failed: bool = false,

        fn next(self: *@This()) !?struct { path: []const u8, kind: std.fs.Dir.Entry.Kind } {
            defer self.step += 1;
            return switch (self.step) {
                0 => .{ .path = "before.txt", .kind = .file },
                1 => self.failure,
                2 => .{ .path = "after.txt", .kind = .file },
                else => null,
            };
        }
    };
    for ([_]anyerror{ error.AccessDenied, error.FileNotFound, error.Unexpected }) |failure| {
        var completion = PathCompletion.init(std.testing.allocator);
        defer completion.deinit();
        var walker = Walker{ .failure = failure };
        try completion.scanWalker(&walker);
        try completion.refreshMatches();
        try std.testing.expect(!completion.scan_error);
        try std.testing.expect(completion.has_skipped_entries);
        try std.testing.expect(!completion.is_truncated);
        try std.testing.expectEqual(@as(usize, 2), completion.matches.items.len);
        try std.testing.expectEqualStrings("before.txt", completion.entries.items[0].name);
        try std.testing.expectEqualStrings("after.txt", completion.entries.items[1].name);
    }

    var completion = PathCompletion.init(std.testing.allocator);
    defer completion.deinit();
    var root_failure = Walker{ .step = 1, .root_failed = true };
    try completion.scanWalker(&root_failure);
    try std.testing.expect(completion.scan_error);
    try std.testing.expectEqual(@as(usize, 0), completion.entries.items.len);
    try std.testing.expect(try completion.appendIndexedEntry("read-before-root-error.txt", false));
    root_failure.step = 1;
    try completion.scanWalker(&root_failure);
    try std.testing.expectEqual(@as(usize, 1), completion.entries.items.len);

    var allocation_failure = Walker{ .step = 1, .failure = error.OutOfMemory };
    try std.testing.expectError(error.OutOfMemory, completion.scanWalker(&allocation_failure));
}

test "path roots stop parent navigation at drives, shares and WSL distributions" {
    var model = PathCompletion.init(std.testing.allocator);
    defer model.deinit();
    model.index = .{ .deferred = .{} };
    for ([_][]const u8{ "C:\\", "C:/", "\\\\server\\share", "\\\\wsl.localhost\\NixOS", "\\\\wsl$\\Ubuntu" }) |root| {
        try model.openDirectory(root, "keep");
        try std.testing.expect(!try model.navigateParent());
        const child = try std.fmt.allocPrint(std.testing.allocator, "{s}\\child\\", .{root});
        defer std.testing.allocator.free(child);
        try model.openDirectory(child, "keep");
        try std.testing.expect(try model.navigateParent());
        try std.testing.expectEqualStrings(std.mem.trimRight(u8, root, "\\/"), std.mem.trimRight(u8, model.directory, "\\/"));
        try std.testing.expectEqualStrings("keep", model.query());
        try std.testing.expect(!try model.navigateParent());
    }
    for ([_][]const u8{ "\\\\server\\", "\\\\server\\\\child", "\\\\\\share" }) |invalid| {
        try std.testing.expectError(error.InvalidDirectoryPath, model.openDirectory(invalid, "replace"));
        try std.testing.expectEqualStrings("keep", model.query());
    }
}

test "WSL typed paths stay in their distribution and preserve symlink parent semantics" {
    var model = PathCompletion.init(std.testing.allocator);
    defer model.deinit();
    model.index = .{ .deferred = .{} };
    const root = "\\\\wsl.localhost\\NixOS";
    for ([_]struct { input: []const u8, expected: []const u8, query: []const u8 }{
        .{ .input = "/tmp/after", .expected = root ++ "\\tmp", .query = "after" },
        .{ .input = "/", .expected = root, .query = "" },
        .{ .input = "child/file", .expected = root ++ "\\home\\user\\child", .query = "file" },
        .{ .input = "link/../file", .expected = root ++ "\\home\\user\\link\\..", .query = "file" },
        .{ .input = "a:dir/question?/file", .expected = root ++ "\\home\\user\\a\xef\x80\xbadir\\question\xef\x80\xbf", .query = "file" },
        .{ .input = "C:\\work\\file", .expected = "C:\\work", .query = "file" },
        .{ .input = "\\\\wsl$\\Ubuntu\\home\\file", .expected = "\\\\wsl$\\Ubuntu\\home", .query = "file" },
    }) |fixture| {
        try model.openDirectory(root ++ "\\home\\user", "");
        try model.appendQuery(fixture.input);
        try std.testing.expectEqualStrings(fixture.expected, model.directory);
        try std.testing.expectEqualStrings(fixture.query, model.query());
    }
}

test "Windows typed paths resolve relative, rooted, absolute and parent directories" {
    var model = PathCompletion.init(std.testing.allocator);
    defer model.deinit();
    model.index = .{ .deferred = .{} };
    for ([_]struct { input: []const u8, expected: []const u8 }{
        .{ .input = "child/file", .expected = "C:\\work\\child" },
        .{ .input = "../file", .expected = "C:\\" },
        .{ .input = "/tmp/file", .expected = "C:\\tmp" },
        .{ .input = "D:/other/file", .expected = "D:\\other" },
        .{ .input = "\\\\server\\share\\file", .expected = "\\\\server\\share" },
    }) |fixture| {
        try model.openDirectory("C:\\work", "");
        try model.appendQuery(fixture.input);
        try std.testing.expectEqualStrings(fixture.expected, model.directory);
        try std.testing.expectEqualStrings("file", model.query());
    }
}

test "UNC paths can be typed character by character as well as pasted" {
    var model = PathCompletion.init(std.testing.allocator);
    defer model.deinit();
    model.index = .{ .deferred = .{} };
    for ([_][]const u8{ "C:\\work", "\\\\wsl.localhost\\NixOS\\home" }) |start| {
        try model.openDirectory(start, "");
        const input = "\\\\server\\share\\file";
        for (0..input.len) |i| try model.appendQuery(input[i .. i + 1]);
        try std.testing.expectEqualStrings("\\\\server\\share", model.directory);
        try std.testing.expectEqualStrings("file", model.query());
    }
}

test "unusable filenames do not poison readable results and index flags reset on navigation" {
    var model = PathCompletion.init(std.testing.allocator);
    defer model.deinit();
    try std.testing.expect(try model.appendIndexedEntry("before.txt", false));
    for ([_][]const u8{ "bad\xff", "bad\nname", "bad\x00name", "bad\xc2\x85name" }) |name| {
        try std.testing.expect(try model.appendIndexedEntry(name, false));
    }
    try std.testing.expect(try model.appendIndexedEntry("Żółć 😀.txt", false));
    try model.refreshMatches();
    try std.testing.expectEqual(@as(usize, 2), model.matches.items.len);
    try std.testing.expect(model.has_skipped_entries);
    try std.testing.expect(!model.scan_error);
    model.is_truncated = true;
    model.index = .{ .deferred = .{} };
    try model.openDirectory("C:\\fresh", "");
    try std.testing.expect(!model.has_skipped_entries and !model.is_truncated and !model.scan_error);
}

test "WSL UNC escaped controls and backslashes are skipped while reserved printable names are decoded" {
    const allocator = std.testing.allocator;
    var model = PathCompletion.init(allocator);
    defer model.deinit();
    model.index = .{ .deferred = .{} };
    try model.openDirectory("\\\\wsl.localhost\\NixOS\\tmp", "");
    for ([_][]const u8{ "bad\xef\x80\x8aname", "bad\xef\x81\x9cname" }) |name| {
        try std.testing.expect(try model.appendIndexedEntry(name, false));
    }
    try std.testing.expect(model.has_skipped_entries);
    try std.testing.expectEqual(@as(usize, 0), model.entries.items.len);
    const formatted = try formatPathAlloc(allocator, "\\\\wsl.localhost\\NixOS\\tmp\\question\xef\x80\xbf star\xef\x80\xaa", .wsl, false);
    defer allocator.free(formatted);
    try std.testing.expectEqualStrings("'/tmp/question? star*'", formatted);
}

test "WSL display names are searchable Linux paths while insertion retains filesystem spelling" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkWslDisplayAllocations, .{});
}

fn checkWslDisplayAllocations(allocator: std.mem.Allocator) !void {
    var model = PathCompletion.init(allocator);
    defer model.deinit();
    model.index = .{ .deferred = .{} };
    try model.openDirectory("\\\\wsl.localhost\\NixOS\\tmp\\colon\xef\x80\xba", "");
    try std.testing.expectEqualStrings("NixOS:/tmp/colon:", model.directoryLabel());
    const encoded = "folder\\question\xef\x80\xbf.txt";
    try std.testing.expect(try model.appendIndexedEntry(encoded, false));
    try std.testing.expectEqualStrings("folder/question?.txt", model.entries.items[0].displayName());
    try model.setQuery("?");
    try std.testing.expectEqual(@as(usize, 1), model.matches.items.len);
    const selected = (try model.selectedFullPathAlloc(allocator)).?;
    defer allocator.free(selected);
    try std.testing.expect(std.mem.endsWith(u8, selected, encoded));
    const formatted = try formatPathAlloc(allocator, selected, .wsl, false);
    defer allocator.free(formatted);
    try std.testing.expectEqualStrings("'/tmp/colon:/folder/question?.txt'", formatted);
}

test "index entry limit reports truncation only when a further entry is omitted" {
    var model = PathCompletion.init(std.testing.allocator);
    defer model.deinit();
    for (0..max_entries) |_| try std.testing.expect(try model.appendIndexedEntry("x", false));
    try std.testing.expect(!model.is_truncated);
    try std.testing.expect(!try model.appendIndexedEntry("overflow", false));
    try std.testing.expect(model.is_truncated);
    try std.testing.expectEqual(max_entries, model.entries.items.len);
    try std.testing.expect(!model.has_skipped_entries);
    model.clearEntries();
    model.is_truncated = false;
    model.indexed_name_bytes = max_index_bytes - 2;
    try std.testing.expect(try model.appendIndexedEntry("ż", false));
    try std.testing.expect(!model.is_truncated);
    try std.testing.expect(!try model.appendIndexedEntry("x", false));
    try std.testing.expect(model.is_truncated);
}

test "unreadable trees have bounded scan work and remain cancellation aware" {
    const Walker = struct {
        calls: usize = 0,
        root_failed: bool = false,
        fn next(self: *@This()) !?struct { path: []const u8, kind: std.fs.Dir.Entry.Kind } {
            self.calls += 1;
            return error.AccessDenied;
        }
    };
    var model = PathCompletion.init(std.testing.allocator);
    defer model.deinit();
    var walker = Walker{};
    try model.scanWalker(&walker);
    try std.testing.expectEqual(max_entries * 2, walker.calls);
    try std.testing.expect(model.has_skipped_entries and model.is_truncated);
    var latest = std.atomic.Value(u64).init(2);
    model.scan_revision = .{ .latest = &latest, .expected = 1 };
    try std.testing.expectError(error.StalePathIndex, model.scanWalker(&walker));
    try std.testing.expectEqual(max_entries * 2, walker.calls);
}

test "failed query edits preserve text, selection and matches and successful edits clear hover" {
    const allocator = std.testing.allocator;
    var model = PathCompletion.init(allocator);
    defer model.deinit();
    try std.testing.expect(try model.appendIndexedEntry("file", false));
    try model.setQuery("zz");
    model.hovered_index = 7;
    model.first_visible = 7;
    var failing = std.testing.FailingAllocator.init(allocator, .{ .fail_index = 0 });
    model.allocator = failing.allocator();
    defer model.allocator = allocator;
    try std.testing.expectError(error.OutOfMemory, model.clearQuery());
    try std.testing.expectEqualStrings("zz", model.query());
    try std.testing.expectEqual(@as(?usize, 7), model.hovered_index);
    try std.testing.expectEqual(@as(usize, 7), model.first_visible);
    try std.testing.expectEqual(@as(usize, 0), model.matches.items.len);
    model.allocator = allocator;
    try model.clearQuery();
    try std.testing.expectEqual(@as(?usize, null), model.hovered_index);
    model.hovered_index = 0;
    model.moveSelection(std.math.maxInt(isize), 8);
    try std.testing.expectEqual(@as(usize, 0), model.selected_index);
    model.moveSelection(std.math.minInt(isize), 8);
    try std.testing.expectEqual(@as(usize, 0), model.selected_index);
    try std.testing.expectEqual(@as(?usize, null), model.hovered_index);
}

test "WSL home fallback uses its distribution and reported paths remain unchanged" {
    const allocator = std.testing.allocator;
    const context: directory.DirectoryContext = .{ .wsl = try wsl_paths.WslDistribution.parse("NixOS") };
    const home = try initialDirectoryAlloc(allocator, .{ .bytes = "C:\\Users\\WindowsUser", .shell_home = true }, context);
    defer allocator.free(home);
    try std.testing.expectEqualStrings("\\\\wsl.localhost\\NixOS\\", home);
    const reported = try initialDirectoryAlloc(allocator, .{ .bytes = "\\\\wsl$\\NixOS\\home\\linux-user" }, context);
    defer allocator.free(reported);
    try std.testing.expectEqualStrings("\\\\wsl$\\NixOS\\home\\linux-user", reported);
}

test "deferred failures stop loading and mismatched results cannot replace the current folder" {
    var model = PathCompletion.init(std.testing.allocator);
    defer model.deinit();
    model.index = .{ .deferred = .{} };
    try model.openDirectory("C:\\current", "keep");
    var result = PathCompletion.init(std.testing.allocator);
    defer result.deinit();
    result.index = .{ .deferred = .{} };
    try result.openDirectory("C:\\stale", "");
    try std.testing.expectError(error.MismatchedPathIndex, model.applyDeferredIndex(&result));
    try std.testing.expectEqualStrings("keep", model.query());
    model.failDeferredIndex();
    try std.testing.expect(!model.index.deferred.loading);
    try std.testing.expect(model.scan_error);
}

test "long pasted directory prefixes and full queries can still navigate" {
    var model = PathCompletion.init(std.testing.allocator);
    defer model.deinit();
    model.index = .{ .deferred = .{} };
    var text: std.ArrayList(u8) = .empty;
    defer text.deinit(std.testing.allocator);
    try text.appendSlice(std.testing.allocator, "C:\\");
    for (0..80) |_| try text.appendSlice(std.testing.allocator, "folder\\");
    const directory_end = text.items.len - 1;
    try text.appendSlice(std.testing.allocator, "Żółć.txt");
    try model.openDirectory("C:\\old", "");
    try model.appendQuery(text.items);
    try std.testing.expectEqualStrings(text.items[0..directory_end], model.directory);
    try std.testing.expectEqualStrings("Żółć.txt", model.query());
    const full_query = [_]u8{'a'} ** 128;
    try model.openDirectory("C:\\old", &full_query);
    try model.appendQuery("/");
    try std.testing.expect(std.mem.endsWith(u8, model.directory, &full_query));
    try std.testing.expectEqualStrings("", model.query());
}

test "path query navigation rolls back all allocation failures" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkPathQueryNavigation, .{});
}

fn checkPathQueryNavigation(allocator: std.mem.Allocator) !void {
    var model = PathCompletion.init(allocator);
    defer model.deinit();
    model.index = .{ .deferred = .{} };
    try model.openDirectory("C:\\original", "keep");
    const revision = model.index.deferred.revision;
    var long_text: [600]u8 = @splat('a');
    long_text[590] = '/';
    model.appendQuery(&long_text) catch |err| {
        try std.testing.expectEqualStrings("C:\\original", model.directory);
        try std.testing.expectEqualStrings("keep", model.query());
        try std.testing.expectEqual(revision, model.index.deferred.revision);
        return err;
    };
    try std.testing.expectEqualStrings("aaaaaaaaa", model.query());
}

test "unsafe query and path text cannot enter results or shell input" {
    var model = PathCompletion.init(std.testing.allocator);
    defer model.deinit();
    try model.openDirectory("", "keep");
    for ([_][]const u8{ "\n", "\x00", "\x7f", "\xc2\x85" }) |text| {
        try model.appendQuery(text);
        try std.testing.expectEqualStrings("keep", model.query());
        try std.testing.expectError(error.InvalidPathQuery, model.openDirectory("", text));
        try std.testing.expectEqualStrings("keep", model.query());
        try std.testing.expectError(error.ControlCharacterInPath, formatPathAlloc(std.testing.allocator, text, .wsl, false));
    }
    try std.testing.expectError(error.InvalidUtf8, formatPathAlloc(std.testing.allocator, "\xff", .pwsh, false));
}

test "path directory replacement rolls back every allocation failure" {
    var fixture = std.testing.tmpDir(.{});
    defer fixture.cleanup();
    try fixture.dir.makeDir("child");
    for ([_][]const u8{ "old-a.txt", "old-b.txt", "child/new.txt" }) |name| {
        const file = try fixture.dir.createFile(name, .{});
        file.close();
    }
    const root = try fixture.dir.realpathAlloc(std.testing.allocator, ".");
    defer std.testing.allocator.free(root);
    const child = try fixture.dir.realpathAlloc(std.testing.allocator, "child");
    defer std.testing.allocator.free(child);
    try std.testing.checkAllAllocationFailures(std.testing.allocator, checkPathDirectoryReplacement, .{ root, child });
}

fn checkPathDirectoryReplacement(allocator: std.mem.Allocator, root: []const u8, child: []const u8) !void {
    var completion = PathCompletion.init(allocator);
    defer completion.deinit();
    try completion.openDirectory(root, "old");
    try std.testing.expectEqual(@as(usize, 2), completion.matches.items.len);
    completion.selected_index = 1;
    completion.hovered_index = 1;
    completion.first_visible = 1;
    const previous = completion;
    const previous_matches = completion.matches.items[0..2].*;
    completion.openDirectory(child, "new") catch |err| {
        try std.testing.expectEqual(previous.directory.ptr, completion.directory.ptr);
        try std.testing.expectEqualStrings(root, completion.directory);
        try std.testing.expectEqualStrings("old", completion.query());
        try std.testing.expectEqual(previous.entries.items.ptr, completion.entries.items.ptr);
        try std.testing.expectEqual(previous.entries.items.len, completion.entries.items.len);
        try std.testing.expectEqual(previous.matches.items.ptr, completion.matches.items.ptr);
        try std.testing.expectEqualSlices(Match, &previous_matches, completion.matches.items);
        try std.testing.expectEqual(@as(usize, 1), completion.selected_index);
        try std.testing.expectEqual(@as(?usize, 1), completion.hovered_index);
        try std.testing.expectEqual(@as(usize, 1), completion.first_visible);
        return err;
    };
    try std.testing.expectEqualStrings(child, completion.directory);
    try std.testing.expectEqualStrings("new", completion.query());
    try std.testing.expectEqual(@as(usize, 1), completion.matches.items.len);
    try std.testing.expectEqual(@as(usize, 0), completion.selected_index);
    try std.testing.expectEqual(@as(?usize, null), completion.hovered_index);
    try std.testing.expectEqual(@as(usize, 0), completion.first_visible);
}

test "path directory inputs reject relative, drive-relative and malformed paths before mutation" {
    var completion = PathCompletion.init(std.testing.allocator);
    defer completion.deinit();
    try completion.openDirectory("", "keep");
    for ([_][]const u8{ "C:", "relative", "C:relative", "\\\\server", "C:\\bad\x00path", "C:\\bad\xffpath" }) |invalid| {
        try std.testing.expectError(error.InvalidDirectoryPath, completion.openDirectory(invalid, "replace"));
        try std.testing.expectEqualStrings("", completion.directory);
        try std.testing.expectEqualStrings("keep", completion.query());
    }
}

test "path directory navigation accepts borrowed paths and queries and marks inaccessible scans explicitly" {
    var fixture = std.testing.tmpDir(.{});
    defer fixture.cleanup();
    try fixture.dir.makeDir("child");
    const root = try fixture.dir.realpathAlloc(std.testing.allocator, ".");
    defer std.testing.allocator.free(root);
    const child = try fixture.dir.realpathAlloc(std.testing.allocator, "child");
    defer std.testing.allocator.free(child);
    var completion = PathCompletion.init(std.testing.allocator);
    defer completion.deinit();
    try completion.openDirectory(child, "keep");
    try completion.openDirectory(completion.directory, completion.query());
    try std.testing.expectEqualStrings(child, completion.directory);
    try std.testing.expectEqualStrings("keep", completion.query());
    try std.testing.expect(try completion.navigateParent());
    try std.testing.expectEqualStrings(root, completion.directory);
    try std.testing.expectEqualStrings("keep", completion.query());
    try completion.clearQuery();
    try std.testing.expect(try completion.enterSelectedDirectory());
    try std.testing.expectEqualStrings(child, completion.directory);
    try std.testing.expectEqualStrings("", completion.query());
    try std.testing.expect(!completion.scan_error);

    const missing = try std.fs.path.join(std.testing.allocator, &.{ root, "missing" });
    defer std.testing.allocator.free(missing);
    try completion.openDirectory(missing, "");
    try std.testing.expect(completion.scan_error);
    try std.testing.expectEqual(@as(usize, 0), completion.entries.items.len);
    try std.testing.expectEqual(@as(usize, 0), completion.matches.items.len);
}

test "path query limits never split a UTF-8 codepoint and backspace removes one codepoint" {
    var completion = PathCompletion.init(std.testing.allocator);
    defer completion.deinit();
    for ([_][]const u8{ "ż", "中", "😀" }) |character| {
        for (1..character.len) |missing| {
            var text: [132]u8 = @splat('a');
            const prefix_len = completion.query_buf.len - missing;
            @memcpy(text[prefix_len .. prefix_len + character.len], character);
            const input = text[0 .. prefix_len + character.len];
            try completion.openDirectory("", input);
            try std.testing.expectEqualStrings(input[0..prefix_len], completion.query());
            try std.testing.expect(std.unicode.utf8ValidateSlice(completion.query()));

            try completion.openDirectory("", input[0..prefix_len]);
            try completion.appendQuery(character);
            try std.testing.expectEqualStrings(input[0..prefix_len], completion.query());
        }
        try completion.openDirectory("", "");
        try completion.appendQuery(character);
        try std.testing.expectEqualStrings(character, completion.query());
        try std.testing.expect(try completion.backspaceQuery());
        try std.testing.expectEqualStrings("", completion.query());
    }
    var exact: [128]u8 = @splat('a');
    @memcpy(exact[124..], "😀");
    try completion.openDirectory("", &exact);
    try std.testing.expectEqualStrings(&exact, completion.query());
    try std.testing.expect(try completion.backspaceQuery());
    try std.testing.expectEqualStrings(exact[0..124], completion.query());
}

test "path queries reject malformed UTF-8 without changing existing query or results" {
    var completion = PathCompletion.init(std.testing.allocator);
    defer completion.deinit();
    try completion.openDirectory("", "keep");
    for ([_][]const u8{ "\xff", "\xc0\x80", "\xed\xa0\x80", "\xf4\x90\x80\x80", "\xe2\x82" }) |invalid| {
        try std.testing.expectError(error.InvalidUtf8, completion.appendQuery(invalid));
        try std.testing.expectEqualStrings("keep", completion.query());
        try std.testing.expectError(error.InvalidUtf8, completion.openDirectory("", invalid));
        try std.testing.expectEqualStrings("keep", completion.query());
    }
}

test "path completion keeps the best fifty matches regardless of directory enumeration order" {
    const allocator = std.testing.allocator;
    var completion = PathCompletion.init(allocator);
    defer completion.deinit();
    for (0..max_entries) |i| {
        const name = try std.fmt.allocPrint(allocator, "file-{d:0>5}", .{max_entries - i});
        errdefer allocator.free(name);
        try completion.entries.append(allocator, .{ .name = name, .is_directory = i % 7 == 0 });
    }

    const Oracle = struct {
        fn lessThan(entries: []const Entry, a: Match, b: Match) bool {
            if (a.score != b.score) return a.score > b.score;
            return std.ascii.lessThanIgnoreCase(entries[a.entry_index].name, entries[b.entry_index].name);
        }
    };
    var expected: std.ArrayList(Match) = .empty;
    defer expected.deinit(allocator);
    for ([_][]const u8{ "", "file", "f1", "000", "zz", "-", "file-00001" }) |q| {
        @memcpy(completion.query_buf[0..q.len], q);
        completion.query_len = q.len;
        expected.clearRetainingCapacity();
        for (completion.entries.items, 0..) |entry, index| {
            const score = scorePath(entry.name, entry.is_directory, q) orelse continue;
            try expected.append(allocator, .{ .entry_index = index, .score = score });
        }
        std.mem.sort(Match, expected.items, completion.entries.items, Oracle.lessThan);
        try completion.refreshMatches();
        try std.testing.expectEqualSlices(Match, expected.items[0..@min(max_matches, expected.items.len)], completion.matches.items);
    }
}

test "path completion preserves results on allocation failure and warm queries allocate nothing" {
    const allocator = std.testing.allocator;
    var completion = PathCompletion.init(allocator);
    defer completion.deinit();
    for (0..100) |i| {
        const name = try std.fmt.allocPrint(allocator, "file-{d:0>5}", .{i});
        errdefer allocator.free(name);
        try completion.entries.append(allocator, .{ .name = name, .is_directory = false });
    }
    const prior = Match{ .entry_index = 0, .score = 17 };
    try completion.matches.append(allocator, prior);
    completion.selected_index = 0;
    var failing = std.testing.FailingAllocator.init(allocator, .{ .fail_index = 0 });
    completion.allocator = failing.allocator();
    defer completion.allocator = allocator;
    try std.testing.expectError(error.OutOfMemory, completion.refreshMatches());
    try std.testing.expectEqualSlices(Match, &.{prior}, completion.matches.items);

    completion.allocator = allocator;
    try completion.refreshMatches();
    completion.allocator = failing.allocator();
    try completion.appendQuery("file-0");
    _ = try completion.backspaceQuery();
    try completion.clearQuery();
    try std.testing.expectEqual(max_matches, completion.matches.items.len);
}

test "path completion uses deterministic case ties at the result limit" {
    const allocator = std.testing.allocator;
    var completion = PathCompletion.init(allocator);
    defer completion.deinit();
    for (0..max_matches + 2) |i| {
        const name = try allocator.dupe(u8, if (i < max_matches) "file" else "FILE");
        errdefer allocator.free(name);
        try completion.entries.append(allocator, .{ .name = name, .is_directory = false });
    }
    try completion.refreshMatches();
    try std.testing.expectEqual(max_matches, completion.matches.items.len);
    try std.testing.expectEqual(max_matches, completion.matches.items[0].entry_index);
    try std.testing.expectEqual(max_matches + 1, completion.matches.items[1].entry_index);
    try std.testing.expectEqual(@as(usize, 0), completion.matches.items[2].entry_index);
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

test "path scoring preserves normalization and queries beyond the picker limit" {
    const path = "src/Terminal/My_File.zig";
    for ([_][]const u8{ "", "SRC", "my_file", "MyF", "src\\terminal/", "missing" }) |q| {
        var buf: [128]u8 = undefined;
        const normalized = normalizeQuerySlice(q, &buf);
        for ([_]bool{ false, true }) |is_directory| {
            try std.testing.expectEqual(
                scorePathImpl(path, is_directory, q, false),
                scorePathImpl(path, is_directory, normalized, true),
            );
        }
    }
    const long_path = [_]u8{'A'} ** 129;
    const long_query = [_]u8{'a'} ** 129;
    try std.testing.expectEqual(@as(?i32, 6212), scorePath(&long_path, false, &long_query));
    try std.testing.expectEqual(@as(?i32, 6222), scorePath(&long_path, true, &long_query));
    try std.testing.expectEqual(@as(?i32, null), scorePath(long_path[0..128], false, &long_query));
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

test "partial and final indexes preserve the selected path across reordered matches" {
    for ([_]bool{ true, false }) |partial| {
        var current = PathCompletion.init(std.testing.allocator);
        defer current.deinit();
        current.index = .{ .deferred = .{} };
        try current.openDirectory("C:\\review", "txt");
        for ([_][]const u8{ "b.txt", "c.txt" }) |name| _ = try current.appendIndexedEntry(name, false);
        try current.refreshMatches();
        current.selected_index = 1;
        current.first_visible = 1;
        current.hovered_index = 1;

        var update = PathCompletion.init(std.testing.allocator);
        defer update.deinit();
        update.index = .{ .deferred = .{} };
        try update.openDirectory("C:\\review", "");
        for ([_][]const u8{ "a.txt", "b.txt", "c.txt" }) |name| _ = try update.appendIndexedEntry(name, false);
        if (partial) try current.applyPartialIndex(&update) else try current.applyDeferredIndex(&update);

        try std.testing.expectEqualStrings("c.txt", current.selectedEntry().?.name);
        try std.testing.expectEqualStrings("txt", current.query());
        try std.testing.expectEqual(@as(usize, 2), current.selected_index);
        try std.testing.expectEqual(@as(usize, 1), current.first_visible);
        try std.testing.expectEqual(@as(?usize, null), current.hovered_index);
        try std.testing.expectEqual(partial, current.index.deferred.loading);

        var final = try current.cloneIndexAlloc();
        defer final.deinit();
        try current.applyDeferredIndex(&final);
        try std.testing.expectEqualStrings("c.txt", current.selectedEntry().?.name);
        try std.testing.expect(!current.index.deferred.loading);

        var empty = PathCompletion.init(std.testing.allocator);
        defer empty.deinit();
        empty.index = .{ .deferred = .{} };
        try empty.openDirectory("C:\\review", "");
        try current.applyDeferredIndex(&empty);
        try std.testing.expectEqual(@as(usize, 0), current.selected_index);
        try std.testing.expectEqual(@as(usize, 0), current.first_visible);
        try std.testing.expectEqual(@as(?Entry, null), current.selectedEntry());
    }
}

test "index snapshots preserve unresolved symbolic link metadata" {
    const Walker = struct {
        step: usize = 0,
        root_failed: bool = false,

        fn next(self: *@This()) !?struct { path: []const u8, kind: std.fs.Dir.Entry.Kind } {
            defer self.step += 1;
            return switch (self.step) {
                0 => .{ .path = "bad\nlink", .kind = .sym_link },
                1 => .{ .path = "link", .kind = .sym_link },
                2 => .{ .path = "file", .kind = .file },
                else => null,
            };
        }
    };
    var completion = PathCompletion.init(std.testing.allocator);
    defer completion.deinit();
    var walker = Walker{};
    try completion.scanWalker(&walker);
    try std.testing.expect(completion.has_skipped_entries);
    var snapshot = try completion.cloneIndexAlloc();
    defer snapshot.deinit();
    try std.testing.expectEqual(@as(usize, 2), snapshot.entries.items.len);
    try std.testing.expect(snapshot.entries.items[0].is_symbolic_link);
    try std.testing.expect(!snapshot.entries.items[0].is_directory);
    try std.testing.expect(!snapshot.entries.items[1].is_symbolic_link);
}

test "path picker opens directory symlinks without indexing link cycles" {
    var fixture = std.testing.tmpDir(.{ .iterate = true });
    defer fixture.cleanup();
    try fixture.dir.makeDir("child");
    const file = try fixture.dir.createFile("child/file.txt", .{});
    file.close();
    fixture.dir.symLink(".", "loop", .{ .is_directory = true }) catch |err| switch (err) {
        error.AccessDenied => return error.SkipZigTest,
        else => return err,
    };
    const root = try fixture.dir.realpathAlloc(std.testing.allocator, ".");
    defer std.testing.allocator.free(root);
    var completion = PathCompletion.init(std.testing.allocator);
    defer completion.deinit();
    completion.recursive = true;
    try completion.openDirectory(root, "loop");
    try std.testing.expectEqual(@as(usize, 3), completion.entries.items.len);
    try std.testing.expect(completion.selectedEntry().?.is_directory);
    try std.testing.expect(try completion.enterSelectedDirectory());
    try std.testing.expect(!completion.scan_error);
    try std.testing.expectEqual(@as(usize, 3), completion.entries.items.len);
}

test "incremental candidate filtering matches full rescan across query extensions and backspaces" {
    const allocator = std.testing.allocator;
    var incremental = PathCompletion.init(allocator);
    defer incremental.deinit();
    var baseline = PathCompletion.init(allocator);
    defer baseline.deinit();

    const samples = [_][]const u8{
        "src/zig/terminal/path_completion.zig",
        "src/zig/terminal/path_walker.zig",
        "src/zig/terminal/path_index.zig",
        "src/zig/shell/wsl_paths.zig",
        "src/zig/app/terminal_application.zig",
        "README.md",
        "build.zig",
    };
    for (samples, 0..) |sample, i| {
        try std.testing.expect(try incremental.appendIndexedEntry(sample, i % 3 == 0));
        try std.testing.expect(try baseline.appendIndexedEntry(sample, i % 3 == 0));
    }

    for ([_][]const u8{ "p", "a", "t", "h", "_", "c", "o" }) |ch| {
        try incremental.appendQuery(ch);
        baseline.candidate_query_len = 0;
        try baseline.setQuery(incremental.query());
        try std.testing.expectEqualSlices(Match, baseline.matches.items, incremental.matches.items);
    }
    while (incremental.query_len > 0) {
        _ = try incremental.backspaceQuery();
        baseline.candidate_query_len = 0;
        try baseline.setQuery(incremental.query());
        try std.testing.expectEqualSlices(Match, baseline.matches.items, incremental.matches.items);
    }
}
