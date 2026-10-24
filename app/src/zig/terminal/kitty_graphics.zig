const std = @import("std");
const binding = @import("api.zig");
const c = binding.c;
const check = binding.checkResult;

pub const storage_limit: u64 = 32 * 1024 * 1024;
pub const Image = struct {
    generation: u64,
    width: u32,
    height: u32,
    rgba: []u8,
    used: bool = false,
};
pub const Placement = struct {
    generation: u64,
    image_id: u32,
    placement_id: u32,
    z: i32,
    column: i32,
    row: i32,
    x_offset: u32,
    y_offset: u32,
    width: u32,
    height: u32,
    source_x: u32,
    source_y: u32,
    source_width: u32,
    source_height: u32,
};

/// Owned pixels cross the snapshot boundary; no borrowed Ghostty handles reach drawing code.
pub const Snapshot = struct {
    images: std.ArrayList(Image) = .empty,
    placements: std.ArrayList(Placement) = .empty,
    generation: u64 = 0,
    valid: bool = false,

    pub fn deinit(self: *Snapshot, allocator: std.mem.Allocator) void {
        for (self.images.items) |image| allocator.free(image.rgba);
        self.images.deinit(allocator);
        self.placements.deinit(allocator);
    }

    fn makeRoom(self: *Snapshot, allocator: std.mem.Allocator, extra: usize) bool {
        var bytes = extra;
        for (self.images.items) |image| bytes += image.rgba.len;
        while (self.images.items.len >= 128 or bytes > storage_limit * 2) {
            var unused: ?usize = null;
            for (self.images.items, 0..) |image, index| {
                if (!image.used) {
                    unused = index;
                    break;
                }
            }
            const old = self.images.swapRemove(unused orelse return false);
            bytes -= old.rgba.len;
            allocator.free(old.rgba);
        }
        return true;
    }

    pub fn update(self: *Snapshot, allocator: std.mem.Allocator, api: *const binding.Api, terminal: c.GhosttyTerminal, iterator_ptr: *c.GhosttyKittyGraphicsPlacementIterator, geometry_dirty: bool) !void {
        var graphics: c.GhosttyKittyGraphics = null;
        try check(api.terminal_get(terminal, c.GHOSTTY_TERMINAL_DATA_KITTY_GRAPHICS, @ptrCast(&graphics)));
        var generation: u64 = 0;
        try check(api.kitty_graphics_get(graphics, c.GHOSTTY_KITTY_GRAPHICS_DATA_GENERATION, &generation));
        if (self.valid and self.generation == generation and !geometry_dirty) return;
        self.valid = false;
        self.placements.clearRetainingCapacity();
        for (self.images.items) |*image| image.used = false;
        if (generation == 0 and self.images.items.len == 0) {
            self.generation = generation;
            self.valid = true;
            return;
        }
        if (iterator_ptr.* == null) try check(api.kitty_graphics_placement_iterator_new(null, iterator_ptr));
        const iterator = iterator_ptr.*;
        try check(api.kitty_graphics_get(graphics, c.GHOSTTY_KITTY_GRAPHICS_DATA_PLACEMENT_ITERATOR, @ptrCast(iterator_ptr)));
        var visited: usize = 0;
        while (api.kitty_graphics_placement_next(iterator)) {
            // Bound work even when a sender reuses one tiny image for many placements.
            visited += 1;
            if (visited > 4096) break;
            var image_id: u32 = 0;
            try check(api.kitty_graphics_placement_get(iterator, c.GHOSTTY_KITTY_GRAPHICS_PLACEMENT_DATA_IMAGE_ID, &image_id));
            const handle = api.kitty_graphics_image(graphics, image_id) orelse continue;
            var info: c.GhosttyKittyGraphicsPlacementRenderInfo = std.mem.zeroes(c.GhosttyKittyGraphicsPlacementRenderInfo);
            info.size = @sizeOf(c.GhosttyKittyGraphicsPlacementRenderInfo);
            try check(api.kitty_graphics_placement_render_info(iterator, handle, terminal, &info));
            if (!info.viewport_visible or info.pixel_width == 0 or info.pixel_height == 0) continue;
            var image_generation: u64 = 0;
            try check(api.kitty_graphics_image_get(handle, c.GHOSTTY_KITTY_IMAGE_DATA_GENERATION, &image_generation));
            var image: ?*Image = null;
            for (self.images.items) |*cached| {
                if (cached.generation == image_generation) {
                    image = cached;
                    break;
                }
            }
            if (image == null) {
                // Reject a full working set before copying pixels, including repeated placements of an uncached image.
                if (!self.makeRoom(allocator, 0)) continue;
                const decoded = try readImage(allocator, api, handle, image_generation) orelse continue;
                errdefer allocator.free(decoded.rgba);
                // Old snapshots may coexist briefly with replacements, but never grow unbounded.
                if (!self.makeRoom(allocator, decoded.rgba.len)) {
                    allocator.free(decoded.rgba);
                    continue;
                }
                try self.images.append(allocator, decoded);
                image = &self.images.items[self.images.items.len - 1];
            }
            image.?.used = true;
            var placement: Placement = .{
                .generation = image_generation,
                .image_id = image_id,
                .placement_id = 0,
                .z = 0,
                .column = info.viewport_col,
                .row = info.viewport_row,
                .x_offset = 0,
                .y_offset = 0,
                .width = info.pixel_width,
                .height = info.pixel_height,
                .source_x = info.source_x,
                .source_y = info.source_y,
                .source_width = info.source_width,
                .source_height = info.source_height,
            };
            try check(api.kitty_graphics_placement_get(iterator, c.GHOSTTY_KITTY_GRAPHICS_PLACEMENT_DATA_PLACEMENT_ID, &placement.placement_id));
            try check(api.kitty_graphics_placement_get(iterator, c.GHOSTTY_KITTY_GRAPHICS_PLACEMENT_DATA_Z, &placement.z));
            try check(api.kitty_graphics_placement_get(iterator, c.GHOSTTY_KITTY_GRAPHICS_PLACEMENT_DATA_X_OFFSET, &placement.x_offset));
            try check(api.kitty_graphics_placement_get(iterator, c.GHOSTTY_KITTY_GRAPHICS_PLACEMENT_DATA_Y_OFFSET, &placement.y_offset));
            try self.placements.append(allocator, placement);
        }
        var index: usize = 0;
        while (index < self.images.items.len) {
            if (self.images.items[index].used) {
                index += 1;
            } else {
                const old = self.images.swapRemove(index);
                allocator.free(old.rgba);
            }
        }
        std.mem.sort(Placement, self.placements.items, {}, struct {
            fn less(_: void, a: Placement, b: Placement) bool {
                if (a.z != b.z) return a.z < b.z;
                if (a.image_id != b.image_id) return a.image_id < b.image_id;
                return a.placement_id < b.placement_id;
            }
        }.less);
        self.generation = generation;
        self.valid = true;
    }
};

fn readImage(allocator: std.mem.Allocator, api: *const binding.Api, handle: c.GhosttyKittyGraphicsImage, generation: u64) !?Image {
    var width: u32 = 0;
    var height: u32 = 0;
    var format: c.GhosttyKittyImageFormat = 0;
    var length: usize = 0;
    var data: [*c]const u8 = null;
    try check(api.kitty_graphics_image_get(handle, c.GHOSTTY_KITTY_IMAGE_DATA_WIDTH, &width));
    try check(api.kitty_graphics_image_get(handle, c.GHOSTTY_KITTY_IMAGE_DATA_HEIGHT, &height));
    try check(api.kitty_graphics_image_get(handle, c.GHOSTTY_KITTY_IMAGE_DATA_FORMAT, &format));
    try check(api.kitty_graphics_image_get(handle, c.GHOSTTY_KITTY_IMAGE_DATA_DATA_LEN, &length));
    try check(api.kitty_graphics_image_get(handle, c.GHOSTTY_KITTY_IMAGE_DATA_DATA_PTR, @ptrCast(&data)));
    if (width == 0 or height == 0 or width > 8192 or height > 8192 or data == null) return null;
    const count = @as(usize, width) * height;
    if (count > storage_limit / 4) return null;
    const channels: usize = switch (format) {
        c.GHOSTTY_KITTY_IMAGE_FORMAT_RGB => 3,
        c.GHOSTTY_KITTY_IMAGE_FORMAT_RGBA => 4,
        c.GHOSTTY_KITTY_IMAGE_FORMAT_GRAY_ALPHA => 2,
        c.GHOSTTY_KITTY_IMAGE_FORMAT_GRAY => 1,
        else => return null,
    };
    if (length != count * channels) return null;
    const rgba = try allocator.alloc(u8, count * 4);
    if (channels == 4) {
        @memcpy(rgba, data[0..rgba.len]);
    } else for (0..count) |pixel| {
        const src = data[pixel * channels ..][0..channels];
        const dst = rgba[pixel * 4 ..][0..4];
        dst[0] = src[0];
        dst[1] = if (channels >= 3) src[1] else src[0];
        dst[2] = if (channels >= 3) src[2] else src[0];
        dst[3] = if (channels == 2) src[1] else 255;
    }
    return .{ .generation = generation, .width = width, .height = height, .rgba = rgba };
}
