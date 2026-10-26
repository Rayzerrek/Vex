#include "pty_input_queue.h"
#include <stdio.h>
#include <limits.h>

namespace {
void* failAllocation(size_t) { return nullptr; }
#define REQUIRE(condition) do { if (!(condition)) { fprintf(stderr, "PTY queue assertion at line %d: %s\n", __LINE__, #condition); return 1; } } while (0)
}

int testPtyInputQueue() {
    using Result = PtyInputQueue::Result;
    PtyInputQueue queue = {};
    uint8_t output[8192], input[8192];
    for (size_t i = 0; i < sizeof(input); ++i) input[i] = (uint8_t)(i * 17 + i / 256);
    REQUIRE(queue.take(output, sizeof(output)) == 0);
    REQUIRE(queue.append(nullptr, 0, false) == Result::ok);
    REQUIRE(queue.capacity == 0);
    REQUIRE(queue.append(input, 3000, false) == Result::ok);
    REQUIRE(queue.capacity == PtyInputQueue::initialCapacity);
    REQUIRE(queue.take(output, 2500) == 2500 && !memcmp(output, input, 2500));
    REQUIRE(queue.append(input + 3000, 2000, true) == Result::ok);
    REQUIRE(queue.take(output, 1000) == 1000 && !memcmp(output, input + 2500, 1000));
    // The remaining bytes straddle the ring end. Growth failure must preserve both spans.
    const size_t head = queue.head, length = queue.length, capacity = queue.capacity;
    auto bytes = queue.bytes;
    REQUIRE(queue.append(input, 6000, false, failAllocation) == Result::outOfMemory);
    REQUIRE(queue.head == head && queue.length == length && queue.capacity == capacity && queue.bytes == bytes);
    REQUIRE(queue.append(input, 6000, false) == Result::ok);
    REQUIRE(queue.take(output, sizeof(output)) == 7500);
    REQUIRE(!memcmp(output, input + 3500, 1500) && !memcmp(output + 1500, input, 6000));
    REQUIRE(queue.length == 0 && queue.head == 0);
    queue.release();
    REQUIRE(!queue.bytes && !queue.capacity);
    REQUIRE(queue.append(input, 1, false, failAllocation) == Result::outOfMemory);
    REQUIRE(!queue.bytes && !queue.length && !queue.capacity);

    auto large = static_cast<uint8_t*>(malloc(PtyInputQueue::limit));
    REQUIRE(large);
    memset(large, 0x5a, PtyInputQueue::limit);
    REQUIRE(queue.append(large, PtyInputQueue::userLimit + 1, false) == Result::quota);
    REQUIRE(!queue.capacity);
    REQUIRE(queue.append(large, PtyInputQueue::userLimit, false) == Result::ok);
    REQUIRE(queue.append(input, 1, false) == Result::quota);
    REQUIRE(queue.append(large, PtyInputQueue::replyReserve, true) == Result::ok);
    REQUIRE(queue.length == PtyInputQueue::limit && queue.capacity == PtyInputQueue::limit);
    REQUIRE(queue.append(input, 1, true) == Result::quota);
    REQUIRE(queue.append(input, SIZE_MAX, true) == Result::quota);
    size_t drained = 0;
    while (queue.length) {
        const size_t count = queue.take(output, sizeof(output));
        REQUIRE(count && !memcmp(output, large, count));
        drained += count;
    }
    REQUIRE(drained == PtyInputQueue::limit);
    free(large);
    queue.release();
    queue.release();

    // Differential FIFO check across repeated wrapping and geometric growth.
    uint8_t expected[32768];
    size_t expectedLength = 0;
    uint32_t random = 17;
    for (size_t iteration = 0; iteration < 2000; ++iteration) {
        random = random * 1664525u + 1013904223u;
        const size_t count = (random >> 16) % sizeof(input);
        if (count <= sizeof(expected) - expectedLength) {
            REQUIRE(queue.append(input, count, (iteration & 1) != 0) == Result::ok);
            memcpy(expected + expectedLength, input, count);
            expectedLength += count;
        }
        random = random * 1664525u + 1013904223u;
        const size_t wanted = (random >> 16) % sizeof(output);
        const size_t actual = queue.take(output, wanted);
        const size_t target = wanted < expectedLength ? wanted : expectedLength;
        REQUIRE(actual == target && !memcmp(output, expected, actual));
        expectedLength -= actual;
        memmove(expected, expected + actual, expectedLength);
        REQUIRE(queue.length == expectedLength && queue.capacity <= PtyInputQueue::limit);
    }
    queue.release();
    return 0;
}
