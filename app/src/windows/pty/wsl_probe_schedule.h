#ifndef VEX_WSL_PROBE_SCHEDULE_H
#define VEX_WSL_PROBE_SCHEDULE_H
#include <stdint.h>

namespace wslProbeSchedule {
// Status is advisory: opening a short-lived tab must not spawn a second WSL client.
constexpr uint64_t startupDelayMs = 750;
constexpr uint64_t requestIntervalMs = 1000;
constexpr uint64_t activeIntervalMs = 200;
constexpr uint64_t activityWindowMs = 1500;
constexpr uint64_t queryTimeoutMs = 5000;
constexpr uint64_t shutdownGraceMs = 250;
constexpr uint64_t retryDelayMs(unsigned failures) {
    return 1000ULL << (failures > 4 ? 4 : failures);
}
constexpr bool requestDue(uint64_t now, uint64_t created, uint64_t requested, uint64_t activeUntil = 0, uint64_t completed = 0) {
    const uint64_t interval = now < activeUntil ? activeIntervalMs : requestIntervalMs;
    return now >= created && now - created >= startupDelayMs &&
        (requested == 0 || (now >= requested && now - requested >= interval)) &&
        (completed == 0 || (now >= completed && now - completed >= interval));
}
}
#endif
