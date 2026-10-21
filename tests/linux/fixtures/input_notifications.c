#include <assert.h>
#include "../../../src/linux/native/gtk3/input-notifications.h"

static InputNotifications baseline(void) {
    InputNotifications state = {0};
    uint64_t probe = input_notifications_probe(&state);
    input_notifications_observed(&state, probe, 192, 47, true);
    return state;
}
static void notified(InputNotifications *state, bool applied) {
    input_notifications_begin(state, true, false);
    input_notifications_end(state, applied);
}
int main(void) {
    InputNotifications state = baseline();
    uint64_t probe;
    for (unsigned n = 0; n < 15; n++) notified(&state, true);
    probe = input_notifications_probe(&state);
    assert(input_notifications_accounted(&state, 192, 47, 62));
    assert(!input_notifications_accounted(&state, 193, 47, 62));
    assert(!input_notifications_accounted(&state, 192, 47, 61));
    assert(!input_notifications_accounted(&state, 192, 47, 63));
    assert(!input_notifications_accounted(&state, 192, 47, 46));
    notified(&state, true); /* Even a correctly counted new probe input refuses. */
    assert(!input_notifications_accounted(&state, 192, 47, 63));
    input_notifications_observed(&state, probe, 192, 63, true);
    assert(!state.valid);
    probe = input_notifications_probe(&state);
    input_notifications_observed(&state, probe, 192, 63, true);
    assert(state.valid);

    state = baseline();
    notified(&state, false); /* Ordered transition did not invalidate input. */
    probe = input_notifications_probe(&state);
    assert(!input_notifications_accounted(&state, 192, 47, 48));
    state = baseline();
    input_notifications_begin(&state, true, false);
    notified(&state, true); /* Reentrant callback. */
    input_notifications_end(&state, true);
    probe = input_notifications_probe(&state);
    assert(!input_notifications_accounted(&state, 192, 47, 49));

    for (unsigned mode = 0; mode < 4; mode++) {
        state = baseline();
        if (mode == 0) input_notifications_begin(&state, false, false);
        if (mode == 1) input_notifications_begin(&state, true, true);
        if (mode == 2) {
            state.notifications = UINT64_MAX;
            input_notifications_begin(&state, true, false);
        }
        if (mode == 3) {
            input_notifications_reset(&state);
            input_notifications_begin(&state, true, false);
        }
        input_notifications_end(&state, true);
        probe = input_notifications_probe(&state);
        assert(!input_notifications_accounted(&state, 192, 47, 48));
    }
    state = baseline();
    probe = input_notifications_probe(&state);
    input_notifications_begin(&state, true, false); /* Even cause zero during probe. */
    input_notifications_end(&state, false);
    input_notifications_observed(&state, probe, 192, 47, true);
    assert(!state.valid);
    state = baseline();
    input_notifications_begin(&state, true, false);
    input_notifications_begin(&state, true, false);
    input_notifications_end(&state, true); /* Outer callback remains active. */
    probe = input_notifications_probe(&state);
    input_notifications_observed(&state, probe, 192, 49, true);
    assert(!state.valid);
    input_notifications_end(&state, true);
    probe = input_notifications_probe(&state);
    input_notifications_observed(&state, probe, 192, 49, true);
    assert(state.valid);

    state = baseline();
    input_notifications_begin(&state, true, false);
    input_notifications_reset(&state); /* Cannot erase the outer callback. */
    probe = input_notifications_probe(&state);
    input_notifications_observed(&state, probe, 192, 48, true);
    assert(!state.valid);
    input_notifications_end(&state, true);

    state = baseline();
    probe = input_notifications_probe(&state);
    uint64_t outer = probe;
    input_notifications_reset(&state); /* Cannot erase an in-progress probe. */
    probe = input_notifications_probe(&state);
    assert(probe == 0);
    input_notifications_observed(&state, probe, 192, 47, false);
    assert(state.probe == outer && !state.valid);
    input_notifications_observed(&state, outer, 192, 47, true);
    assert(!state.valid && state.probe == 0);
    probe = input_notifications_probe(&state);
    input_notifications_observed(&state, outer, 192, 47, false); /* Stale completion cannot close new probe. */
    assert(state.probe == probe && !state.valid);
    input_notifications_observed(&state, probe, 192, 47, true);
    assert(!state.valid);
    state = baseline();
    state.callback_depth = UINT32_MAX;
    input_notifications_begin(&state, true, false);
    input_notifications_end(&state, true);
    input_notifications_reset(&state);
    assert(state.overflow && state.callback_depth == UINT32_MAX);
    assert(input_notifications_probe(&state) == 0);

    state = baseline();
    state.next_probe = UINT64_MAX;
    assert(input_notifications_probe(&state) == 0);
    input_notifications_reset(&state);
    assert(state.overflow && input_notifications_probe(&state) == 0);

    state = baseline();
    probe = input_notifications_probe(&state);
    input_notifications_observed(&state, probe, 0, 0, false);
    assert(!state.valid && state.probe == 0);
    input_notifications_reset(&state);
    probe = input_notifications_probe(&state);
    input_notifications_observed(&state, probe, 193, 50, true);
    assert(state.valid && state.context == 193);
    return 0;
}
