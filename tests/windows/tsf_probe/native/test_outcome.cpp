#include "outcome.h"
#include <cassert>
int main() {
    using probe::LifecycleObserved;
    assert(LifecycleObserved(true, true, true, false, true));
    // Startup/WaitForInputIdle/unique-Edit failure cannot be rescued by cleanup.
    assert(!LifecycleObserved(false, true, true, false, true));
    assert(!LifecycleObserved(true, false, true, false, true));
    assert(!LifecycleObserved(true, true, false, false, true));
    assert(!LifecycleObserved(true, true, true, true, true));
    assert(!LifecycleObserved(true, true, true, false, false));
}
