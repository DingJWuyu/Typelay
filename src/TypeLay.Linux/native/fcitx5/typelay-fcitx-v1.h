#pragma once
#include <gtk/gtk.h>

/* The actual GtkIMContext publishes itself on its owning widget. GTK3 qdata
 * retains the context. GTK4's context already retains its client widget, so its
 * qdata binding is borrowed and cleared before unbinding or context teardown.
 * The capability table is static. Consumers retain the context during each
 * call, never create a reverse lifetime cycle. Neither a requested im-module
 * name nor loading a library establishes this binding. */
#define TL_FCITX_CONTEXT_V1 "typelay.fcitx.context.v1"
#define TL_FCITX_CAPABILITY_V1 "typelay.fcitx.capability.v1"
#define TL_FCITX_CHANGED_V1 "typelay-state-changed-v1"
#define TL_FCITX_ABI_V1 1u

enum {
    TL_FCITX_KNOWN = 1u << 0,
    TL_FCITX_FOCUSED = 1u << 1,
    TL_FCITX_PENDING = 1u << 2,
    TL_FCITX_COMPOSING = 1u << 3
};
enum {
    TL_FCITX_CHANGE_INPUT = 1u << 0,
    TL_FCITX_CHANGE_FOCUS = 1u << 1,
    TL_FCITX_CHANGE_BINDING = 1u << 2
};

typedef struct {
    guint32 size;
    guint32 version;
    guint64 context_generation;
    guint64 epoch;
    /* Pure focus transitions do not change input_epoch. Real input, composition
     * and an actual engine change do, even when the field text is unchanged. */
    guint64 input_epoch;
    guint64 server_serial;
    guint64 applied_serial;
    guint32 flags;
} TlFcitxStateV1;

typedef struct {
    guint32 size;
    guint32 version;
    /* UI thread only, outside nested dispatch and IM callbacks. Fill the entire
     * state, prove the live context still belongs to expected_widget and query
     * the actual server. FALSE, unknown flags, pending output or unequal serials
     * never authorize a body read or write. No reset or focus change is allowed.
     * The context emits TL_FCITX_CHANGED_V1(guint causes) before state changes
     * become externally observable, so consumers immediately revoke permits. */
    gboolean (*inspect)(GtkIMContext *context, GtkWidget *expected_widget, TlFcitxStateV1 *state);
    /* Only validate the exact last successful Clear inspect, in the same UI
     * dispatch. Check the live binding, local epoch/input/compose/focus, applied
     * output counter, callback depth and queued replay without IPC or dispatch.
     * This cannot establish Clear by itself. Once a permitted synchronous write
     * has begun, new un-dispatched server output is ordered after that write;
     * delivered output or reentrant local input must invalidate this baseline. */
    gboolean (*validate_local)(GtkIMContext *context, GtkWidget *expected_widget,
        const TlFcitxStateV1 *baseline);
    /* Prepare only a previously authorized standard Entry caret movement while
     * its exact baseline is Clear. Run GTK's normal reset-if-needed through a
     * server-validated, bounded synchronous reset batch and deliver its actual
     * output. Do not alter selection or suppress IM output. The consumer must
     * inspect again and revalidate its original source authorization afterwards;
     * this operation cannot turn an unknown/composing context into authority. */
    gboolean (*prepare_caret)(GtkIMContext *context, GtkWidget *expected_widget,
        const TlFcitxStateV1 *baseline);
} TlFcitxCapabilityV1;
