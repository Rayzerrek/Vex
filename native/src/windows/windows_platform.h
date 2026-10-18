#ifndef VEX_WINDOWS_PLATFORM_H
#define VEX_WINDOWS_PLATFORM_H
#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#ifdef __cplusplus
extern "C" {
#endif
/* An opaque, uniquely owned session; close consumes it after joining its workers. */
typedef struct VexPty VexPty;
typedef struct { float x, y, width, height; } VexPaneRect;
/* State: none/idle/working/done/blocked/error. Kind: none/permission/question/auth. */
/* Detail is borrowed only for the synchronous draw call; no native UI object retains it. */
/* State: none/idle/working/done/blocked/error. Kind: none/permission/question/auth. Phase: 0..2 icon wave step. */
typedef struct { uint32_t state, kind; int32_t progress; const uint8_t* detail; size_t detail_length; uint32_t phase; } VexProgramStatus;
/* Kind: 0 = shell (> _), 1 = vector glyph, 2 = letter badge. */
typedef struct { uint8_t kind, glyph, badge_len, reserved; uint32_t color_rgb; uint8_t badge[4]; } VexAppIcon;
/* Theme-derived window, tab strip, and pane chrome palette. */
typedef struct {
    uint32_t window_bg, titlebar_bg, border, pane_bg, pane_header_bg;
    uint32_t active_tab_bg, active_tab_border, inactive_tab_hover_bg;
    uint32_t text_primary, text_muted, accent;
    bool is_dark;
} VexChromePalette;
typedef struct {
    const uint8_t* name; size_t name_length;
    bool is_directory, selected, hovered;
} VexPathCompletionItem;
/* Borrowed UTF-16 strings exclude sentinel lengths; environment includes its final double NUL. */
typedef struct {
    const uint16_t* program; size_t program_length;
    uint16_t* command; size_t command_length;
    const uint16_t* directory; size_t directory_length;
    const uint16_t* environment; size_t environment_length;
} VexPtyLaunch;
/* Pointer event/cursor values are shared with the checked Zig adapter. */
typedef enum { VEX_POINTER_HOVER = 0, VEX_POINTER_BEGIN = 1, VEX_POINTER_MOVE = 2, VEX_POINTER_END = 3, VEX_POINTER_CANCEL = 4 } VexPointerEvent;
typedef enum { VEX_POINTER_NORMAL = 0, VEX_POINTER_RIGHT = 1, VEX_POINTER_DOWN = 2, VEX_POINTER_TAB_ARMED = 3, VEX_POINTER_TAB_DRAGGING = 4, VEX_POINTER_SELECTION = 5, VEX_POINTER_LINK = 6, VEX_POINTER_TEXT = 7 } VexPointerResponse;
/* Owned dialog controls copy all request strings before returning to the caller. */
typedef struct { const uint8_t* bytes; size_t length; } VexDialogText;
typedef struct { uint32_t value; VexDialogText label, detail; uint32_t background, foreground, accent; uint32_t swatches[5]; } VexDialogItem;
typedef struct { uint32_t theme, font_size, shell; bool cursor_blink; uint8_t font_family[128]; } VexUserSettings;
/* Kind: appearance/commands/tabs/settings/shell/close = 1..6. */
typedef struct {
    uint32_t kind, selected, mode;
    VexDialogText title, description, accept_label;
    const VexDialogItem* items; size_t count;
    const VexDialogItem* shells; size_t shell_count;
    VexUserSettings settings;
    VexChromePalette palette;
} VexDialogRequest;
int vex_dialog_open(VexDialogRequest request);
void vex_dialog_close(void);
void vex_dialog_warning(VexDialogText message);
uint32_t vex_dialog_first_visible(void);
/* All callbacks run on the window thread. Context and borrowed bytes must remain live. */
typedef struct {
    void* context;
    /* Output requests a frame for visible output or changed background tab status. */
    bool (*output)(void*, uint32_t, const uint8_t*, size_t);
    void (*resize)(void*, float, float, float, float, float);
    void (*paint)(void*);
    bool (*key)(void*, uint32_t, uint32_t);
    void (*text)(void*, const uint8_t*, size_t);
    void (*paste)(void*, const uint8_t*, size_t);
    /* Signed raw wheel units (120 per notch); the pane accumulates high-resolution deltas. */
    void (*scroll)(void*, int32_t, float, float, uint32_t);
    void (*click)(void*, float, float);
    void (*mouse_button)(void*, uint32_t, bool, float, float, uint32_t);
    /* Event: hover/begin/move/end/cancel. DIP coordinates + modifiers. Result: cursor/armed-tab/captured-tab response. */
    uint32_t (*pointer)(void*, uint32_t, float, float, uint32_t);
    void (*action)(void*, uint32_t, uint32_t);
    void (*exited)(void*, uint32_t, uint32_t);
    void (*tick)(void*);
    void (*cursor_wake)(void*);
    void (*cursor_activity)(void*, bool);
    uint64_t (*probe)(void*, uint32_t);
    /* Event: cancel/choose/preview/query/settings/mode = 0..5; text is borrowed for this call only. */
    void (*dialog)(void*, uint32_t, uint32_t, const uint8_t*, size_t, VexUserSettings);
} VexWindowCallbacks;
/* Zero is success; nonzero values are Win32/HRESULT error codes. */
int vex_window_run(VexWindowCallbacks callbacks);
void vex_startup_begin(void);
void vex_window_invalidate(void);
void vex_window_defer_synchronized(void);
void vex_window_close(void);
void vex_window_paste_clipboard(void);
void vex_window_set_chrome_palette(VexChromePalette palette);
int vex_window_set_font_size(float font_size);
int vex_window_set_font_preferences(const uint8_t* family, float font_size);
float vex_window_font_size(void);
uint64_t vex_window_paint_count(void);
/* One-shot cursor wake; zero cancels it. No animation timer exists for a steady/hidden cursor. */
int vex_window_schedule_cursor_wake(uint32_t delay_ms);
bool vex_window_cursor_wake_armed(void);
bool vex_window_animations_enabled(void);
uint32_t vex_window_wheel_scroll_lines(void);
/* Window thread only; releasing capture may synchronously deliver WM_CAPTURECHANGED. */
int vex_window_cancel_pointer_capture(void);
bool vex_window_has_pointer_capture(void);
/* Parsed strip geometry comes from Zig, so paint/input never use a stale painted tab count. */
int vex_window_tab_strip(uint32_t count, float tab_width);
void vex_draw_tab_strip_background(uint32_t active_index);
int vex_draw_tab_insertion_marker(float x);
void vex_window_error(const char* message);
int vex_window_shell_name(const uint8_t* bytes, size_t length);
/* Tokens are nonzero, never reused, and cannot be interpreted as heap pointers. */
int vex_pty_open(uint32_t token, uint16_t columns, uint16_t rows, VexPtyLaunch launch, VexPty** output);
int vex_machine_name(uint8_t* bytes, size_t capacity, size_t* length);
int vex_system_directory(uint8_t* bytes, size_t capacity, size_t* length);
int vex_clipboard_write(const uint8_t* bytes, size_t length);
int vex_open_url(const uint8_t* bytes, size_t length);
void vex_pty_close(VexPty* session);
bool vex_pty_write(VexPty* session, const uint8_t* bytes, size_t length);
bool vex_pty_resize(VexPty* session, uint16_t columns, uint16_t rows);
uint32_t vex_pty_process_id(VexPty* session);
int vex_pty_foreground_process(VexPty* session, uint8_t* exe_bytes, size_t exe_capacity, size_t* exe_length, uint8_t* cmd_bytes, size_t cmd_capacity, size_t* cmd_length);
/* Private window/pipe bridge: borrowing succeeds once per posted block. */
bool vex_pty_borrow_output(uint32_t token, const uint8_t** bytes, size_t* length);
void vex_pty_ack_output(uint32_t token);
bool vex_pty_exists(uint32_t token);
void vex_draw_tab(uint32_t index, uint32_t count, bool active, bool editing, const uint8_t* title, size_t title_length, const VexAppIcon* icons, size_t icon_count, VexProgramStatus status, bool attention);
void vex_begin_pane(VexPaneRect bounds, bool focused, bool hovered, bool can_focus, bool zoomed, uint8_t hovered_action, const uint8_t* title, size_t title_length, VexAppIcon icon, VexProgramStatus status);
void vex_end_pane(void);
void vex_draw_scrollbar(VexPaneRect bounds, float thumb_y, float thumb_height, bool active, float opacity);
void vex_draw_session_exit(VexPaneRect bounds, uint32_t exit_code);
void vex_draw_split_preview(VexPaneRect bounds);
void vex_draw_path_completion(VexPaneRect bounds, const uint8_t* directory, size_t directory_length, const uint8_t* query, size_t query_length, const VexPathCompletionItem* items, size_t item_count, uint32_t flags);
int vex_draw_run(uint16_t column, uint16_t row, uint16_t cells, const uint8_t* utf8, size_t length, uint32_t foreground, uint32_t background, uint32_t flags);
void vex_draw_cursor(uint16_t column, uint16_t row, uint32_t style, uint32_t color);
/* Exact scalar key: pane, dimensions, palette, block cursor and hovered link. No borrowed pointers. */
typedef struct {
    uint64_t generation;
    uint32_t pane_id, columns, rows;
    uint32_t background, foreground, cursor, selection_background, accent, is_dark;
    uint32_t link_row, link_start, link_end;
} VexTerminalGeometryKey;
int vex_terminal_geometry_begin(VexTerminalGeometryKey key, bool* reused);
void vex_terminal_geometry_end(bool complete);
/* Loads only the DLL beside the executable, never a library from PATH/CWD. */
void* vex_terminal_symbol(const char* name);
#ifdef __cplusplus
}
#endif
#endif
