const std = @import("std");

pub const Box = struct {
    first_row: u16,
    last_row: u16,
    start_col: u16,
    end_col: u16,
};

/// Snapshot only the occupied extent of each copied row. Blank rows and trailing
/// selection padding are not content; interior spaces remain part of the line.
/// Called once per copy, never on animation frames. Storage is reused by the owner.
pub fn collectContentSpans(allocator: std.mem.Allocator, spans: anytype, frame: anytype, box: ?Box) !void {
    spans.clearRetainingCapacity();
    if (frame.columns == 0 or frame.rows == 0) return;
    const first_row = if (box) |b| b.first_row else 0;
    const last_row = if (box) |b| @min(b.last_row, frame.rows - 1) else frame.rows - 1;
    const first_col = if (box) |b| b.start_col else 0;
    const last_col = if (box) |b| @min(b.end_col, frame.columns) else frame.columns;
    if (first_row > last_row or first_col >= last_col) return;
    for (first_row..@as(usize, last_row) + 1) |row| {
        var start: ?u16 = null;
        var end: u16 = 0;
        for (first_col..last_col) |col| {
            const cell = frame.cells.items[row * frame.columns + col];
            if (box == null and !cell.selected) continue;
            if (cell.width == .tail or whitespace(frame.cellText(cell))) continue;
            if (start == null) start = @intCast(col);
            end = @intCast(@min(last_col, col + @as(usize, if (cell.width == .wide) 2 else 1)));
        }
        if (start) |column| try spans.append(allocator, .{ .row = @intCast(row), .start_col = column, .end_col = end });
    }
}

/// Retain text, not render styles: TUIs often remove their own selection highlight
/// after copying. Reusing these buffers avoids allocating on every input event.
pub const Snapshot = struct {
    const Cell = struct {
        width: enum { narrow, wide, tail },
        text_start: usize,
        text_length: usize,
    };
    columns: u16 = 0,
    rows: u16 = 0,
    cells: std.ArrayList(Cell) = .empty,
    utf8: std.ArrayList(u8) = .empty,
    captured_ms: ?i64 = null,
    source_generation: ?u64 = null,

    pub fn deinit(self: *Snapshot, allocator: std.mem.Allocator) void {
        self.cells.deinit(allocator);
        self.utf8.deinit(allocator);
    }

    /// Generations belong to one terminal; cancellation expires attribution, not the retained pixels.
    pub fn captureVersioned(self: *Snapshot, allocator: std.mem.Allocator, frame: anytype, now_ms: i64) !void {
        if (self.source_generation == frame.generation and self.columns == frame.columns and self.rows == frame.rows) {
            self.captured_ms = now_ms;
            return;
        }
        try self.capture(allocator, frame, now_ms);
        self.source_generation = frame.generation;
    }

    pub fn capture(self: *Snapshot, allocator: std.mem.Allocator, frame: anytype, now_ms: i64) !void {
        self.captured_ms = null;
        self.source_generation = null;
        try self.cells.ensureTotalCapacity(allocator, frame.cells.items.len);
        try self.utf8.ensureTotalCapacity(allocator, frame.utf8.items.len);
        self.cells.clearRetainingCapacity();
        self.utf8.clearRetainingCapacity();
        self.utf8.appendSliceAssumeCapacity(frame.utf8.items);
        for (frame.cells.items) |cell| self.cells.appendAssumeCapacity(.{
            .width = switch (cell.width) {
                .wide => .wide,
                .tail => .tail,
                else => .narrow,
            },
            .text_start = cell.text_start,
            .text_length = cell.text_length,
        });
        self.columns = frame.columns;
        self.rows = frame.rows;
        self.captured_ms = now_ms;
    }

    pub fn cellText(self: *const Snapshot, cell: Cell) []const u8 {
        return self.utf8.items[cell.text_start..][0..cell.text_length];
    }

    /// Shared/ownerless relays have no pane identity. Very short payloads and
    /// stale input are insufficient evidence even if they happen to match text.
    pub fn acceptsUnattributed(self: *const Snapshot, now_ms: i64, text: []const u8) bool {
        const captured = self.captured_ms orelse return false;
        if (now_ms < captured or now_ms - captured > 750) return false;
        const trimmed = std.mem.trim(u8, text, " \t\r\n");
        return (std.unicode.utf8CountCodepoints(trimmed) catch return false) >= 4;
    }

    pub fn recent(self: *const Snapshot, now_ms: i64) bool {
        const captured = self.captured_ms orelse return false;
        return now_ms >= captured and now_ms - captured <= 2000;
    }
};

pub const settle_ms = 80;

/// Neither OSC nor the Windows clipboard says whether a write is a copy or a
/// delete. Require the source to exist in the same place before AND after the
/// operation, after allowing the TUI to finish its redraw. No key/app allowlist.
pub fn unchanged(allocator: std.mem.Allocator, before: *const Snapshot, after: anytype, payload: []const u8) ?Box {
    if (before.columns != after.columns or before.rows != after.rows) return null;
    if (locate(before, payload)) |old| {
        if (locate(after, payload)) |current| {
            if (std.meta.eql(old, current) and sameText(before, after, current)) return current;
        }
    }
    // Rich-text TUIs may copy presentation punctuation not present on screen
    // (or vice versa). A substantial, unique word sequence is a safe fallback
    // only when the ORIGINAL source cells are unchanged, punctuation included.
    var words: usize = 0;
    var letters: usize = 0;
    var in_word = false;
    const plain = allocator.dupe(u8, payload) catch return null;
    defer allocator.free(plain);
    for (plain) |*byte| {
        if (wordByte(byte.*)) {
            letters += 1;
            if (!in_word) words += 1;
            in_word = true;
        } else {
            in_word = false;
            if (byte.* != '\n' and byte.* != '\r') byte.* = ' ';
        }
    }
    if (words < 3 or letters < 12) return null;
    if (locate(plainFrame(before), plain)) |old| {
        if (locate(plainFrame(after), plain)) |current| {
            if (std.meta.eql(old, current) and sameText(before, after, current)) return current;
        }
    }
    // A copied response/file region can be larger than the viewport. Animate a
    // substantial contiguous visible excerpt, never a single incidental word.
    const old = locateExcerpt(allocator, before, payload) orelse return null;
    const current = locateExcerpt(allocator, after, payload) orelse return null;
    return if (std.meta.eql(old, current) and sameText(before, after, current)) current else null;
}

fn sameText(before: *const Snapshot, after: anytype, box: Box) bool {
    for (box.first_row..@as(usize, box.last_row) + 1) |row| {
        for (box.start_col..box.end_col) |col| {
            const index = row * before.columns + col;
            const old = before.cellText(before.cells.items[index]);
            const current = after.cellText(after.cells.items[index]);
            if (whitespace(old) and whitespace(current)) continue;
            if (!std.mem.eql(u8, old, current)) return false;
        }
    }
    return true;
}

fn wordByte(byte: u8) bool {
    return std.ascii.isAlphanumeric(byte) or byte >= 0x80;
}

fn PlainFrame(comptime Source: type) type {
    const Value = switch (@typeInfo(Source)) {
        .pointer => |pointer| pointer.child,
        else => Source,
    };
    return struct {
        source: Source,
        columns: u16,
        rows: u16,
        cells: @FieldType(Value, "cells"),
        utf8: @FieldType(Value, "utf8"),

        pub fn cellText(self: @This(), cell: anytype) []const u8 {
            const bytes = self.source.cellText(cell);
            for (bytes) |byte| if (wordByte(byte)) return bytes;
            return " ";
        }
    };
}

fn plainFrame(frame: anytype) PlainFrame(@TypeOf(frame)) {
    return .{ .source = frame, .columns = frame.columns, .rows = frame.rows, .cells = frame.cells, .utf8 = frame.utf8 };
}

const Canonical = struct {
    text: std.ArrayList(u8) = .empty,
    columns: std.ArrayList(u16) = .empty,

    fn deinit(self: *Canonical, allocator: std.mem.Allocator) void {
        self.text.deinit(allocator);
        self.columns.deinit(allocator);
    }

    fn append(self: *Canonical, allocator: std.mem.Allocator, bytes: []const u8, col: ?u16) !void {
        var offset: usize = 0;
        while (offset < bytes.len) {
            const length = std.unicode.utf8ByteSequenceLength(bytes[offset]) catch return;
            if (offset + length > bytes.len) return;
            const glyph = bytes[offset..][0..length];
            const cp = std.unicode.utf8Decode(glyph) catch return;
            // Generic presentation separators, including box-drawing borders.
            const significant = wordByte(bytes[offset]) and !(cp >= 0x2000 and cp <= 0x206f) and !(cp >= 0x2500 and cp <= 0x259f);
            if (significant) {
                try self.text.appendSlice(allocator, glyph);
                if (col) |column| for (glyph) |_| try self.columns.append(allocator, column);
            } else if (self.text.items.len > 0 and self.text.items[self.text.items.len - 1] != ' ') {
                try self.text.append(allocator, ' ');
                if (col) |column| try self.columns.append(allocator, column);
            }
            offset += length;
        }
    }

    fn slice(self: *const Canonical) []const u8 {
        return std.mem.trimEnd(u8, self.text.items, " ");
    }
};

const ExcerptRun = struct {
    box: Box,
    rows: usize,
    bytes: usize,
    payload_end: usize,
};

fn keepExcerpt(best: *?ExcerptRun, ambiguous: *bool, run: ?ExcerptRun) void {
    const candidate = run orelse return;
    if (candidate.rows < 2 or candidate.bytes < 48) return;
    if (best.* == null or candidate.bytes > best.*.?.bytes) {
        best.* = candidate;
        ambiguous.* = false;
    } else if (candidate.bytes == best.*.?.bytes) ambiguous.* = true;
}

fn locateExcerpt(allocator: std.mem.Allocator, frame: anytype, payload: []const u8) ?Box {
    var copied: Canonical = .{};
    defer copied.deinit(allocator);
    copied.append(allocator, payload, null) catch return null;
    const text = copied.slice();
    if (text.len < 48) return null;
    var row_text: Canonical = .{};
    defer row_text.deinit(allocator);
    var run: ?ExcerptRun = null;
    var best: ?ExcerptRun = null;
    var ambiguous = false;
    // Bound substring scanning for large clipboard payloads and repetitive rows.
    var budget: usize = 64 * 1024 * 1024;
    for (0..frame.rows) |r| {
        row_text.text.clearRetainingCapacity();
        row_text.columns.clearRetainingCapacity();
        for (frame.cells.items[r * frame.columns ..][0..frame.columns], 0..) |cell, col| {
            if (cell.width == .tail) continue;
            const raw = frame.cellText(cell);
            row_text.append(allocator, if (raw.len == 0) " " else raw, @intCast(col)) catch return null;
        }
        const row = row_text.slice();
        if (row.len == 0) continue;
        var start: usize = 0;
        var found: ?struct { offset: usize, start: usize } = null;
        while (start < row.len) {
            const excerpt = row[start..];
            if (excerpt.len < 12 or std.mem.count(u8, excerpt, " ") < 1) break;
            if (budget < text.len * 2) return null;
            budget -= text.len * 2;
            if (std.mem.indexOf(u8, text, excerpt)) |offset| {
                const end = offset + excerpt.len;
                const bounded = (offset == 0 or text[offset - 1] == ' ') and (end == text.len or text[end] == ' ');
                if (bounded and std.mem.indexOf(u8, text[offset + 1 ..], excerpt) == null) {
                    found = .{ .offset = offset, .start = start };
                    break;
                }
            }
            start = if (std.mem.indexOfScalarPos(u8, row, start, ' ')) |space| space + 1 else row.len;
        }
        if (found) |match| {
            const end_col = row_text.columns.items[row.len - 1];
            const last_cell = frame.cells.items[r * frame.columns + end_col];
            const box: Box = .{
                .first_row = @intCast(r),
                .last_row = @intCast(r),
                .start_col = row_text.columns.items[match.start],
                .end_col = @min(frame.columns, end_col + @as(u16, if (last_cell.width == .wide) 2 else 1)),
            };
            const length = row.len - match.start;
            if (run) |*previous| {
                if (match.offset >= previous.payload_end and std.mem.trim(u8, text[previous.payload_end..match.offset], " ").len == 0) {
                    previous.box.last_row = @intCast(r);
                    previous.box.start_col = @min(previous.box.start_col, box.start_col);
                    previous.box.end_col = @max(previous.box.end_col, box.end_col);
                    previous.rows += 1;
                    previous.bytes += length;
                    previous.payload_end = match.offset + length;
                    continue;
                }
                keepExcerpt(&best, &ambiguous, run);
            }
            run = .{ .box = box, .rows = 1, .bytes = length, .payload_end = match.offset + length };
        } else {
            keepExcerpt(&best, &ambiguous, run);
            run = null;
        }
    }
    keepExcerpt(&best, &ambiguous, run);
    return if (!ambiguous) (if (best) |candidate| candidate.box else null) else null;
}

const Match = struct { box: Box, end: usize };

fn whitespace(bytes: []const u8) bool {
    return bytes.len == 0 or std.mem.trim(u8, bytes, " \t\r\n").len == 0;
}

fn include(box: *Box, frame: anytype, index: usize, wide: bool) void {
    const col: u16 = @intCast(index % frame.columns);
    box.last_row = @intCast(index / frame.columns);
    box.start_col = @min(box.start_col, col);
    box.end_col = @max(box.end_col, @min(frame.columns, col + @as(u16, if (wide) 2 else 1)));
}

/// Match one logical line, allowing tabs/spaces and physical wrapping. A margin
/// handles editor gutters or a TUI's indentation on continuation rows.
fn lineAt(frame: anytype, line: []const u8, start: usize, margin: usize, budget: *usize) ?Match {
    var index = start;
    var pos: usize = 0;
    var box: Box = .{
        .first_row = @intCast(start / frame.columns),
        .last_row = @intCast(start / frame.columns),
        .start_col = @intCast(start % frame.columns),
        .end_col = @intCast(start % frame.columns),
    };
    while (pos < line.len and index < frame.cells.items.len) {
        if (budget.* == 0) return null;
        budget.* -= 1;
        const cell = frame.cells.items[index];
        if (cell.width == .tail) {
            index += 1;
            continue;
        }
        const bytes = frame.cellText(cell);
        if (whitespace(bytes)) {
            include(&box, frame, index, false);
            index += 1;
            // Padding at the end of a wrapped row need not exist in the
            // clipboard; internal spaces must not disappear inside words.
            if (pos < line.len and (line[pos] == ' ' or line[pos] == '\t')) {
                while (pos < line.len and (line[pos] == ' ' or line[pos] == '\t')) pos += 1;
                while (index < frame.cells.items.len and index % frame.columns != 0 and whitespace(frame.cellText(frame.cells.items[index]))) : (index += 1) {
                    if (budget.* == 0) return null;
                    budget.* -= 1;
                }
            } else {
                const row_end = (index / frame.columns + @intFromBool(index % frame.columns != 0)) * frame.columns;
                while (index < row_end and whitespace(frame.cellText(frame.cells.items[index]))) : (index += 1) {
                    if (budget.* == 0) return null;
                    budget.* -= 1;
                }
                if (index != row_end) return null;
            }
        } else {
            if (!std.mem.startsWith(u8, line[pos..], bytes)) return null;
            include(&box, frame, index, cell.width == .wide);
            pos += bytes.len;
            index += 1;
            if (index < frame.cells.items.len and frame.cells.items[index].width == .tail) index += 1;
        }
        if (pos < line.len and index % frame.columns == 0) {
            index += margin;
            while (index < frame.cells.items.len and index % frame.columns != 0 and whitespace(frame.cellText(frame.cells.items[index]))) : (index += 1) {
                if (budget.* == 0) return null;
                budget.* -= 1;
            }
            while (pos < line.len and (line[pos] == ' ' or line[pos] == '\t')) pos += 1;
        }
    }
    if (pos != line.len) return null;
    return .{ .box = box, .end = index };
}

fn matchAt(frame: anytype, text: []const u8, start: usize, margin: usize, budget: *usize) ?Box {
    if (text.len >= budget.*) {
        budget.* = 0;
        return null;
    }
    budget.* -= text.len;
    var lines = std.mem.tokenizeAny(u8, text, "\r\n");
    const first = std.mem.trim(u8, lines.next() orelse return null, " \t");
    var match = lineAt(frame, first, start, margin, budget) orelse return null;
    var box = match.box;
    while (lines.next()) |raw| {
        const line = std.mem.trim(u8, raw, " \t");
        if (line.len == 0) continue;
        var row = (match.end + frame.columns - 1) / frame.columns;
        // Hard lines may have blank rows between them (paragraph spacing).
        // Only skip rows blank after the same gutter/indentation margin.
        while (row < frame.rows) : (row += 1) {
            const base = row * frame.columns;
            var next: ?Match = null;
            for (0..frame.columns) |col| {
                if (budget.* == 0) return null;
                budget.* -= 1;
                const cell = frame.cells.items[base + col];
                if (cell.width == .tail or whitespace(frame.cellText(cell))) continue;
                if (!std.mem.startsWith(u8, line, frame.cellText(cell))) continue;
                if (lineAt(frame, line, base + col, margin, budget)) |found| {
                    if (next != null) return null;
                    next = found;
                }
            }
            if (next) |found| {
                match = found;
                box.last_row = found.box.last_row;
                box.start_col = @min(box.start_col, found.box.start_col);
                box.end_col = @max(box.end_col, found.box.end_col);
                break;
            }
            var blank = true;
            for (frame.cells.items[base + margin .. base + frame.columns]) |cell| {
                if (budget.* == 0) return null;
                budget.* -= 1;
                if (!whitespace(frame.cellText(cell))) blank = false;
            }
            if (!blank) return null;
        } else return null;
    }
    return box;
}

/// Locate all copied text, not just a prefix or cursor fallback. Formatting
/// whitespace, CRLF, soft wraps and per-row gutters need not match byte-for-byte.
/// Ambiguous matches and excessive work fail closed.
pub fn locate(frame: anytype, payload: []const u8) ?Box {
    const text = std.mem.trim(u8, payload, " \t\r\n");
    if (text.len == 0 or frame.columns == 0) return null;
    if (text.len > frame.utf8.items.len + frame.cells.items.len + frame.rows * @as(usize, 2)) return null;
    var result: ?Box = null;
    var budget: usize = 1024 * 1024;
    for (frame.cells.items, 0..) |cell, start| {
        if (cell.width == .tail or whitespace(frame.cellText(cell))) continue;
        if (!std.mem.startsWith(u8, text, frame.cellText(cell))) continue;
        const col = start % frame.columns;
        const found = matchAt(frame, text, start, 0, &budget) orelse (if (col != 0) matchAt(frame, text, start, col, &budget) else null) orelse continue;
        if (result != null) return null;
        result = found;
    }
    if (budget == 0) return null;
    return result;
}

const TestFrame = struct {
    const Cell = struct { width: enum { narrow, wide, tail } = .narrow, text_start: usize, text_length: usize = 1, selected: bool = true };
    columns: u16,
    rows: u16,
    cells: struct { items: []const Cell },
    utf8: struct { items: []const u8 },
    pub fn cellText(self: @This(), cell: Cell) []const u8 {
        return self.utf8.items[cell.text_start..][0..cell.text_length];
    }
};

fn asciiFrame(text: []const u8, cells: []TestFrame.Cell, columns: u16) TestFrame {
    for (cells, 0..) |*cell, i| cell.* = .{ .text_start = i };
    return .{ .columns = columns, .rows = @intCast(cells.len / columns), .cells = .{ .items = cells }, .utf8 = .{ .items = text } };
}

test "copy shimmer excludes selected blank rows and per-line trailing padding" {
    var cells: [60]TestFrame.Cell = undefined;
    const frame = asciiFrame("            " ++ "  alpha     " ++ "            " ++ " beta gamma " ++ "            ", &cells, 12);
    const Span = struct { row: u16, start_col: u16, end_col: u16 };
    var spans: std.ArrayList(Span) = .empty;
    defer spans.deinit(std.testing.allocator);
    try collectContentSpans(std.testing.allocator, &spans, frame, null);
    try std.testing.expectEqualSlices(Span, &.{
        .{ .row = 1, .start_col = 2, .end_col = 7 },
        .{ .row = 3, .start_col = 1, .end_col = 11 },
    }, spans.items);
    // Application-managed copies use the same content mask as native selections.
    try collectContentSpans(std.testing.failing_allocator, &spans, frame, .{ .first_row = 0, .last_row = 4, .start_col = 0, .end_col = 12 });
    try std.testing.expectEqual(@as(usize, 2), spans.items.len);
    for (&cells) |*cell| cell.selected = false;
    try collectContentSpans(std.testing.allocator, &spans, frame, null);
    try std.testing.expectEqual(@as(usize, 0), spans.items.len);
}

test "copy shimmer keeps wide glyphs and combining marks without a 64-row cap" {
    const cells = [_]TestFrame.Cell{
        .{ .width = .wide, .text_start = 0, .text_length = 3 },
        .{ .width = .tail, .text_start = 3, .text_length = 0 },
        .{ .text_start = 3, .text_length = 3 },
        .{ .text_start = 6, .text_length = 1 },
    };
    const frame: TestFrame = .{ .columns = 4, .rows = 1, .cells = .{ .items = &cells }, .utf8 = .{ .items = "界e\xcc\x81 " } };
    const Span = struct { row: u16, start_col: u16, end_col: u16 };
    var spans: std.ArrayList(Span) = .empty;
    defer spans.deinit(std.testing.allocator);
    try collectContentSpans(std.testing.allocator, &spans, frame, null);
    try std.testing.expectEqual(@as(u16, 3), spans.items[0].end_col);
    var tall_cells: [130]TestFrame.Cell = undefined;
    const tall = asciiFrame(&([_]u8{ 'x', ' ' } ** 65), &tall_cells, 2);
    try collectContentSpans(std.testing.allocator, &spans, tall, null);
    try std.testing.expectEqual(@as(usize, 65), spans.items.len);
    try std.testing.expectEqual(@as(u16, 64), spans.items[64].row);
}

test "unattributed clipboard relays require fresh input and reject incidental Unicode matches" {
    var source: Snapshot = .{};
    try std.testing.expect(!source.acceptsUnattributed(1000, "copied text"));
    source.captured_ms = 1000;
    try std.testing.expect(source.acceptsUnattributed(1750, "copied text"));
    try std.testing.expect(!source.acceptsUnattributed(1751, "copied text"));
    try std.testing.expect(!source.acceptsUnattributed(999, "copied text"));
    for ([_][]const u8{ "", " \t\r\n", "x", "界", "🦀", "abc" }) |text| {
        try std.testing.expect(!source.acceptsUnattributed(1100, text));
    }
    try std.testing.expect(source.acceptsUnattributed(1100, "Zażółć"));
    source.captured_ms = null;
    try std.testing.expect(!source.acceptsUnattributed(1100, "copied text"));
}

test "multiline copies allow gutters, indentation, CRLF and blank rows" {
    var cells: [60]TestFrame.Cell = undefined;
    const frame = asciiFrame(" 1  alpha      " ++ " 2    beta     " ++ " 3             " ++ " 4  gamma      ", &cells, 15);
    const box = locate(frame, "alpha\r\n  beta\n\ngamma\n").?;
    try std.testing.expectEqual(@as(u16, 0), box.first_row);
    try std.testing.expectEqual(@as(u16, 3), box.last_row);
}

test "single lines, soft wraps and whitespace reflow" {
    var cells: [30]TestFrame.Cell = undefined;
    const frame = asciiFrame("hello     world     unique    ", &cells, 10);
    try std.testing.expectEqual(@as(u16, 1), locate(frame, "hello world").?.last_row);
    try std.testing.expectEqual(@as(u16, 1), locate(frame, "hello\tworld").?.last_row);
    try std.testing.expectEqual(@as(u16, 2), locate(frame, "unique").?.first_row);
    try std.testing.expect(locate(frame, "helloworldx") == null);
    try std.testing.expect(locate(frame, " \r\n") == null);
}

test "versioned input snapshots reuse storage after cancellation and refresh changed frames" {
    var cells: [10]TestFrame.Cell = undefined;
    const frame = asciiFrame("hello     ", &cells, 10);
    const VersionedFrame = struct {
        columns: u16,
        rows: u16,
        cells: @TypeOf(frame.cells),
        utf8: @TypeOf(frame.utf8),
        generation: u64 = 1,
    };
    var versioned: VersionedFrame = .{ .columns = frame.columns, .rows = frame.rows, .cells = frame.cells, .utf8 = frame.utf8 };
    var snapshot: Snapshot = .{};
    defer snapshot.deinit(std.testing.allocator);
    try snapshot.captureVersioned(std.testing.allocator, versioned, 1000);
    const original_cells = snapshot.cells.items.ptr;
    snapshot.captured_ms = null;
    // Reuse must not touch the allocator, even if allocation would fail.
    try snapshot.captureVersioned(std.testing.failing_allocator, versioned, 2000);
    try std.testing.expectEqual(original_cells, snapshot.cells.items.ptr);
    try std.testing.expect(snapshot.recent(2001));
    try std.testing.expectEqualStrings("hello     ", snapshot.utf8.items);
    versioned.generation += 1;
    versioned.utf8.items = "world     ";
    try snapshot.captureVersioned(std.testing.allocator, versioned, 3000);
    try std.testing.expectEqualStrings("world     ", snapshot.utf8.items);
    // Dimensions are part of the key even if a caller reuses a generation.
    versioned.columns = 5;
    versioned.rows = 2;
    try snapshot.captureVersioned(std.testing.allocator, versioned, 3500);
    try std.testing.expectEqual(@as(u16, 5), snapshot.columns);
    try std.testing.expectEqual(@as(u16, 2), snapshot.rows);
    // An unversioned capture invalidates the key even when dimensions match.
    versioned.columns = frame.columns;
    versioned.rows = frame.rows;
    try snapshot.capture(std.testing.allocator, frame, 4000);
    try snapshot.captureVersioned(std.testing.allocator, versioned, 5000);
    try std.testing.expectEqualStrings("world     ", snapshot.utf8.items);
}

test "deletes, changes, moved text, duplicates and missing suffixes never authorize feedback" {
    var cells: [20]TestFrame.Cell = undefined;
    const frame = asciiFrame("hello     world     ", &cells, 10);
    var before: Snapshot = .{};
    defer before.deinit(std.testing.allocator);
    try before.capture(std.testing.allocator, frame, 1000);
    try std.testing.expect(before.recent(2999));
    try std.testing.expect(!before.recent(3001));
    try std.testing.expect(unchanged(std.testing.allocator, &before, frame, "hello world") != null);
    var after = frame;
    after.utf8.items = "          world     ";
    try std.testing.expect(unchanged(std.testing.allocator, &before, after, "hello") == null);
    after.utf8.items = "world     hello     ";
    try std.testing.expect(unchanged(std.testing.allocator, &before, after, "hello") == null);
    after.utf8.items = "hello     hello     ";
    try std.testing.expect(locate(after, "hello") == null);
    try std.testing.expect(locate(frame, "hello world missing") == null);
}

test "formatted text fallback is substantial, unique and checks original punctuation" {
    var cells: [30]TestFrame.Cell = undefined;
    const frame = asciiFrame("alpha beta gamma              ", &cells, 30);
    var before: Snapshot = .{};
    defer before.deinit(std.testing.allocator);
    try before.capture(std.testing.allocator, frame, 1000);
    try std.testing.expect(unchanged(std.testing.allocator, &before, frame, "**alpha** _beta_ `gamma`") != null);
    try std.testing.expect(unchanged(std.testing.allocator, &before, frame, "**alpha**") == null);
    const punctuated = asciiFrame("alpha+beta+gamma              ", &cells, 30);
    try before.capture(std.testing.allocator, punctuated, 1001);
    try std.testing.expect(unchanged(std.testing.allocator, &before, frame, "alpha beta gamma") == null);
}

test "copies taller than 64 rows and wrapped editor gutters" {
    var cells: [650]TestFrame.Cell = undefined;
    var text: [650]u8 = @splat(' ');
    var payload: [129]u8 = undefined;
    for (0..65) |row| {
        text[row * 10] = @intCast('!' + row);
        payload[row * 2] = text[row * 10];
        if (row < 64) payload[row * 2 + 1] = '\n';
    }
    const tall = asciiFrame(&text, &cells, 10);
    try std.testing.expectEqual(@as(u16, 64), locate(tall, &payload).?.last_row);
    var wrapped_cells: [30]TestFrame.Cell = undefined;
    const wrapped = asciiFrame(" 1 abcdefg" ++ "   hijklmn" ++ "   opqr   ", &wrapped_cells, 10);
    try std.testing.expectEqual(@as(u16, 2), locate(wrapped, "abcdefghijklmnopqr").?.last_row);
}

test "large copies animate only a substantial unchanged visible excerpt" {
    var cells: [80]TestFrame.Cell = undefined;
    const first = "alpha beta gamma delta epsilon          ";
    const second = "zeta eta theta iota kappa lambda         ";
    const frame = asciiFrame(first ++ second, &cells, 40);
    const payload = "older content outside the viewport\n**alpha** beta gamma delta epsilon zeta eta theta iota kappa lambda\nnewer content outside the viewport";
    var before: Snapshot = .{};
    defer before.deinit(std.testing.allocator);
    try before.capture(std.testing.allocator, frame, 1000);
    const box = unchanged(std.testing.allocator, &before, frame, payload).?;
    try std.testing.expectEqual(@as(u16, 0), box.first_row);
    try std.testing.expectEqual(@as(u16, 1), box.last_row);
    var after = frame;
    after.utf8.items = first ++ "                                        ";
    try std.testing.expect(unchanged(std.testing.allocator, &before, after, payload) == null);
    try std.testing.expect(unchanged(std.testing.allocator, &before, frame, "unrelated alpha beta gamma delta epsilon unrelated") == null);
}

test "Unicode graphemes and wide tails retain cell coordinates" {
    const cells = [_]TestFrame.Cell{
        .{ .width = .wide, .text_start = 0, .text_length = 3 },
        .{ .width = .tail, .text_start = 3, .text_length = 0 },
        .{ .text_start = 3, .text_length = 3 },
    };
    const frame: TestFrame = .{ .columns = 3, .rows = 1, .cells = .{ .items = &cells }, .utf8 = .{ .items = "界e\xcc\x81" } };
    try std.testing.expectEqual(@as(u16, 2), locate(frame, "界").?.end_col);
    try std.testing.expectEqual(@as(u16, 3), locate(frame, "界e\xcc\x81").?.end_col);
}
