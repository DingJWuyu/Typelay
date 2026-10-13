#define _GNU_SOURCE
#include <dlfcn.h>
#include <gio/gio.h>
#include <stdio.h>
#include <fcitx-gclient/fcitxgclient.h>
#include <stdlib.h>

// Diagnostic only: traces actual Fcitx GClient calls, never reads field text,
// changes the selected IM, marks a field Clear, or permits a write.
typedef struct { guint64 issued, completed; guint pending, depth; } State;
typedef struct { GAsyncReadyCallback callback; gpointer data; guint64 sequence; } Call;
static State *state(FcitxGClient *client) {
    State *s = g_object_get_data(G_OBJECT(client), "typelay-fcitx-probe");
    if (!s) {
        s = g_new0(State, 1);
        g_object_set_data_full(G_OBJECT(client), "typelay-fcitx-probe", s, g_free);
    }
    return s;
}
static void trace(FcitxGClient *client, const char *event, guint64 sequence) {
    State *s = state(client);
    const guint8 *(*uuid)(FcitxGClient *) = dlsym(RTLD_NEXT, "fcitx_g_client_get_uuid");
    fprintf(stderr, "typelay-fcitx-probe event=%s context=", event);
    const guint8 *id = uuid ? uuid(client) : NULL;
    for (unsigned i = 0; i < 16 && id; ++i) fprintf(stderr, "%02x", id[i]);
    fprintf(stderr, " seq=%" G_GUINT64_FORMAT " pending=%u depth=%u\n", sequence, s->pending, s->depth);
}
static void finished(GObject *source, GAsyncResult *result, gpointer data) {
    Call *call = data;
    State *s = state(FCITX_G_CLIENT(source));
    ++s->depth;
    trace(FCITX_G_CLIENT(source), "reply-enter", call->sequence);
    call->callback(source, result, call->data);
    --s->depth;
    --s->pending;
    ++s->completed;
    trace(FCITX_G_CLIENT(source), "reply-callback-exit", call->sequence);
    g_free(call);
}
void fcitx_g_client_process_key(FcitxGClient *client, guint32 keyval,
    guint32 keycode, guint32 modifiers, gboolean release, guint32 time,
    gint timeout, GCancellable *cancel, GAsyncReadyCallback callback, gpointer data) {
    typedef void (*Function)(FcitxGClient *, guint32, guint32, guint32, gboolean,
        guint32, gint, GCancellable *, GAsyncReadyCallback, gpointer);
    Function next = dlsym(RTLD_NEXT, "fcitx_g_client_process_key");
    if (!next) abort();
    State *s = state(client);
    Call *call = g_new(Call, 1);
    *call = (Call){callback, data, ++s->issued};
    ++s->pending;
    trace(client, "request-issued", call->sequence);
    next(client, keyval, keycode, modifiers, release, time, timeout, cancel, finished, call);
}
gboolean fcitx_g_client_process_key_sync(FcitxGClient *client, guint32 keyval,
    guint32 keycode, guint32 modifiers, gboolean release, guint32 time) {
    typedef gboolean (*Function)(FcitxGClient *, guint32, guint32, guint32, gboolean, guint32);
    Function next = dlsym(RTLD_NEXT, "fcitx_g_client_process_key_sync");
    if (!next) abort();
    State *s = state(client);
    guint64 sequence = ++s->issued;
    ++s->pending;
    ++s->depth;
    trace(client, "sync-enter", sequence);
    gboolean result = next(client, keyval, keycode, modifiers, release, time);
    --s->depth;
    --s->pending;
    ++s->completed;
    trace(client, "sync-return", sequence);
    return result;
}
static gboolean emission(GSignalInvocationHint *hint, guint n,
    const GValue *values, gpointer data) {
    (void)data;
    if (n && G_VALUE_HOLDS_OBJECT(values)) {
        FcitxGClient *client = g_value_get_object(values);
        trace(client, g_signal_name(hint->signal_id), state(client)->issued);
    }
    return TRUE;
}
static gboolean select_candidate(gpointer data) {
    FcitxGClient *client = data;
    trace(client, "candidate-select-request", state(client)->issued);
    fcitx_g_client_select_candidate(client, 0);
    return G_SOURCE_REMOVE;
}
static void preedit_updated(FcitxGClient *client, GPtrArray *items, gint cursor, gpointer data) {
    (void)cursor; (void)data;
    if (g_object_get_data(G_OBJECT(client), "typelay-candidate-selected")) return;
    gboolean nonempty = FALSE;
    for (guint i = 0; items && i < items->len; ++i) {
        FcitxGPreeditItem *item = g_ptr_array_index(items, i);
        if (item->string && *item->string) nonempty = TRUE;
    }
    if (!nonempty) return;
    g_object_set_data(G_OBJECT(client), "typelay-candidate-selected", GINT_TO_POINTER(1));
    g_idle_add_full(G_PRIORITY_DEFAULT_IDLE, select_candidate, g_object_ref(client), g_object_unref);
}

FcitxGClient *fcitx_g_client_new_with_watcher(FcitxGWatcher *watcher) {
    typedef FcitxGClient *(*Function)(FcitxGWatcher *);
    Function next = dlsym(RTLD_NEXT, "fcitx_g_client_new_with_watcher");
    if (!next) abort();
    FcitxGClient *client = next(watcher);
    static gboolean installed;
    if (!installed) {
        installed = TRUE;
        const char *names[] = {"commit-string", "update-formatted-preedit", "delete-surrounding-text", "forward-key", "connected"};
        for (unsigned i = 0; i < G_N_ELEMENTS(names); ++i) {
            guint signal = g_signal_lookup(names[i], G_OBJECT_TYPE(client));
            if (signal) g_signal_add_emission_hook(signal, 0, emission, NULL, NULL);
        }
    }
    if (g_strcmp0(g_getenv("TYPELAY_FCITX_PROBE_SELECT_FIRST"), "1") == 0)
        g_signal_connect(client, "update-formatted-preedit", G_CALLBACK(preedit_updated), NULL);
    return client;
}
