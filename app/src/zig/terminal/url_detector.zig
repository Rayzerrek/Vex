const std = @import("std");

/// Half-open byte range `[start, end)` within a single row's UTF-8 text buffer.
pub const ByteSpan = struct {
    start: usize,
    end: usize,
};

/// Inclusive cell column range `[start_col, end_col]` for a detected link on a terminal row.
pub const RowLinkMatch = struct {
    start_col: u16,
    end_col: u16,
    url_len: usize,
};

/// Fast scan for `https://`, `http://`, `file://`, and `www.` URLs within a UTF-8 line.
pub fn findUrlSpans(text: []const u8, out: []ByteSpan) usize {
    if (text.len < 4 or out.len == 0) return 0;
    if (std.mem.indexOfScalar(u8, text, ':') == null and std.ascii.indexOfIgnoreCase(text, "www.") == null) {
        return 0;
    }

    var count: usize = 0;
    var i: usize = 0;
    while (i < text.len and count < out.len) {
        if (matchPrefixAt(text, i)) |prefix_len| {
            const start = i;
            var end = i + prefix_len;
            while (end < text.len and !isUrlBoundary(text[end])) : (end += 1) {}
            const trimmed_end = trimLinkEnd(text, start, end);
            if (trimmed_end > start + prefix_len and isValidBody(text[start..trimmed_end], prefix_len)) {
                out[count] = .{ .start = start, .end = trimmed_end };
                count += 1;
                i = trimmed_end;
                continue;
            }
            i += prefix_len;
        } else {
            i += 1;
        }
    }
    return count;
}

fn matchPrefixAt(text: []const u8, index: usize) ?usize {
    const rem = text[index..];
    if (rem.len < 5) return null;
    if (index > 0 and isWordOrSchemePredecessor(text[index - 1])) return null;

    if (std.ascii.startsWithIgnoreCase(rem, "https://")) return 8;
    if (std.ascii.startsWithIgnoreCase(rem, "http://")) return 7;
    if (std.ascii.startsWithIgnoreCase(rem, "file://")) return 7;
    if (std.ascii.startsWithIgnoreCase(rem, "www.")) {
        if (index > 0 and (text[index - 1] == '/' or text[index - 1] == '.')) return null;
        return 4;
    }
    return null;
}

fn isWordOrSchemePredecessor(b: u8) bool {
    return std.ascii.isAlphanumeric(b) or b == '_';
}

fn isUrlBoundary(b: u8) bool {
    return b <= 0x20 or b == 0x7f or b == '<' or b == '>' or b == '"' or b == '\'' or b == '`';
}

fn isValidBody(matched: []const u8, prefix_len: usize) bool {
    const body = matched[prefix_len..];
    if (body.len == 0) return false;
    if (prefix_len == 4) {
        // "www." links require at least one domain label character and a dot or slash.
        return body.len >= 3 and body[0] != '.' and (std.mem.indexOfScalar(u8, body, '.') != null or std.mem.indexOfScalar(u8, body, '/') != null);
    }
    return true;
}

/// Strips trailing punctuation from a URL match while preserving balanced parentheses/brackets.
pub fn trimLinkEnd(text: []const u8, start: usize, initial_end: usize) usize {
    var end = initial_end;
    var counted = false;
    var opens: [3]usize = @splat(0);
    var closes: [3]usize = @splat(0);
    while (end > start) {
        const ch = text[end - 1];
        if (ch == '.' or ch == ',' or ch == ';' or ch == ':' or ch == '!' or ch == '?' or ch == '\'' or ch == '"') {
            end -= 1;
            continue;
        }
        const bracket: usize = switch (ch) {
            ')' => 0,
            ']' => 1,
            '}' => 2,
            else => break,
        };
        if (!counted) {
            // Count once: a long suffix of unmatched closers must not rescan
            // the whole URL for every byte removed.
            for (text[start..end]) |byte| {
                switch (byte) {
                    '(' => opens[0] += 1,
                    '[' => opens[1] += 1,
                    '{' => opens[2] += 1,
                    ')' => closes[0] += 1,
                    ']' => closes[1] += 1,
                    '}' => closes[2] += 1,
                    else => {},
                }
            }
            counted = true;
        }
        if (opens[bracket] < closes[bracket]) {
            closes[bracket] -= 1;
            end -= 1;
            continue;
        }
        break;
    }
    return end;
}

/// Normalizes a matched URL into `out_buf` (prepending `https://` for `www.` links).
pub fn normalizeUrl(raw: []const u8, out_buf: []u8) ?[]const u8 {
    if (raw.len == 0 or std.mem.indexOfScalar(u8, raw, 0) != null or !std.unicode.utf8ValidateSlice(raw)) return null;
    if (std.ascii.startsWithIgnoreCase(raw, "www.")) {
        const prefix = "https://";
        if (prefix.len + raw.len > out_buf.len) return null;
        @memcpy(out_buf[0..prefix.len], prefix);
        @memcpy(out_buf[prefix.len .. prefix.len + raw.len], raw);
        return out_buf[0 .. prefix.len + raw.len];
    }
    if (std.ascii.startsWithIgnoreCase(raw, "https://") or
        std.ascii.startsWithIgnoreCase(raw, "http://") or
        std.ascii.startsWithIgnoreCase(raw, "file://"))
    {
        if (raw.len > out_buf.len) return null;
        @memcpy(out_buf[0..raw.len], raw);
        return out_buf[0..raw.len];
    }
    return null;
}

/// Scans a single row in `frame` and returns the link covering `column`, optionally writing the normalized URL into `out_url`.
pub fn findLinkInFrameRow(frame: anytype, row: u16, column: u16, out_url: ?[]u8) ?RowLinkMatch {
    if (row >= frame.rows or column >= frame.columns or frame.columns == 0) return null;
    const row_offset = @as(usize, row) * @as(usize, frame.columns);
    if (row_offset + frame.columns > frame.cells.items.len) return null;
    const cells = frame.cells.items[row_offset .. row_offset + frame.columns];

    var row_text: [4096]u8 = undefined;
    var byte_to_col: [4096]u16 = undefined;
    var len: usize = 0;

    for (cells, 0..) |cell, col_idx| {
        if (cell.width == .tail) continue;
        const text = frame.cellText(cell);
        if (text.len == 0) {
            if (len >= row_text.len) break;
            row_text[len] = ' ';
            byte_to_col[len] = @intCast(col_idx);
            len += 1;
        } else {
            if (len + text.len > row_text.len) break;
            @memcpy(row_text[len .. len + text.len], text);
            @memset(byte_to_col[len .. len + text.len], @intCast(col_idx));
            len += text.len;
        }
    }

    var spans: [32]ByteSpan = undefined;
    const span_count = findUrlSpans(row_text[0..len], &spans);
    for (spans[0..span_count]) |span| {
        if (span.start >= span.end or span.end > len) continue;
        const start_col = byte_to_col[span.start];
        const last_col = byte_to_col[span.end - 1];
        const end_col: u16 = if (cells[last_col].width == .wide and last_col + 1 < frame.columns)
            last_col + 1
        else
            last_col;
        if (column >= start_col and column <= end_col) {
            var url_len: usize = 0;
            if (out_url) |buf| {
                const normalized = normalizeUrl(row_text[span.start..span.end], buf) orelse return null;
                url_len = normalized.len;
            }
            return .{
                .start_col = start_col,
                .end_col = end_col,
                .url_len = url_len,
            };
        }
    }
    return null;
}

test "findUrlSpans detects http, https, file, and www links while trimming punctuation and preserving balanced parens" {
    var spans: [8]ByteSpan = undefined;
    const line1 = "See https://example.com/path?a=1&b=2, and (https://en.wikipedia.org/wiki/Zig_(programming_language)).";
    const count1 = findUrlSpans(line1, &spans);
    try std.testing.expectEqual(@as(usize, 2), count1);
    try std.testing.expectEqualStrings("https://example.com/path?a=1&b=2", line1[spans[0].start..spans[0].end]);
    try std.testing.expectEqualStrings("https://en.wikipedia.org/wiki/Zig_(programming_language)", line1[spans[1].start..spans[1].end]);

    const line2 = "Visit www.github.com/ziglang/zig! Or <http://localhost:3000/test> and [file:///C:/Users/test/index.html]";
    const count2 = findUrlSpans(line2, &spans);
    try std.testing.expectEqual(@as(usize, 3), count2);
    try std.testing.expectEqualStrings("www.github.com/ziglang/zig", line2[spans[0].start..spans[0].end]);
    try std.testing.expectEqualStrings("http://localhost:3000/test", line2[spans[1].start..spans[1].end]);
    try std.testing.expectEqualStrings("file:///C:/Users/test/index.html", line2[spans[2].start..spans[2].end]);

    var norm_buf: [256]u8 = undefined;
    try std.testing.expectEqualStrings("https://www.github.com/ziglang/zig", normalizeUrl(line2[spans[0].start..spans[0].end], &norm_buf).?);
}

test "URL trimming preserves scalar behavior across mixed brackets and punctuation" {
    const Reference = struct {
        fn trim(text: []const u8, start: usize, initial_end: usize) usize {
            var end = initial_end;
            while (end > start) {
                const ch = text[end - 1];
                if (std.mem.indexOfScalar(u8, ".,;:!?'\"", ch) != null) {
                    end -= 1;
                    continue;
                }
                const open: u8 = switch (ch) {
                    ')' => '(',
                    ']' => '[',
                    '}' => '{',
                    else => break,
                };
                if (std.mem.count(u8, text[start .. end - 1], &.{open}) >= std.mem.count(u8, text[start..end], &.{ch})) break;
                end -= 1;
            }
            return end;
        }
    };
    var prng = std.Random.DefaultPrng.init(0);
    const alphabet = "()[]{}.,;:!?'\"abc";
    var text: [128]u8 = undefined;
    for (0..500) |_| {
        for (&text) |*byte| byte.* = alphabet[prng.random().uintLessThan(usize, alphabet.len)];
        for (0..text.len + 1) |end| {
            const start = end / 3;
            try std.testing.expectEqual(Reference.trim(&text, start, end), trimLinkEnd(&text, start, end));
        }
    }
    var long: [4096]u8 = @splat(')');
    @memcpy(long[0..4], "url(");
    try std.testing.expectEqual(@as(usize, 5), trimLinkEnd(&long, 0, long.len));
}
