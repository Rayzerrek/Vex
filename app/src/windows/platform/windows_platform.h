#ifndef VEX_WINDOWS_PLATFORM_H
#define VEX_WINDOWS_PLATFORM_H
#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#ifdef __cplusplus
extern "C" {
#endif
/* An opaque, uniquely owned session; close detaches it and queues native teardown. */
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
typedef struct {
    uint16_t row, start_col, end_col;
} VexCopySpan;
typedef struct {
    bool editing, select_all;
    uint8_t cursor;
    const uint8_t* placeholder;
    size_t placeholder_length;
} VexTabEdit;
/* Borrowed UTF-16 strings exclude sentinel lengths; environment includes its final double NUL. */
typedef struct {
    const uint16_t* program; size_t program_length;
    uint16_t* command; size_t command_length;
    const uint16_t* directory; size_t directory_length;
    const uint16_t* environment; size_t environment_length;
    bool declared_shell;
    bool app_execution_alias;
    const uint16_t* wsl_probe_command; size_t wsl_probe_command_length;
} VexPtyLaunch;
/* Pointer event/cursor values are shared with the checked Zig adapter. */
typedef enum { VEX_POINTER_HOVER = 0, VEX_POINTER_BEGIN = 1, VEX_POINTER_MOVE = 2, VEX_POINTER_END = 3, VEX_POINTER_CANCEL = 4 } VexPointerEvent;
typedef enum { VEX_POINTER_NORMAL = 0, VEX_POINTER_RIGHT = 1, VEX_POINTER_DOWN = 2, VEX_POINTER_TAB_ARMED = 3, VEX_POINTER_TAB_DRAGGING = 4, VEX_POINTER_SELECTION = 5, VEX_POINTER_LINK = 6, VEX_POINTER_TEXT = 7 } VexPointerResponse;
/* Owned dialog controls copy all request strings before returning to the caller. */
typedef struct { const uint8_t* bytes; size_t length; } VexDialogText;
typedef struct { uint32_t value; VexDialogText label, detail; uint32_t background, foreground, accent; uint32_t swatches[5]; VexAppIcon icon; } VexDialogItem;
typedef struct { uint32_t theme, font_size, shell; bool cursor_blink, copy_on_select, confirm_close; uint8_t font_family[128]; } VexUserSettings;
/* Kind: appearance=1, commands=2, settings=4, shell=5, close=6, custom_shell=7, close_popover=8. */
typedef struct {
    uint32_t kind, selected;
    VexDialogText title, description, accept_label;
    const VexDialogItem* items; size_t count;
    const VexDialogItem* shells; size_t shell_count;
    VexUserSettings settings;
    VexChromePalette palette;
    VexAppIcon icon;
    float anchor_x, anchor_y;
} VexDialogRequest;
int vex_dialog_open(VexDialogRequest request);
void vex_dialog_form_error(VexDialogText message);
void vex_dialog_close(void);
typedef enum { VEX_NOTICE_INFO = 0, VEX_NOTICE_SUCCESS = 1, VEX_NOTICE_WARNING = 2, VEX_NOTICE_ERROR = 3 } VexNoticeKind;
/* Native notification strings are copied/decoded before return; no terminal or dialog storage is retained. */
typedef struct { uint32_t kind; VexDialogText title, message; } VexNoticeRequest;
void vex_window_notice(VexNoticeRequest request);
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
    /* External text writes only. Token 0 means the owner could not be attributed to a PTY job. */
    void (*clipboard)(void*, uint32_t, const uint8_t*, size_t);
    /* Signed raw wheel units (120 per notch); the pane accumulates high-resolution deltas. */
    void (*scroll)(void*, int32_t, float, float, uint32_t);
    void (*click)(void*, float, float);
    void (*mouse_button)(void*, uint32_t, bool, float, float, uint32_t);
    /* Event: hover/begin/move/end/cancel. DIP coordinates + modifiers. Result: cursor/armed-tab/captured-tab response. */
    uint32_t (*pointer)(void*, uint32_t, float, float, uint32_t);
    void (*action)(void*, uint32_t, uint32_t);
    void (*exited)(void*, uint32_t, uint32_t);
    /* Transport failure affects only the owning pane, not the window. */
    void (*session_error)(void*, uint32_t, uint32_t);
    void (*process_changed)(void*, uint32_t);
    void (*tick)(void*);
    void (*cursor_wake)(void*);
    void (*cursor_activity)(void*, bool);
    bool (*close_requested)(void*);
    uint64_t (*probe)(void*, uint32_t);
    /* Events 0..4: cancel/choose/preview/query/settings; 6/7: add/save custom shell. Text is borrowed. */
    void (*dialog)(void*, uint32_t, uint32_t, const uint8_t*, size_t, VexUserSettings);
} VexWindowCallbacks;
/* Zero is success; nonzero values are Win32/HRESULT error codes. */
int vex_window_run(VexWindowCallbacks callbacks);
void vex_startup_begin(void);
void vex_window_invalidate(void);
/* Paint-only continuation, paced to the monitor; stops when paint no longer requests it. */
void vex_window_request_animation_frame(void);
void vex_window_defer_synchronized(void);
void vex_window_close(void);
/* Startup declines new sessions while this install is being replaced; no MSI/network IO. */
bool vex_update_can_start(void);
/* Window-thread actions. Preflight is asynchronous; install only commits an acknowledged helper. */
void vex_update_check(void);
bool vex_update_ready(void);
bool vex_update_prepare(void);
bool vex_update_prepared(void);
void vex_update_cancel_install(void);
bool vex_update_install(void);
void vex_window_paste_clipboard(void);
void vex_window_schedule_copy_check(uint32_t delay_ms);
void vex_window_set_chrome_palette(VexChromePalette palette);
int vex_window_set_font_size(float font_size);
int vex_window_set_font_preferences(const uint8_t* family, float font_size);
float vex_window_font_size(void);
uint64_t vex_window_paint_count(void);
/* One-shot cursor wake; zero cancels it. No animation timer exists for a steady/hidden cursor. */
int vex_window_schedule_cursor_wake(uint32_t delay_ms);
bool vex_window_cursor_wake_armed(void);
bool vex_window_animations_enabled(void);
// Notification geometry can change under a stationary pointer.
void vex_window_refresh_hover_cursor(void);
uint32_t vex_window_wheel_scroll_lines(void);
/* Window thread only; releasing capture may synchronously deliver WM_CAPTURECHANGED. */
int vex_window_cancel_pointer_capture(void);
bool vex_window_has_pointer_capture(void);
/* Parsed strip geometry comes from Zig, so paint/input never use a stale painted tab count. */
int vex_window_tab_strip(uint32_t count, float tab_width);
void vex_draw_tab_strip_background(uint32_t active_index);
int vex_draw_tab_insertion_marker(float x);
void vex_draw_tab_drag(VexPaneRect bounds, uint32_t source, const uint8_t* title, size_t title_length, const VexAppIcon* icons, size_t icon_count, bool valid_target);
void vex_window_error(const char* message);
int vex_window_shell_name(const uint8_t* bytes, size_t length);
/* Tokens are nonzero, never reused, and cannot be interpreted as heap pointers. */
int vex_pty_open(uint32_t token, uint16_t columns, uint16_t rows, VexPtyLaunch launch, VexPty** output);
int vex_machine_name(uint8_t* bytes, size_t capacity, size_t* length);
int vex_system_directory(uint8_t* bytes, size_t capacity, size_t* length);
int vex_shell_path(uint8_t* bytes, size_t capacity, size_t* length);
int vex_clipboard_write(const uint8_t* bytes, size_t length);
int vex_open_url(const uint8_t* bytes, size_t length);
void vex_pty_close(VexPty* session);
/* Window thread: joins detached owners without affecting active sessions. Use before shutdown. */
void vex_pty_drain_closed(void);
size_t vex_pty_pending_closures(void);
uint32_t vex_pty_forced_closures(void);
/* Only called behind the window adapter's VEX_NATIVE_SELFTEST gate. */
bool vex_pty_test_delay_close(uint32_t token);
enum { VEX_PTY_INPUT_FULL = 1816, VEX_PTY_OUT_OF_MEMORY = 8 }; /* ERROR_NOT_ENOUGH_QUOTA / ERROR_NOT_ENOUGH_MEMORY */
/* Atomic enqueue: ERROR_NOT_ENOUGH_QUOTA leaves the queue unchanged. */
int vex_pty_write(VexPty* session, const uint8_t* bytes, size_t length);
void vex_pty_note_process_input(VexPty* session);
/* Same FIFO, but can consume reserved protocol capacity after a maximum clipboard paste. */
int vex_pty_write_response(VexPty* session, const uint8_t* bytes, size_t length);
bool vex_pty_resize(VexPty* session, uint16_t columns, uint16_t rows);
uint32_t vex_pty_process_id(VexPty* session);
uint32_t vex_pty_clipboard_token(uint32_t process_id);
int vex_pty_foreground_process(VexPty* session, uint8_t* exe_bytes, size_t exe_capacity, size_t* exe_length, uint8_t* cmd_bytes, size_t cmd_capacity, size_t* cmd_length, bool* is_root, bool* is_shell);
bool vex_pty_wsl_snapshot(VexPty* session, uint8_t* bytes, size_t capacity, size_t* length);
/* Private window/pipe bridge: borrowing succeeds once per posted block. */
bool vex_pty_borrow_output(uint32_t token, const uint8_t** bytes, size_t* length);
void vex_pty_ack_output(uint32_t token);
bool vex_pty_exists(uint32_t token);
void vex_draw_tab(uint32_t index, uint32_t count, bool active, VexTabEdit edit, const uint8_t* title, size_t title_length, const VexAppIcon* icons, size_t icon_count, VexProgramStatus status, bool attention);
void vex_begin_pane(VexPaneRect bounds, bool focused, bool hovered, bool can_focus, bool zoomed, uint8_t hovered_action, const uint8_t* title, size_t title_length, VexAppIcon icon, VexProgramStatus status);
void vex_end_pane(void);
void vex_draw_scrollbar(VexPaneRect bounds, float thumb_y, float thumb_height, bool active, float opacity);
void vex_draw_copy_feedback(VexPaneRect bounds, float cell_width, float cell_height, const VexCopySpan* spans, size_t span_count, float progress);
void vex_draw_split_preview(VexPaneRect bounds);
void vex_begin_terminal_preview(VexPaneRect bounds, uint16_t columns, uint16_t rows);
void vex_draw_tab_overview(bool animating);
void vex_draw_preview_close(VexPaneRect bounds, bool hovered);
void vex_draw_tab_preview_card(VexPaneRect bounds, const uint8_t* title, size_t length, bool selected);
void vex_draw_path_completion(VexPaneRect bounds, const uint8_t* directory, size_t directory_length, const uint8_t* query, size_t query_length, const VexPathCompletionItem* items, size_t item_count, uint32_t flags);
int vex_draw_run(uint16_t column, uint16_t row, uint16_t cells, const uint8_t* utf8, size_t length, uint32_t foreground, uint32_t background, uint32_t flags);
void vex_draw_cursor(uint16_t column, uint16_t row, uint32_t style, uint32_t color);
typedef struct {
    uint64_t generation;
    const uint8_t* rgba;
    uint32_t width, height;
    float left, top, right, bottom;
    float u0, v0, u1, v1;
} VexTerminalImage;
int vex_draw_terminal_image(VexTerminalImage image);
/* 0: normal; 1: backgrounds only; 2: text/decorations only. */
void vex_terminal_cell_layer(uint32_t layer);
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
