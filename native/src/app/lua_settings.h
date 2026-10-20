#pragma once
#include <stddef.h>
#include <stdbool.h>

typedef struct VexLuaSettings {
    char theme[128];
    char font_family[128];
    char default_shell[32];
    int font_size;
    bool cursor_blink;
    bool copy_on_select;
    bool confirm_close;
} VexLuaSettings;

typedef int (*VexShellCallback)(void* context, unsigned id, const char* name, size_t name_length,
                                const char* program, size_t program_length, const char* arguments, size_t arguments_length);
int vex_lua_shells_decode(const char* bytes, size_t length, const char* path, void* context,
                         VexShellCallback callback, char* diagnostic, size_t capacity);

// Runs configuration in a fresh, bounded Lua VM. No process, native module or IO APIs.
int vex_lua_settings_decode(const char* bytes, size_t length, const char* path,
                            VexLuaSettings* settings, char* diagnostic, size_t capacity);
