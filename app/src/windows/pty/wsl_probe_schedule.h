#ifndef VEX_WSL_PROBE_SCHEDULE_H
#define VEX_WSL_PROBE_SCHEDULE_H
#include <stdint.h>

namespace wslProbeSchedule {
// Status is advisory: opening a short-lived tab must not spawn a second WSL client.
constexpr uint64_t startupDelayMs = 750;
constexpr uint64_t requestIntervalMs = 1000;
constexpr uint64_t queryTimeoutMs = 5000;
constexpr uint64_t shutdownGraceMs = 250;
constexpr uint64_t retryDelayMs(unsigned failures) {
    return requestIntervalMs << (failures > 4 ? 4 : failures);
}
constexpr bool requestDue(uint64_t now, uint64_t created, uint64_t requested) {
    return now >= created && now - created >= startupDelayMs && now >= requested && now - requested >= requestIntervalMs;
}
}
#endif
