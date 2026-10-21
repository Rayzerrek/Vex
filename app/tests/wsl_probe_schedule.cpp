#include "wsl_probe_schedule.h"
using namespace wslProbeSchedule;

static_assert(!requestDue(1749, 1000, 0));
static_assert(requestDue(1750, 1000, 0));
static_assert(!requestDue(1999, 1000, 1000));
static_assert(requestDue(2000, 1000, 1000));
static_assert(!requestDue(999, 1000, 0));
static_assert(retryDelayMs(0) == 1000);
static_assert(retryDelayMs(1) == 2000);
static_assert(retryDelayMs(4) == 16000);
static_assert(retryDelayMs(100) == 16000);
static_assert(queryTimeoutMs > shutdownGraceMs);
