#include "lua_settings.h"
#include "lua.h"
#include "lauxlib.h"
#include "lualib.h"
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#ifdef _WIN32
#include <windows.h>
#endif

typedef struct ConfigRuntime {
    size_t allocated;
    unsigned ticks;
    const char* bytes;
    size_t length;
    const char* path;
    VexLuaSettings* settings;
} ConfigRuntime;

static void* configAllocate(void* context, void* pointer, size_t old_size, size_t new_size) {
    ConfigRuntime* runtime = context;
    if (!pointer) old_size = 0;
    if (!new_size) {
        free(pointer);
        runtime->allocated -= old_size;
        return NULL;
    }
    if (new_size > 8 * 1024 * 1024 || runtime->allocated - old_size > 8 * 1024 * 1024 - new_size) return NULL;
    void* next = realloc(pointer, new_size);
    if (next) runtime->allocated = runtime->allocated - old_size + new_size;
    return next;
}

static void configInstructionHook(lua_State* state, lua_Debug* debug) {
    (void)debug;
    ConfigRuntime* runtime = *(ConfigRuntime**)lua_getextraspace(state);
    if (++runtime->ticks >= 100) luaL_error(state, "Vex configuration: instruction limit exceeded");
}

static void setString(lua_State* state, const char* name, const char* value) {
    lua_pushstring(state, value);
    lua_setfield(state, -2, name);
}

static void readString(lua_State* state, const char* name, char* output, size_t capacity) {
    lua_pushstring(state, name);
    lua_rawget(state, -2);
    size_t length = 0;
    const char* text = lua_type(state, -1) == LUA_TSTRING ? lua_tolstring(state, -1, &length) : NULL;
    if (!text || !length || length >= capacity || memchr(text, 0, length))
        luaL_error(state, "Vex configuration: %s must be a nonempty string shorter than %d bytes", name, (int)capacity);
    memcpy(output, text, length);
    output[length] = 0;
    lua_pop(state, 1);
}

static int configModuleSearcher(lua_State* state) {
    ConfigRuntime* runtime = *(ConfigRuntime**)lua_getextraspace(state);
    size_t length;
    const char* name = luaL_checklstring(state, 1, &length);
    if (!length || length > 128 || name[0] == '.' || name[length - 1] == '.')
        return luaL_error(state, "Vex configuration: invalid module name");
    for (size_t i = 0; i < length; ++i) {
        char c = name[i];
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || (c == '.' && name[i - 1] != '.')))
            return luaL_error(state, "Vex configuration: module names must stay beside init.lua");
    }
    const char* slash = strrchr(runtime->path, '/');
    const char* backslash = strrchr(runtime->path, '\\');
    if (backslash && (!slash || backslash > slash)) slash = backslash;
    if (!slash) return luaL_error(state, "Vex configuration: require needs an absolute configuration path");
    lua_pushlstring(state, runtime->path, (size_t)(slash - runtime->path + 1));
    luaL_gsub(state, name, ".", "/");
    lua_pushliteral(state, ".lua");
    lua_concat(state, 3);
    const char* path = lua_tostring(state, -1);
    char* bytes = lua_newuserdatauv(state, 65537, 0);
#ifdef _WIN32
    int units = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, NULL, 0);
    if (!units) return luaL_error(state, "Vex configuration: invalid UTF-8 module path");
    wchar_t* wide_path = lua_newuserdatauv(state, (size_t)units * sizeof(wchar_t), 0);
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, wide_path, units);
    FILE* file = _wfopen(wide_path, L"rb");
#else
    FILE* file = fopen(path, "rb");
#endif
    if (!file) {
        lua_pushfstring(state, "\n\tunable to read Vex Lua module '%s'", path);
        return 1;
    }
    size_t count = fread(bytes, 1, 65537, file);
    int failed = ferror(file);
    fclose(file);
    if (failed || count > 65536) return luaL_error(state, "Vex configuration: module '%s' is unreadable or exceeds 64 KiB", path);
    const char* chunk = lua_pushfstring(state, "@%s", path);
    size_t offset = count >= 3 && memcmp(bytes, "\xef\xbb\xbf", 3) == 0 ? 3 : 0;
    if (luaL_loadbufferx(state, bytes + offset, count - offset, chunk, "t") != LUA_OK) return lua_error(state);
    lua_pushstring(state, path);
    return 2;
}

static int configSetMetatable(lua_State* state) {
    luaL_checktype(state, 1, LUA_TTABLE);
    if (!lua_isnil(state, 2)) {
        luaL_checktype(state, 2, LUA_TTABLE);
        lua_pushliteral(state, "__gc"); lua_rawget(state, 2);
        if (!lua_isnil(state, -1)) return luaL_error(state, "Vex configuration: finalizers are unavailable because they bypass execution limits");
        lua_pop(state, 1);
    }
    lua_settop(state, 2);
    lua_pushvalue(state, lua_upvalueindex(1));
    lua_insert(state, 1);
    lua_call(state, 2, 1);
    return 1;
}

static int configExecute(lua_State* state) {
    ConfigRuntime* runtime = lua_touserdata(state, 1);
    VexLuaSettings* settings = runtime->settings;
    luaL_requiref(state, "_G", luaopen_base, 1); lua_pop(state, 1);
    luaL_requiref(state, LUA_TABLIBNAME, luaopen_table, 1); lua_pop(state, 1);
    luaL_requiref(state, LUA_STRLIBNAME, luaopen_string, 1); lua_pop(state, 1);
    luaL_requiref(state, LUA_MATHLIBNAME, luaopen_math, 1); lua_pop(state, 1);
    luaL_requiref(state, LUA_UTF8LIBNAME, luaopen_utf8, 1); lua_pop(state, 1);
    luaL_requiref(state, LUA_LOADLIBNAME, luaopen_package, 1);
    // Only bounded UTF-8 Lua source beside init.lua; never DLLs or binary chunks.
    lua_getfield(state, -1, "searchers");
    lua_pushcfunction(state, configModuleSearcher); lua_rawseti(state, -2, 2);
    lua_pushnil(state); lua_rawseti(state, -2, 3);
    lua_pushnil(state); lua_rawseti(state, -2, 4);
    lua_pop(state, 1);
    setString(state, "cpath", "");
    lua_pushnil(state); lua_setfield(state, -2, "loadlib");
    setString(state, "path", "");
    lua_pop(state, 1);
    const char* blocked[] = { "dofile", "loadfile", "load", "pcall", "xpcall", "print", "collectgarbage" };
    for (size_t i = 0; i < sizeof(blocked) / sizeof(blocked[0]); ++i) {
        lua_pushnil(state); lua_setglobal(state, blocked[i]);
    }
    lua_getglobal(state, "setmetatable");
    lua_pushcclosure(state, configSetMetatable, 1);
    lua_setglobal(state, "setmetatable");
    lua_newtable(state);
    lua_newtable(state);
    setString(state, "theme", settings->theme);
    setString(state, "font_family", settings->font_family);
    setString(state, "default_shell", settings->default_shell);
    lua_pushinteger(state, settings->font_size); lua_setfield(state, -2, "font_size");
    lua_pushboolean(state, settings->cursor_blink); lua_setfield(state, -2, "cursor_blink");
    lua_pushboolean(state, settings->copy_on_select); lua_setfield(state, -2, "copy_on_select");
    lua_setfield(state, -2, "config");
    lua_setglobal(state, "vex");
    lua_sethook(state, configInstructionHook, LUA_MASKCOUNT, 10000);
    const char* chunk_name = lua_pushfstring(state, "@%s", runtime->path);
    int status = luaL_loadbufferx(state, runtime->bytes, runtime->length, chunk_name, "t");
    if (status != LUA_OK) return lua_error(state);
    lua_call(state, 0, 0);
    lua_getglobal(state, "vex");
    if (!lua_istable(state, -1)) return luaL_error(state, "Vex configuration: vex must remain a table");
    lua_pushliteral(state, "config"); lua_rawget(state, -2);
    if (!lua_istable(state, -1)) return luaL_error(state, "Vex configuration: vex.config must be a table");
    // Raw access prevents configuration metatables from executing during validation.
    lua_pushnil(state);
    while (lua_next(state, -2)) {
        size_t key_length = 0;
        const char* key = lua_type(state, -2) == LUA_TSTRING ? lua_tolstring(state, -2, &key_length) : NULL;
        if (!key || strlen(key) != key_length || (strcmp(key, "theme") && strcmp(key, "font_family") && strcmp(key, "default_shell") && strcmp(key, "font_size") && strcmp(key, "cursor_blink") && strcmp(key, "copy_on_select")))
            return luaL_error(state, "Vex configuration: unknown setting '%s'", key ? key : "non-string key");
        lua_pop(state, 1);
    }
    readString(state, "theme", settings->theme, sizeof(settings->theme));
    readString(state, "font_family", settings->font_family, sizeof(settings->font_family));
    readString(state, "default_shell", settings->default_shell, sizeof(settings->default_shell));
    lua_pushliteral(state, "font_size"); lua_rawget(state, -2);
    if (!lua_isinteger(state, -1) || lua_tointeger(state, -1) < 9 || lua_tointeger(state, -1) > 32)
        return luaL_error(state, "Vex configuration: font_size must be an integer from 9 to 32");
    settings->font_size = (int)lua_tointeger(state, -1);
    lua_pop(state, 1);
    lua_pushliteral(state, "cursor_blink"); lua_rawget(state, -2);
    if (!lua_isboolean(state, -1)) return luaL_error(state, "Vex configuration: cursor_blink must be a boolean");
    settings->cursor_blink = lua_toboolean(state, -1) != 0;
    lua_pop(state, 1);
    lua_pushliteral(state, "copy_on_select"); lua_rawget(state, -2);
    if (!lua_isboolean(state, -1)) return luaL_error(state, "Vex configuration: copy_on_select must be a boolean");
    settings->copy_on_select = lua_toboolean(state, -1) != 0;
    return 0;
}

int vex_lua_settings_decode(const char* bytes, size_t length, const char* path,
                            VexLuaSettings* settings, char* diagnostic, size_t capacity) {
    ConfigRuntime runtime = { .bytes = bytes, .length = length, .path = path, .settings = settings };
    lua_State* state = lua_newstate(configAllocate, &runtime);
    if (!state) return 2;
    *(ConfigRuntime**)lua_getextraspace(state) = &runtime;
    lua_pushcfunction(state, configExecute);
    lua_pushlightuserdata(state, &runtime);
    int status = lua_pcall(state, 1, 0, 0);
    if (status != LUA_OK && capacity) {
        const char* message = lua_tostring(state, -1);
        snprintf(diagnostic, capacity, "%s", message ? message : "Vex configuration: Lua execution failed");
    }
    lua_close(state);
    return status == LUA_OK ? 0 : status == LUA_ERRMEM ? 2 : 1;
}
