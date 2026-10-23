#pragma once
#include "windows_dialog.h"
#include "dialog_motion.h"
#include "chrome_motion.h"
#include <commctrl.h>

// Private shared state for the dialog host, controls and painting implementation.
namespace vex::dialog {
constexpr int titleId = 100, descriptionId = 101, queryId = 102, listId = 103;
constexpr int fontId = 116, fontLabelId = 117, addShellId = 118, confirmCloseId = 119;
constexpr int shellNameId = 201, shellProgramId = 202, shellArgumentsId = 203, shellBrowseId = 204, shellErrorId = 205;
constexpr int sizeId = 111, blinkId = 112, shellId = 113, copyOnSelectId = 114, themeGridId = 115;
constexpr int closeId = 125, firstPageId = 130;
constexpr int fontSizeLabelId = 121, shellLabelId = 122, shellDetailId = 123, fontSizeDetailId = 126;
constexpr int customShellLabelId = 140, customShellDetailId = 141, fontDetailId = 142;
enum class SettingsPage { general, appearance, terminal, about };
constexpr size_t itemLimit = 64, shellLimit = 64;
struct DialogItem {
    uint32_t value;
    wchar_t label[256], detail[256];
    COLORREF background, foreground, accent, swatches[5];
    VexAppIcon icon;
};
struct DialogModel {
    uint32_t kind, selected;
    wchar_t title[128], description[512], accept[64];
    DialogItem items[itemLimit], shells[shellLimit];
    size_t count, shellCount;
    VexUserSettings settings;
    VexChromePalette palette;
    VexAppIcon icon;
    float anchorX, anchorY;
};
struct ControlLayout { HWND window; float x, y, width, height; };
struct ThemeCardCache {
    HBITMAP images[2] = {};
    int width = 0, height = 0;
    float scale = 0;
};
struct DialogHost {
    HWND owner, window, backdrop, previousFocus;
    ULONG_PTR graphicsToken = 0;
    VexWindowCallbacks callbacks;
    DialogModel model;
    DialogItem* exitItems = nullptr;
    HFONT font, heading, symbols;
    HBRUSH background, field;
    uint64_t themeCardPaintCount = 0, dialogPaintCount = 0, surfacePaintCount = 0;
    enum class Phase { closed, creating, active, closing } phase = Phase::closed;
    bool synchronizing = false, fontSizeInvalid = false;
    DialogMotion motion;
    ChromeMotion cursorBlinkMotion, copyOnSelectMotion, confirmCloseMotion;
    uint32_t pendingEvent = 0, pendingValue = 0;
    bool layered = false, layoutReady = false;
    SettingsPage page = SettingsPage::general;
    VexUserSettings draft = {};
    float scale = 1, width = 600, height = 470, baseWidth = 600, baseHeight = 470;
    ControlLayout controls[40];
    size_t controlCount = 0;
    ThemeCardCache themeCards[itemLimit];
};
extern DialogHost host;
inline const DialogItem& listItem(size_t index) { return host.model.kind == 9 ? host.exitItems[index] : host.model.items[index]; }
inline bool destructiveDialog() { return host.model.kind == 6 || host.model.kind == 8 || host.model.kind == 9; }
COLORREF color(uint32_t rgb);
int px(float dip);
COLORREF blend(COLORREF a, COLORREF b, float amount);
COLORREF surfaceColor();
COLORREF fieldColor();
COLORREF selectionColor();
int listRowHeight();
int themeRowHeight();
int themeColumnWidth();
int headingSize();
// Settings pages are built from titled cards of equal-height rows; one description drives painting and child placement.
struct SettingsCard { float top; int rows; };
struct SettingsGeometry {
    bool compact;
    float left, right, rowHeight, gridTop, gridHeight, headerY[2];
    SettingsCard cards[2];
    int cardCount;
    const wchar_t* headers[2];
};
SettingsGeometry settingsGeometry(SettingsPage page);
bool settingsRect(int id, float& x, float& y, float& width, float& height);
HFONT controlFont(int id);
void roundedFill(HDC dc, RECT bounds, COLORREF fill, COLORREF border, int diameter);
void drawSymbol(HDC dc, const wchar_t* symbol, RECT bounds, COLORREF ink);
void drawListItem(const DRAWITEMSTRUCT& draw);
void drawComboItem(const DRAWITEMSTRUCT& draw);
void drawDialogButton(const DRAWITEMSTRUCT& draw);
void paintDialogSurface(HDC dc);
void acceptDialog();
void notify(uint32_t event, uint32_t value = 0, const uint8_t* text = nullptr, size_t length = 0, VexUserSettings settings = {});
bool navigateCommandList(WPARAM key);
void fillCombo(HWND combo, const DialogItem* items, size_t count, uint32_t selected);
void fillList(int id = listId);
bool listSelection(uint32_t& value, int id = listId);
bool comboSelection(int id, uint32_t& value);
float dialogWidth();
float dialogHeight();
void showPage(SettingsPage previousPage);
void setPageControlVisibility(int id, bool visible);
void drawDialogItem(const DRAWITEMSTRUCT& draw);
INT_PTR inspectListPainting(HWND window, WPARAM wparam, LPARAM lparam);
INT_PTR inspectListScroll(HWND window, WPARAM wparam, LPARAM lparam);
bool createControls(float width, float height);
struct DialogPaintBuffer {
    HDC dc, destination, memory = nullptr;
    HBITMAP bitmap = nullptr;
    HGDIOBJ previous = nullptr;
    RECT bounds;
    DialogPaintBuffer(HDC target, RECT area) : dc(target), destination(target), bounds(area) {
        memory = CreateCompatibleDC(target);
        if (memory) bitmap = CreateCompatibleBitmap(target, area.right - area.left, area.bottom - area.top);
        if (bitmap) {
            previous = SelectObject(memory, bitmap);
            SetWindowOrgEx(memory, area.left, area.top, nullptr);
            dc = memory;
        }
    }
    DialogPaintBuffer(const DialogPaintBuffer&) = delete;
    DialogPaintBuffer& operator=(const DialogPaintBuffer&) = delete;
    ~DialogPaintBuffer() {
        if (bitmap) {
            BitBlt(destination, bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top, memory, bounds.left, bounds.top, SRCCOPY);
            SelectObject(memory, previous);
            DeleteObject(bitmap);
        }
        if (memory) DeleteDC(memory);
    }
};
}
