#pragma once
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>

// ConPTY consumes APC on its input side. Win32 key records deliver the literal
// reply characters to the child instead of parsing them as another APC command.
inline size_t encodeConptyGraphicsReply(const uint8_t* bytes, size_t length, uint8_t* output, size_t capacity) {
    if (!bytes || !output || !length || length > capacity / 20) return 0;
    for (size_t i = 0; i < length; ++i) if (bytes[i] > 127) return 0;
    size_t used = 0;
    for (size_t i = 0; i < length; ++i) {
        const int count = snprintf(reinterpret_cast<char*>(output + used), capacity - used, "\x1b[0;0;%u;1;0;1_", (unsigned)bytes[i]);
        if (count <= 0 || (size_t)count >= capacity - used) return 0;
        used += (size_t)count;
    }
    return used;
}
