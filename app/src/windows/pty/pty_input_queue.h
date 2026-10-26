#pragma once
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

// The caller serializes access. Zero initialization is valid for partial PTY startup.
struct PtyInputQueue {
    static constexpr size_t replyReserve = 64 * 1024;
    static constexpr size_t userLimit = 1024 * 1024 + 12;
    static constexpr size_t limit = userLimit + replyReserve;
    static constexpr size_t initialCapacity = 4096;
    enum class Result { ok, quota, outOfMemory };
    uint8_t* bytes;
    size_t head, length, capacity;

    Result append(const uint8_t* source, size_t count, bool response, void* (*allocate)(size_t) = malloc) {
        const size_t allowed = response ? limit : userLimit;
        if (length > allowed || count > allowed - length) return Result::quota;
        if (!count) return Result::ok;
        const size_t required = length + count;
        if (required > capacity) {
            size_t grown = capacity ? capacity : initialCapacity;
            while (grown < required) grown = grown > limit / 2 ? limit : grown * 2;
            auto replacement = static_cast<uint8_t*>(allocate(grown));
            if (!replacement) return Result::outOfMemory;
            // Allocate before mutation so a rejected paste leaves queued replies intact.
            const size_t first = length < capacity - head ? length : capacity - head;
            if (length) {
                memcpy(replacement, bytes + head, first);
                memcpy(replacement + first, bytes, length - first);
            }
            free(bytes);
            bytes = replacement; capacity = grown; head = 0;
        }
        const size_t tail = (head + length) % capacity;
        const size_t first = count < capacity - tail ? count : capacity - tail;
        memcpy(bytes + tail, source, first);
        memcpy(bytes, source + first, count - first);
        length += count;
        return Result::ok;
    }

    size_t take(uint8_t* destination, size_t available) {
        const size_t count = length < available ? length : available;
        if (!count) return 0;
        const size_t first = count < capacity - head ? count : capacity - head;
        memcpy(destination, bytes + head, first);
        memcpy(destination + first, bytes, count - first);
        length -= count;
        head = length ? (head + count) % capacity : 0;
        return count;
    }

    void release() {
        free(bytes);
        bytes = nullptr; head = length = capacity = 0;
    }
};
