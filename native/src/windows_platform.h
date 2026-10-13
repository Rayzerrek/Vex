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
/* All callbacks run on the window thread. Context and borrowed bytes must remain live. */
typedef struct {
    void* context;
    /* Output returns whether a visible pane needs painting; hidden output still parses. */
    bool (*output)(void*, uint32_t, const uint8_t*, size_t);
    void (*resize)(void*, float, float, float, float, float);
    void (*paint)(void*);
    bool (*key)(void*, uint32_t, uint32_t);
    void (*text)(void*, const uint8_t*, size_t);
    void (*scroll)(void*, int32_t);
    void (*click)(void*, float, float);
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
void vex_window_error(const char* message);
/* Tokens are nonzero, never reused, and cannot be interpreted as heap pointers. */
int vex_pty_open(uint32_t token, uint16_t columns, uint16_t rows, const uint8_t* directory, size_t directory_length, VexPty** output);
int vex_machine_name(uint8_t* bytes, size_t capacity, size_t* length);
void vex_pty_close(VexPty* session);
bool vex_pty_write(VexPty* session, const uint8_t* bytes, size_t length);
bool vex_pty_resize(VexPty* session, uint16_t columns, uint16_t rows);
uint32_t vex_pty_process_id(VexPty* session);
/* Private window/pipe bridge: borrowing succeeds once per posted block. */
bool vex_pty_borrow_output(uint32_t token, const uint8_t** bytes, size_t* length);
void vex_pty_ack_output(uint32_t token);
bool vex_pty_exists(uint32_t token);
void vex_draw_tab(uint32_t index, uint32_t count, bool active);
void vex_begin_pane(VexPaneRect bounds, bool focused);
void vex_end_pane(void);
int vex_draw_run(uint16_t column, uint16_t row, uint16_t cells, const uint8_t* utf8, size_t length, uint32_t foreground, uint32_t background, uint32_t flags);
void vex_draw_cursor(uint16_t column, uint16_t row, uint32_t style, uint32_t color);
/* Loads only the DLL beside the executable, never a library from PATH/CWD. */
void* vex_terminal_symbol(const char* name);
#ifdef __cplusplus
}
#endif
#endif
