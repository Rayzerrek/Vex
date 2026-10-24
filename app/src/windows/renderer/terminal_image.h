#pragma once
#include <ghostty/vt/sys.h>
#ifdef __cplusplus
extern "C" {
#endif
bool vex_terminal_decode_png(void* userdata, const GhosttyAllocator* allocator, const uint8_t* data, size_t length, GhosttySysImage* out);
#ifdef __cplusplus
}
#endif
