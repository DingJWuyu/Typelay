#pragma once
#include <stdbool.h>
#include <stdint.h>

/* The bundled V1 provider increments input_epoch exactly once before each
 * synchronous INPUT signal. Count only callbacks whose original invalidation
 * actually ran. This ledger never supplies IME Clear or restores a permit. */
typedef struct {
    uint64_t notifications, baseline, context, input, next_probe, probe;
    uint32_t callback_depth;
    bool valid, disturbed, overflow;
} InputNotifications;

static inline void input_notifications_reset(InputNotifications *state) {
    state->valid = false;
    state->notifications = state->baseline = state->context = state->input = 0;
    /* Reset is not permission to forget an outer callback or provider call. */
    if (state->probe || state->callback_depth) state->disturbed = true;
}
static inline void input_notifications_begin(InputNotifications *state, bool known, bool binding) {
    if (state->callback_depth || state->probe || !known || binding) {
        state->valid = false;
        if (state->probe) state->disturbed = true;
    }
    if (state->callback_depth == UINT32_MAX) {
        state->overflow = true;
        state->valid = false;
    } else state->callback_depth++;
}
static inline void input_notifications_end(InputNotifications *state, bool applied) {
    if (!state->callback_depth || state->overflow) {
        state->valid = false;
        state->overflow = true;
        return;
    }
    if (applied) {
        if (state->notifications == UINT64_MAX) {
            state->valid = false;
            state->overflow = true;
        } else state->notifications++;
    }
    state->callback_depth--;
}
static inline uint64_t input_notifications_probe(InputNotifications *state) {
    if (state->probe || state->overflow || state->next_probe == UINT64_MAX) {
        state->valid = false;
        state->disturbed = true;
        if (state->next_probe == UINT64_MAX) state->overflow = true;
        return 0;
    }
    state->probe = ++state->next_probe;
    state->disturbed = state->callback_depth != 0;
    return state->probe;
}
static inline bool input_notifications_accounted(const InputNotifications *state,
        uint64_t context, uint64_t before, uint64_t after) {
    return state->valid && state->probe && !state->disturbed && !state->callback_depth && !state->overflow
        && context != 0 && context == state->context && before == state->input
        && after > before && state->notifications >= state->baseline
        && after - before == state->notifications - state->baseline;
}
static inline void input_notifications_observed(InputNotifications *state, uint64_t probe,
        uint64_t context, uint64_t input, bool success) {
    if (!probe || state->probe != probe) {
        state->valid = false;
        state->disturbed = true;
        return;
    }
    bool stable = success && context != 0 && !state->disturbed && !state->callback_depth && !state->overflow;
    state->probe = 0;
    state->valid = stable;
    if (stable) {
        state->context = context;
        state->input = input;
        state->baseline = state->notifications;
    }
}
