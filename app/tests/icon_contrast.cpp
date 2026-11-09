#include "icon_contrast.h"
#include "pi_icon.h"

int testIconContrast() {
    const uint32_t backgrounds[] = {0xffffff, 0xf5f5f5, 0xe8e0d5, 0x808080, 0x202020, 0};
    const uint32_t brands[] = {0xf1ecec, 0xf0efef, 0xf7df1e, 0x61dafb, 0x7a9dff, 0xb8b8be, 0x191919, 0, 0xd97757};
    for (uint32_t background : backgrounds) {
        for (uint32_t brand : brands) {
            const uint32_t adjusted = contrastingIconColor(brand, background);
            if (iconContrastRatio(adjusted, background) < 3.0) return 1;
            if (contrastingIconColor(adjusted, background) != adjusted) return 2;
            if (iconContrastRatio(brand, background) >= 3.0 && adjusted != brand) return 3;
        }
    }
    for (const auto& block : piIconBlocks) {
        if (block.left < 0 || block.top < 0 || block.right > 24 || block.bottom > 24) return 4;
        if (block.left >= block.right || block.top >= block.bottom) return 5;
        for (uint32_t background : backgrounds)
            if (iconContrastRatio(contrastingIconColor(block.color, background), background) < 3.0) return 6;
        if (contrastingIconColor(block.color, 0x202020) != block.color) return 7;
    }
    if (contrastingIconColor(0xf0efef, 0xffffff) == 0xf0efef) return 8;
    if (contrastingIconColor(0xf0efef, 0x202020) != 0xf0efef) return 9;
    if (contrastingIconColor(0, 0xffffff) != 0) return 10;
    if (contrastingIconColor(0, 0x202020) == 0) return 11;
    return 0;
}
