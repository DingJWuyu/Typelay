#pragma once
namespace probe {
// Pure report contract, not a substitute for observing Windows lifecycle events.
constexpr bool LifecycleObserved(bool validated, bool normalExit, bool jobEmpty,
    bool forced, bool fileRemoved) noexcept {
    return validated && normalExit && jobEmpty && !forced && fileRemoved;
}
}
