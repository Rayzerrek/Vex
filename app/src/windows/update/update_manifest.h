#pragma once
#include <stdint.h>
#include <stddef.h>
#include <string.h>

namespace vex {
// Stable update feeds contain no URLs or executable arguments. Downloads are derived from the version.
struct UpdateVersion {
    uint32_t parts[3] = {};
    bool newerThan(const UpdateVersion& other) const {
        for (unsigned i = 0; i < 3; ++i) if (parts[i] != other.parts[i]) return parts[i] > other.parts[i];
        return false;
    }
};
inline bool parseUpdateNumber(const char*& at, const char* end, uint64_t limit, uint64_t& value) {
    const char* start = at;
    value = 0;
    while (at < end && *at >= '0' && *at <= '9') {
        const unsigned digit = unsigned(*at++ - '0');
        if (value > (limit - digit) / 10) return false;
        value = value * 10 + digit;
    }
    return at != start && (at - start == 1 || *start != '0') && value <= limit;
}
// Parse stable release versions within MSI limits; no prefixes, suffixes or leading zeroes.
inline bool parseUpdateVersion(const char* text, size_t length, UpdateVersion& out) {
    const char* at = text;
    const char* end = text + length;
    UpdateVersion parsed;
    for (unsigned i = 0; i < 3; ++i) {
        uint64_t value;
        if (!parseUpdateNumber(at, end, i == 2 ? 65535 : 255, value)) return false;
        parsed.parts[i] = uint32_t(value);
        if (i < 2 && (at == end || *at++ != '.')) return false;
    }
    if (at != end) return false;
    out = parsed;
    return true;
}
struct UpdateManifest {
    UpdateVersion version;
    uint64_t size = 0;
    uint8_t sha256[32] = {};
};
// Wire format: VEX-UPDATE-1 LF X.Y.Z LF decimal MSI byte count LF lowercase SHA-256 LF.
inline bool parseUpdateManifest(const char* text, size_t length, UpdateManifest& out) {
    constexpr char header[] = "VEX-UPDATE-1\n";
    if (length < sizeof(header) || length > 160 || memcmp(text, header, sizeof(header) - 1)) return false;
    const char* at = text + sizeof(header) - 1;
    const char* end = text + length;
    const char* newline = static_cast<const char*>(memchr(at, '\n', size_t(end - at)));
    UpdateManifest parsed;
    if (!newline || !parseUpdateVersion(at, size_t(newline - at), parsed.version)) return false;
    at = newline + 1;
    if (!parseUpdateNumber(at, end, 512ULL * 1024 * 1024, parsed.size) || parsed.size < 1024 * 1024 || at == end || *at++ != '\n') return false;
    if (end - at != 65 || at[64] != '\n') return false;
    for (unsigned i = 0; i < 64; ++i) {
        const char c = at[i];
        const int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1;
        if (digit < 0) return false;
        parsed.sha256[i / 2] |= uint8_t(digit << (i % 2 ? 0 : 4));
    }
    out = parsed;
    return true;
}
}
