#pragma once
#include <stddef.h>
#include <stdbool.h>

typedef struct VexLuaSettings {
    char theme[128];
    char font_family[128];
    char default_shell[32];
    int font_size;
    bool cursor_blink;
} VexLuaSettings;

// Runs configuration in a fresh, bounded Lua VM. No process, native module or IO APIs.
int vex_lua_settings_decode(const char* bytes, size_t length, const char* path,
                            VexLuaSettings* settings, char* diagnostic, size_t capacity);
