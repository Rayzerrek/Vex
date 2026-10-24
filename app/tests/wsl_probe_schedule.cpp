#include "wsl_probe_schedule.h"
using namespace wslProbeSchedule;

static_assert(!requestDue(1749, 1000, 0));
static_assert(requestDue(1750, 1000, 0));
static_assert(!requestDue(1999, 1000, 1000));
static_assert(requestDue(2000, 1000, 1000));
// Submitted commands get faster metadata; startup protection and rate limits remain.
static_assert(!requestDue(1749, 1000, 0, 3000));
static_assert(requestDue(1750, 1000, 0, 3000));
static_assert(!requestDue(2199, 1000, 2000, 3000));
static_assert(requestDue(2200, 1000, 2000, 3000));
static_assert(!requestDue(3000, 1000, 2200, 3000));
static_assert(requestDue(3200, 1000, 2200, 3000));
// Completion notifications cannot recursively queue another query after a slow scan.
static_assert(!requestDue(4000, 1000, 2000, 5000, 4000));
static_assert(!requestDue(4199, 1000, 2000, 5000, 4000));
static_assert(requestDue(4200, 1000, 2000, 5000, 4000));
static_assert(!requestDue(999, 1000, 0));
static_assert(retryDelayMs(0) == 1000);
static_assert(retryDelayMs(1) == 2000);
static_assert(retryDelayMs(4) == 16000);
static_assert(retryDelayMs(100) == 16000);
static_assert(queryTimeoutMs > shutdownGraceMs);
