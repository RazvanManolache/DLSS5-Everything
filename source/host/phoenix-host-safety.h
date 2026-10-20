#pragma once
#include <cstdint>
#include <limits>

inline constexpr uint64_t kPhoenixFailedSubmission = (std::numeric_limits<uint64_t>::max)();

template<class Close, class Execute, class Signal>
bool PhoenixSubmitCommands(Close close, Execute execute, Signal signal) {
    if (!close()) return false;
    execute();
    return signal();
}

template<class Read, class Arm, class Wait, class Clock>
bool PhoenixWaitFence(uint64_t target, uint32_t timeout, Read read, Arm arm, Wait wait, Clock clock) {
    if (target == kPhoenixFailedSubmission) return false;
    auto value = read();
    if (value == kPhoenixFailedSubmission) return false;
    if (value >= target) return true;
    const uint64_t deadline = clock() + timeout;
    if (!arm()) return false;
    for (;;) {
        value = read();
        if (value == kPhoenixFailedSubmission) return false;
        if (value >= target) return true;
        const auto now = clock();
        if (now >= deadline) return false;
        if (!wait(static_cast<uint32_t>(deadline - now))) {
            value = read();
            return value != kPhoenixFailedSubmission && value >= target;
        }
    }
}

inline bool PhoenixMayPublishShared(bool processed, bool retired, bool terminal) {
    return processed && retired && !terminal;
}
