#include "chrome_motion.h"

constexpr bool chromeMotionSettlesWithoutOvershoot() {
    ChromeMotion motion;
    if (motion.update(8, 0, true) != 8 || motion.pending(0)) return false;
    if (motion.update(172, 10, true) != 8 || !motion.pending(10)) return false;
    if (motion.sample(90) != 151.5f) return false;
    return motion.sample(170) == 172 && !motion.pending(170) && motion.sample(500) == 172;
}
constexpr bool chromeMotionRetargetsContinuously() {
    ChromeMotion motion;
    motion.update(0, 0, true);
    motion.update(160, 0, true);
    const float midpoint = motion.sample(80);
    if (motion.update(0, 80, true) != midpoint) return false;
    if (motion.update(0, 120, true) != motion.sample(120)) return false;
    return motion.sample(240) == 0 && !motion.pending(240);
}
constexpr bool chromeMotionReducedMotionSnapsAndStops() {
    ChromeMotion motion;
    motion.update(0, 0, true);
    motion.update(160, 0, true);
    return motion.update(80, 40, false) == 80 && !motion.pending(40) && motion.sample(1000) == 80;
}
static_assert(chromeMotionSettlesWithoutOvershoot());
static_assert(chromeMotionRetargetsContinuously());
static_assert(chromeMotionReducedMotionSnapsAndStops());
