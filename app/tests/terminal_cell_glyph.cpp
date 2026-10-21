#include "terminal_cell_glyph.h"

constexpr uint8_t horizontal[] = { 0xe2, 0x94, 0x80 };
constexpr uint8_t fullBlock[] = { 0xe2, 0x96, 0x88 };
constexpr uint8_t quadrant[] = { 0xe2, 0x96, 0x9f };
constexpr uint8_t braille[] = { 0xe2, 0xa3, 0xbf };
constexpr uint8_t powerline[] = { 0xee, 0x82, 0xb0 };
constexpr uint8_t ordinaryText[] = { 0xe2, 0x82, 0xac };
constexpr uint8_t malformed[] = { 0xe2, 0x14, 0x80 };
constexpr uint8_t repeated[] = { 0xe2, 0x94, 0x80, 0xe2, 0x94, 0x80 };
static_assert(terminalCellGlyph(horizontal, sizeof(horizontal)));
static_assert(terminalCellGlyph(fullBlock, sizeof(fullBlock)));
static_assert(terminalCellGlyph(quadrant, sizeof(quadrant)));
static_assert(terminalCellGlyph(braille, sizeof(braille)));
static_assert(terminalCellGlyph(powerline, sizeof(powerline)));
static_assert(!terminalCellGlyph(ordinaryText, sizeof(ordinaryText)));
static_assert(!terminalCellGlyph(malformed, sizeof(malformed)));
static_assert(!terminalCellGlyph(repeated, sizeof(repeated)));
static_assert(!terminalCellGlyph(nullptr, 0));

static_assert(!terminalDashVertical(0x2504));
static_assert(!terminalDashVertical(0x2505));
static_assert(terminalDashVertical(0x2506));
static_assert(terminalDashVertical(0x2507));
static_assert(!terminalDashVertical(0x2508));
static_assert(!terminalDashVertical(0x2509));
static_assert(terminalDashVertical(0x250a));
static_assert(terminalDashVertical(0x250b));
static_assert(!terminalDashVertical(0x254c));
static_assert(!terminalDashVertical(0x254d));
static_assert(terminalDashVertical(0x254e));
static_assert(terminalDashVertical(0x254f));
