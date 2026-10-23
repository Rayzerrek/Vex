// Portable regression test: c++ -std=c++17 -Isrc/windows/renderer tests/icon_contrast.cpp -o /tmp/vex-icon-contrast && /tmp/vex-icon-contrast
#include "icon_contrast.h"
#include "pi_icon.h"
#include <assert.h>

int main() {
    const uint32_t backgrounds[] = {0xffffff, 0xf5f5f5, 0xe8e0d5, 0x808080, 0x202020, 0};
    const uint32_t brands[] = {0xf1ecec, 0xf0efef, 0xf7df1e, 0x61dafb, 0x7a9dff, 0xb8b8be, 0x191919, 0, 0xd97757};
    for (uint32_t background : backgrounds) {
        for (uint32_t brand : brands) {
            const uint32_t adjusted = contrastingIconColor(brand, background);
            assert(iconContrastRatio(adjusted, background) >= 3.0);
            assert(contrastingIconColor(adjusted, background) == adjusted);
            if (iconContrastRatio(brand, background) >= 3.0) assert(adjusted == brand);
        }
    }
    for (const auto& block : piIconBlocks) {
        assert(block.left >= 0 && block.top >= 0 && block.right <= 24 && block.bottom <= 24);
        assert(block.left < block.right && block.top < block.bottom);
        for (uint32_t background : backgrounds)
            assert(iconContrastRatio(contrastingIconColor(block.color, background), background) >= 3.0);
        assert(contrastingIconColor(block.color, 0x202020) == block.color);
    }
    assert(contrastingIconColor(0xf0efef, 0xffffff) != 0xf0efef);
    assert(contrastingIconColor(0xf0efef, 0x202020) == 0xf0efef);
    assert(contrastingIconColor(0, 0xffffff) == 0);
    assert(contrastingIconColor(0, 0x202020) != 0);
}
