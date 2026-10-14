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
typedef struct { uint32_t state, kind; int32_t progress; } VexProgramStatus;
/* Borrowed UTF-16 strings exclude sentinel lengths; environment includes its final double NUL. */
typedef struct {
    const uint16_t* program; size_t program_length;
    uint16_t* command; size_t command_length;
    const uint16_t* directory; size_t directory_length;
    const uint16_t* environment; size_t environment_length;
} VexPtyLaunch;
/* Pointer event/cursor values are shared with the checked Zig adapter. */
typedef enum { VEX_POINTER_HOVER = 0, VEX_POINTER_BEGIN = 1, VEX_POINTER_MOVE = 2, VEX_POINTER_END = 3, VEX_POINTER_CANCEL = 4 } VexPointerEvent;
typedef enum { VEX_POINTER_NORMAL = 0, VEX_POINTER_RIGHT = 1, VEX_POINTER_DOWN = 2, VEX_POINTER_TAB_ARMED = 3, VEX_POINTER_TAB_DRAGGING = 4 } VexPointerResponse;
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
    void (*scroll)(void*, int32_t);
    void (*click)(void*, float, float);
    /* Event: hover/begin/move/end/cancel. DIP coordinates. Result: cursor/armed-tab/captured-tab response. */
    uint32_t (*pointer)(void*, uint32_t, float, float);
    void (*action)(void*, uint32_t, uint32_t);
    void (*exited)(void*, uint32_t, uint32_t);
    uint64_t (*probe)(void*, uint32_t);
} VexWindowCallbacks;
/* Zero is success; nonzero values are Win32/HRESULT error codes. */
int vex_window_run(VexWindowCallbacks callbacks);
void vex_startup_begin(void);
void vex_window_invalidate(void);
void vex_window_close(void);
uint64_t vex_window_paint_count(void);
/* Window thread only; releasing capture may synchronously deliver WM_CAPTURECHANGED. */
int vex_window_cancel_pointer_capture(void);
bool vex_window_has_pointer_capture(void);
/* Parsed strip geometry comes from Zig, so paint/input never use a stale painted tab count. */
int vex_window_tab_strip(uint32_t count, float tab_width);
int vex_draw_tab_insertion_marker(float x);
void vex_window_error(const char* message);
int vex_window_shell_name(const uint8_t* bytes, size_t length);
/* Tokens are nonzero, never reused, and cannot be interpreted as heap pointers. */
int vex_pty_open(uint32_t token, uint16_t columns, uint16_t rows, VexPtyLaunch launch, VexPty** output);
int vex_machine_name(uint8_t* bytes, size_t capacity, size_t* length);
int vex_system_directory(uint8_t* bytes, size_t capacity, size_t* length);
int vex_clipboard_write(const uint8_t* bytes, size_t length);
void vex_pty_close(VexPty* session);
bool vex_pty_write(VexPty* session, const uint8_t* bytes, size_t length);
bool vex_pty_resize(VexPty* session, uint16_t columns, uint16_t rows);
uint32_t vex_pty_process_id(VexPty* session);
/* Private window/pipe bridge: borrowing succeeds once per posted block. */
bool vex_pty_borrow_output(uint32_t token, const uint8_t** bytes, size_t* length);
void vex_pty_ack_output(uint32_t token);
bool vex_pty_exists(uint32_t token);
void vex_draw_tab(uint32_t index, uint32_t count, bool active, const uint8_t* title, size_t title_length, VexProgramStatus status, bool attention);
void vex_begin_pane(VexPaneRect bounds, bool focused, const uint8_t* title, size_t title_length, VexProgramStatus status);
void vex_end_pane(void);
int vex_draw_run(uint16_t column, uint16_t row, uint16_t cells, const uint8_t* utf8, size_t length, uint32_t foreground, uint32_t background, uint32_t flags);
void vex_draw_cursor(uint16_t column, uint16_t row, uint32_t style, uint32_t color);
/* Loads only the DLL beside the executable, never a library from PATH/CWD. */
void* vex_terminal_symbol(const char* name);
#ifdef __cplusplus
}
#endif
#endif
