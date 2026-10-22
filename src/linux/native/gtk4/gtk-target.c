#define _GNU_SOURCE
#include "gtk-target.h"
#include "gtk-overlay.h"
#include <string.h>
#include <math.h>
#include <unistd.h>

static GHashTable *targets;
static GQuark target_quark;
static guint target_count;

static gboolean standard_entry(Target *target) {
    return target && !target->dead && target->widget && target->buffer
        && target->client && G_OBJECT_TYPE(target->widget) == GTK_TYPE_ENTRY
        && G_OBJECT_TYPE(target->client) == GTK_TYPE_TEXT
        && gtk_widget_get_parent(target->client) == target->widget
        && gtk_editable_get_delegate(GTK_EDITABLE(target->widget)) == GTK_EDITABLE(target->client)
        && G_OBJECT_TYPE(target->buffer) == GTK_TYPE_ENTRY_BUFFER;
}
static gboolean standard_textview(Target *target) {
    return target && !target->dead && target->widget && target->buffer
        && target->client == target->widget && G_OBJECT_TYPE(target->widget) == GTK_TYPE_TEXT_VIEW
        && G_OBJECT_TYPE(target->buffer) == GTK_TYPE_TEXT_BUFFER
        && G_OBJECT(gtk_text_view_get_buffer(GTK_TEXT_VIEW(target->widget))) == target->buffer;
}
static gboolean plain_text_buffer(GtkTextBuffer *buffer) {
    if (gtk_text_tag_table_get_size(gtk_text_buffer_get_tag_table(buffer)) != 0
        || gtk_text_buffer_get_char_count(buffer) > TL_MAX_TEXT) return FALSE;
    /* Structural inspection only, before obtaining any body text. An untagged
       standard buffer may still contain images or embedded child widgets. */
    GtkTextIter position;
    gtk_text_buffer_get_start_iter(buffer, &position);
    while (!gtk_text_iter_is_end(&position)) {
        if (gtk_text_iter_get_child_anchor(&position) || gtk_text_iter_get_paintable(&position)) return FALSE;
        if (!gtk_text_iter_forward_char(&position)) break;
    }
    return TRUE;
}
gboolean tl_source_glossary_supported(Target *target) {
    /* Reuse plain TextView structural admission, before any body access.
       This capability is not an IME proof or a read/write authorization. */
    return standard_entry(target) || (standard_textview(target)
        && plain_text_buffer(GTK_TEXT_BUFFER(target->buffer)));
}
static const TlFcitxCapabilityV1 *im_capability(GtkIMContext *context) {
    if (!context || !GTK_IS_IM_CONTEXT(context)) return NULL;
    const TlFcitxCapabilityV1 *value = g_object_get_data(G_OBJECT(context), TL_FCITX_CAPABILITY_V1);
    return value && value->size >= sizeof *value && value->version == TL_FCITX_ABI_V1
        && value->inspect && value->validate_local && value->prepare_caret ? value : NULL;
}
static GtkIMContext *published_context(Target *target) {
    GtkIMContext *context = (standard_entry(target) || standard_textview(target))
        ? g_object_get_data(G_OBJECT(target->client), TL_FCITX_CONTEXT_V1) : NULL;
    return im_capability(context) ? context : NULL;
}
static void im_invalidated(Target *target, guint causes) {
    static guint trace_count;
    if (g_getenv("TYPELAY_TRACE") && trace_count < 192) {
        trace_count++;
        g_printerr("TypeLay GTK4 IM invalidated atMs=%" G_GINT64_FORMAT
            " causes=%u inputRevision=%" G_GUINT64_FORMAT " focusRevision=%" G_GUINT64_FORMAT
            "\n", tl_now(), causes, target->input_revision, target->focus_revision);
    }
    target->ime_known = FALSE;
    tl_clear_permit();
    if (target->dead) return;
    gboolean input = (causes & (TL_FCITX_CHANGE_INPUT | TL_FCITX_CHANGE_BINDING)) != 0;
    gboolean focus = (causes & TL_FCITX_CHANGE_FOCUS) != 0;
    if (!input && !focus) return;
    /* A callback during mutation is still an invalidation. The ordinary GTK
       text notifications are suppressed then, but IM/focus changes are not. */
    if (target->mutating) {
        if (input) target->input_revision++;
        if (focus) target->focus_revision++;
    } else {
        if (input) tl_invalidate(target, FALSE, FALSE);
        if (focus) tl_invalidate(target, FALSE, TRUE);
    }
}
static void im_changed(GtkIMContext *context, guint causes, gpointer data) {
    Target *target = data;
    if (context != target->im_context) return;
    if (tl_ordered_transition(target)) {
        target->ime_known = FALSE;
        tl_drop_publication();
    } else im_invalidated(target, causes);
}
static gboolean bind_context(Target *target) {
    GtkIMContext *context = published_context(target);
    if (target->im_context != context) {
        if (target->im_context && target->im_handler)
            g_signal_handler_disconnect(target->im_context, target->im_handler);
        target->im_handler = 0;
        if (target->im_context) {
            g_object_remove_weak_pointer(G_OBJECT(target->im_context), (gpointer *)&target->im_context);
            target->im_context = NULL;
        }
        if (context) {
            target->im_context = context;
            g_object_add_weak_pointer(G_OBJECT(context), (gpointer *)&target->im_context);
            guint signal = g_signal_lookup(TL_FCITX_CHANGED_V1, G_OBJECT_TYPE(context));
            GSignalQuery query = {0};
            if (signal) g_signal_query(signal, &query);
            if (query.return_type == G_TYPE_NONE && query.n_params == 1
                && query.param_types[0] == G_TYPE_UINT)
                target->im_handler = g_signal_connect(context, TL_FCITX_CHANGED_V1, G_CALLBACK(im_changed), target);
            else if (target->im_context) {
                g_object_remove_weak_pointer(G_OBJECT(target->im_context), (gpointer *)&target->im_context);
                target->im_context = NULL;
            }
        }
        target->im_observed = FALSE;
        im_invalidated(target, TL_FCITX_CHANGE_BINDING);
    }
    return target->im_context != NULL;
}
static const TlFcitxPrefixV1 *prefix_capability(GtkIMContext *context) {
    const TlFcitxPrefixV1 *value = context
        ? g_object_get_data(G_OBJECT(context), TL_FCITX_PREFIX_V1) : NULL;
    return value && value->size >= sizeof *value && value->version == TL_FCITX_ABI_V1
        && value->supports && value->begin && value->is_transition && value->finish
        && value->validate && value->prepare_caret && value->cancel ? value : NULL;
}
gboolean tl_prefix_supported(Target *target) {
    if (!(standard_entry(target) || (standard_textview(target)
        && plain_text_buffer(GTK_TEXT_BUFFER(target->buffer)))) || !bind_context(target)) return FALSE;
    GtkIMContext *context = g_object_ref(target->im_context);
    GtkWidget *widget = g_object_ref(target->client);
    const TlFcitxPrefixV1 *cap = prefix_capability(context);
    gboolean ready = cap && cap->supports(context, widget);
    g_object_unref(context); g_object_unref(widget);
    return ready && !target->dead && target->im_context == context;
}
gboolean tl_prefix_begin(Target *target, guint64 id, const GdkEvent *event) {
    if (!tl_prefix_supported(target)) return FALSE;
    GtkIMContext *context = g_object_ref(target->im_context);
    GtkWidget *widget = g_object_ref(target->client);
    const TlFcitxPrefixV1 *cap = prefix_capability(context);
    gboolean ready = cap && cap->begin(context, widget, id, event, &target->im_state);
    g_object_unref(context); g_object_unref(widget);
    return ready && !target->dead && target->im_context == context;
}
gboolean tl_prefix_transition(Target *target, guint64 id) {
    if (!target || target->dead || !target->im_context) return FALSE;
    GtkIMContext *context = g_object_ref(target->im_context);
    const TlFcitxPrefixV1 *cap = prefix_capability(context);
    gboolean transition = cap && cap->is_transition(context, id);
    g_object_unref(context);
    return transition && !target->dead && target->im_context == context;
}
gboolean tl_prefix_validate(Target *target, guint64 id, const TlFcitxStateV1 *before,
    const TlFcitxStateV1 *current) {
    if (!(standard_entry(target) || standard_textview(target)) || !target->im_context) return FALSE;
    GtkIMContext *context = g_object_ref(target->im_context);
    GtkWidget *widget = g_object_ref(target->client);
    const TlFcitxPrefixV1 *cap = prefix_capability(context);
    gboolean valid = cap && cap->validate(context, widget, id, before, current);
    g_object_unref(context); g_object_unref(widget);
    return valid && !target->dead && target->im_context == context;
}
gboolean tl_prefix_finish(Target *target, guint64 id, TlFcitxStateV1 *fresh) {
    if (!(standard_entry(target) || standard_textview(target)) || !target->im_context) return FALSE;
    GtkIMContext *context = g_object_ref(target->im_context);
    GtkWidget *client = g_object_ref(target->client);
    const TlFcitxPrefixV1 *cap = prefix_capability(context);
    gboolean ready = g_main_depth() == 1 && cap && cap->finish(context, client, id, fresh)
        && !gtk_widget_in_destruction(client)
        && g_object_get_data(G_OBJECT(client), TL_FCITX_CONTEXT_V1) == context
        && fresh->size == sizeof *fresh && fresh->version == TL_FCITX_ABI_V1
        && fresh->context_generation != 0
        && !(fresh->flags & (guint32)~(TL_FCITX_KNOWN | TL_FCITX_FOCUSED | TL_FCITX_PENDING
            | TL_FCITX_COMPOSING | TL_FCITX_DETACHED));
    g_object_unref(context); g_object_unref(client);
    return ready && !target->dead && target->client == client && target->im_context == context;
}
gboolean tl_prefix_prepare(Target *target, guint64 id, const TlFcitxStateV1 *current) {
    if (!(standard_entry(target) || standard_textview(target)) || !target->im_context) return FALSE;
    GtkIMContext *context = g_object_ref(target->im_context);
    GtkWidget *widget = g_object_ref(target->client);
    const TlFcitxPrefixV1 *cap = prefix_capability(context);
    gboolean ready = cap && cap->prepare_caret(context, widget, id, current);
    g_object_unref(context); g_object_unref(widget);
    return ready && !target->dead && target->im_context == context;
}
void tl_prefix_cancel(Target *target, GtkIMContext *identity, guint64 id) {
    /* The saved identity is never dereferenced after its existing weak binding
       has gone. No new long-lived provider/widget reference or lifecycle hook. */
    if (!target || target->dead || !identity || target->im_context != identity) return;
    GtkIMContext *context = g_object_ref(target->im_context);
    const TlFcitxPrefixV1 *cap = prefix_capability(context);
    if (cap) cap->cancel(context, id);
    g_object_unref(context);
}
static gboolean same_state(const TlFcitxStateV1 *a, const TlFcitxStateV1 *b) {
    return a->context_generation == b->context_generation && a->epoch == b->epoch
        && a->input_epoch == b->input_epoch && a->server_serial == b->server_serial
        && a->applied_serial == b->applied_serial && a->flags == b->flags;
}
static gboolean read_ime(GtkWidget *widget, GtkIMContext *context, TlFcitxStateV1 *state) {
    const TlFcitxCapabilityV1 *capability = im_capability(context);
    return g_main_depth() == 1 && capability && capability->inspect(context, widget, state)
        && !gtk_widget_in_destruction(widget)
        && g_object_get_data(G_OBJECT(widget), TL_FCITX_CONTEXT_V1) == context
        && state->size == sizeof *state && state->version == TL_FCITX_ABI_V1
        && state->context_generation != 0
        && !(state->flags & (guint32)~(TL_FCITX_KNOWN | TL_FCITX_FOCUSED | TL_FCITX_PENDING
            | TL_FCITX_COMPOSING | TL_FCITX_DETACHED));
}
static gboolean state_clear(const TlFcitxStateV1 *state) {
    return state->flags == (TL_FCITX_KNOWN | TL_FCITX_FOCUSED)
        && state->server_serial == state->applied_serial;
}
static gboolean write_ime_clear(Target *target, gboolean direct_entry) {
    return target->im_observed && !target->composing
        && (state_clear(&target->im_state) || (direct_entry && standard_entry(target)
            && target->im_state.flags == (TL_FCITX_KNOWN | TL_FCITX_DETACHED)
            && target->im_state.server_serial == target->im_state.applied_serial));
}
gboolean tl_widget_ime_clear(GtkWidget *widget) {
    if (!widget || !gtk_widget_has_focus(widget)) return FALSE;
    GtkIMContext *context = g_object_get_data(G_OBJECT(widget), TL_FCITX_CONTEXT_V1);
    if (!im_capability(context)) return FALSE;
    g_object_ref(widget); g_object_ref(context);
    TlFcitxStateV1 state = {.size = sizeof state, .version = TL_FCITX_ABI_V1};
    gboolean clear = read_ime(widget, context, &state) && state_clear(&state);
    g_object_unref(context);
    clear = clear && gtk_widget_has_focus(widget)
        && g_object_get_data(G_OBJECT(widget), TL_FCITX_CONTEXT_V1) == context;
    g_object_unref(widget);
    return clear;
}
static gboolean inspect_ime(Target *target) {
    /* Select once before bind/probe callbacks. A lost ticket cannot turn this
       call into an ordinary new observation or acquire replacement authority. */
    guint64 pending = tl_ordered_pending_id(target);
    if (!target || target->dead || target->thread != g_thread_self() || target->im_probing
        || g_main_depth() != 1 || !bind_context(target)) {
        if (pending) { if (target) target->ime_known = FALSE; tl_clear_permit(); }
        return FALSE;
    }
    TlFcitxStateV1 state = {.size = sizeof state, .version = TL_FCITX_ABI_V1};
    GtkIMContext *context = g_object_ref(target->im_context);
    target->im_probing = TRUE;
    GtkWidget *widget = g_object_ref(target->client);
    gboolean valid = pending ? tl_ordered_finish(target, pending, &state) : read_ime(widget, context, &state);
    target->im_probing = FALSE;
    valid = valid && !target->dead && target->im_context == context;
    g_object_unref(context);
    valid = valid && !target->dead && target->im_context == context;
    g_object_unref(widget);
    if (!valid) {
        im_invalidated(target, 0);
        return FALSE;
    }
    if (target->im_observed && !same_state(&target->im_state, &state)) {
        guint causes = 0;
        if (target->im_state.context_generation != state.context_generation)
            causes |= TL_FCITX_CHANGE_BINDING;
        if (target->im_state.input_epoch != state.input_epoch) causes |= TL_FCITX_CHANGE_INPUT;
        if ((target->im_state.flags ^ state.flags) & TL_FCITX_FOCUSED) causes |= TL_FCITX_CHANGE_FOCUS;
        if (tl_ordered_observation(target, &state)) tl_drop_publication();
        else im_invalidated(target, causes);
    }
    target->im_state = state;
    target->im_observed = TRUE;
    target->ime_known = (state.flags & (TL_FCITX_KNOWN | TL_FCITX_FOCUSED | TL_FCITX_PENDING))
        == (TL_FCITX_KNOWN | TL_FCITX_FOCUSED) && state.server_serial == state.applied_serial;
    target->composing = (state.flags & TL_FCITX_COMPOSING) != 0;
    return TRUE;
}
gboolean tl_ime_clear(Target *target) {
    return target && !target->mutating && inspect_ime(target) && target->ime_known && !target->composing;
}
/* A detached source may back an unchanged, read-only menu preview. This is
 * deliberately not IME Clear and cannot authorize capture or source writes. */
gboolean tl_preview_detached_current(Target *target, JsonObject *basis) {
    JsonObject *stamp = tl_object(basis, "contextStamp");
    return target && !target->dead && !target->mutating && !target->quarantined && stamp
        && inspect_ime(target) && target->im_state.flags == (TL_FCITX_KNOWN | TL_FCITX_DETACHED)
        && target->im_state.server_serial == target->im_state.applied_serial
        && (gint64)target->im_state.context_generation == tl_integer(stamp, "context", -1)
        && (gint64)target->im_state.input_epoch == tl_integer(stamp, "input", -1)
        && (gint64)target->content_revision == tl_integer(basis, "contentRevision", -1)
        && (gint64)target->input_revision == tl_integer(basis, "inputRevision", -1)
        && tl_metadata_matches(target, basis);
}
static gboolean validate_ime_locally(Target *target, const TlFcitxStateV1 *baseline) {
    if (!target || target->dead || target->thread != g_thread_self() || target->im_probing
        || g_main_depth() != 1 || !bind_context(target)) return FALSE;
    GtkIMContext *context = g_object_ref(target->im_context);
    GtkWidget *widget = g_object_ref(target->client);
    const TlFcitxCapabilityV1 *capability = im_capability(context);
    target->im_probing = TRUE;
    gboolean valid = capability && capability->validate_local(context, widget, baseline);
    target->im_probing = FALSE;
    valid = valid && !target->dead && target->im_context == context
        && g_object_get_data(G_OBJECT(widget), TL_FCITX_CONTEXT_V1) == context;
    g_object_unref(widget); g_object_unref(context);
    valid = valid && !target->dead && target->im_context == context;
    if (!valid) im_invalidated(target, 0);
    return valid;
}

static void changed(Target *target) {
    if (!target->mutating) tl_invalidate(target, TRUE, FALSE);
}
static void entry_changed(GtkEditable *editable, gpointer data) { (void)editable; changed(data); }
static void text_buffer_changed(GtkTextBuffer *buffer, gpointer data) {
    (void)buffer;
    Target *target = data;
    target->storage_revision++;
    changed(target);
}
static void entry_inserted(GtkEntryBuffer *buffer, guint position, const gchar *text, guint count, gpointer data) {
    (void)buffer; (void)position; (void)text; (void)count;
    Target *target = data;
    target->storage_revision++;
}
static void entry_deleted(GtkEntryBuffer *buffer, guint position, guint count, gpointer data) {
    (void)buffer; (void)position; (void)count;
    Target *target = data;
    target->storage_revision++;
}
static void selection_changed(GObject *object, GParamSpec *property, gpointer data) {
    (void)object; (void)property;
    Target *target = data;
    if (!target->mutating) tl_invalidate(target, FALSE, FALSE);
}
static void text_mark_changed(GtkTextBuffer *buffer, GtkTextIter *where, GtkTextMark *mark, gpointer data) {
    (void)where;
    if (mark == gtk_text_buffer_get_insert(buffer) || mark == gtk_text_buffer_get_selection_bound(buffer))
        selection_changed(G_OBJECT(buffer), NULL, data);
}
static void tag_added_removed(GtkTextTagTable *table, GtkTextTag *tag, gpointer data) {
    (void)table; (void)tag;
    tl_invalidate(data, FALSE, FALSE);
}
static void tag_changed(GtkTextTagTable *table, GtkTextTag *tag, gboolean size_changed, gpointer data) {
    (void)size_changed;
    tag_added_removed(table, tag, data);
}
static void preedit(GtkWidget *widget, const gchar *text, gpointer data) {
    (void)widget;
    Target *target = data;
    /* The cooperating context classifies actual composition transitions and
       empty focus/reset notifications. Do not misclassify focus as source input. */
    if (bind_context(target)) return;
    /* An empty preedit signal does not expose asynchronous IM requests. */
    target->ime_known = FALSE;
    target->composing = text != NULL && *text != '\0';
    tl_invalidate(target, FALSE, FALSE);
}
static void focused(GObject *object, GParamSpec *property, gpointer data) {
    (void)object; (void)property;
    Target *target = data;
    target->ime_known = FALSE;
    tl_overlay_sync();
    tl_invalidate(target, FALSE, TRUE);
}
static void destroyed(gpointer data, GObject *object) {
    Target *target = data;
    target->dead = TRUE;
    target->widget = NULL;
    if (target->client && G_OBJECT(target->client) != object) {
        g_signal_handlers_disconnect_by_data(target->client, target);
        g_object_remove_weak_pointer(G_OBJECT(target->client), (gpointer *)&target->client);
    }
    target->client = NULL;
    if (target->im_context) {
        if (target->im_handler) g_signal_handler_disconnect(target->im_context, target->im_handler);
        g_object_remove_weak_pointer(G_OBJECT(target->im_context), (gpointer *)&target->im_context);
        target->im_context = NULL;
    }
    target->im_handler = 0;
    if (target->buffer) {
        g_signal_handlers_disconnect_by_data(target->buffer, target);
        g_clear_object(&target->buffer);
    }
    if (target->tag_table) {
        g_signal_handlers_disconnect_by_data(target->tag_table, target);
        g_clear_object(&target->tag_table);
    }
    tl_forget(target);
    /* Bounded identity tombstones also survive callbacks during a native write. */
}
static void attach_buffer(Target *target) {
    GObject *buffer = GTK_IS_ENTRY(target->widget)
        ? G_OBJECT(gtk_entry_get_buffer(GTK_ENTRY(target->widget)))
        : G_OBJECT(gtk_text_view_get_buffer(GTK_TEXT_VIEW(target->widget)));
    if (buffer == target->buffer) return;
    if (target->buffer) {
        g_signal_handlers_disconnect_by_data(target->buffer, target);
        g_object_unref(target->buffer);
    }
    if (target->tag_table) {
        g_signal_handlers_disconnect_by_data(target->tag_table, target);
        g_clear_object(&target->tag_table);
    }
    target->buffer = g_object_ref(buffer);
    target->storage_revision++;
    if (GTK_IS_ENTRY_BUFFER(buffer)) {
        g_signal_connect(buffer, "inserted-text", G_CALLBACK(entry_inserted), target);
        g_signal_connect_after(buffer, "deleted-text", G_CALLBACK(entry_deleted), target);
    }
    if (GTK_IS_TEXT_BUFFER(buffer)) {
        g_signal_connect(buffer, "changed", G_CALLBACK(text_buffer_changed), target);
        g_signal_connect(buffer, "mark-set", G_CALLBACK(text_mark_changed), target);
        target->tag_table = g_object_ref(G_OBJECT(gtk_text_buffer_get_tag_table(GTK_TEXT_BUFFER(buffer))));
        g_signal_connect(target->tag_table, "tag-added", G_CALLBACK(tag_added_removed), target);
        g_signal_connect(target->tag_table, "tag-removed", G_CALLBACK(tag_added_removed), target);
        g_signal_connect(target->tag_table, "tag-changed", G_CALLBACK(tag_changed), target);
    }
    tl_invalidate(target, TRUE, FALSE);
}
static void buffer_replaced(GObject *object, GParamSpec *property, gpointer data) {
    (void)object; (void)property;
    Target *target = data;
    if (!target->dead) attach_buffer(target);
}
void tl_discover(GtkWidget *client, gboolean before_input) {
    (void)before_input;
    if (!client || tl_overlay_contains(client)) return;
    GtkWidget *widget = client;
    if (G_OBJECT_TYPE(client) == GTK_TYPE_TEXT) {
        widget = gtk_widget_get_parent(client);
        if (!widget || G_OBJECT_TYPE(widget) != GTK_TYPE_ENTRY
            || gtk_editable_get_delegate(GTK_EDITABLE(widget)) != GTK_EDITABLE(client)) return;
    } else if (G_OBJECT_TYPE(client) != GTK_TYPE_TEXT_VIEW) return;
    if (!target_quark) target_quark = g_quark_from_static_string("typelay-gtk4-target");
    if (g_object_get_qdata(G_OBJECT(client), target_quark) || target_count >= 2048) return;
    if (!targets) targets = g_hash_table_new(g_str_hash, g_str_equal);
    Target *target = g_new0(Target, 1);
    target->widget = widget;
    target->client = client;
    target->thread = g_thread_self();
    target->id = g_uuid_string_random();
    target->content_revision = target->input_revision = target->focus_revision = 1;
    g_object_set_qdata(G_OBJECT(client), target_quark, target);
    if (client != widget) g_object_add_weak_pointer(G_OBJECT(client), (gpointer *)&target->client);
    g_object_weak_ref(G_OBJECT(widget), destroyed, target);
    g_hash_table_insert(targets, target->id, target);
    target_count++;
    g_signal_connect(client, "notify::has-focus", G_CALLBACK(focused), target);
    g_signal_connect(client, "preedit-changed", G_CALLBACK(preedit), target);
    g_signal_connect(widget, "notify::buffer", G_CALLBACK(buffer_replaced), target);
    if (GTK_IS_ENTRY(widget)) {
        g_signal_connect(widget, "changed", G_CALLBACK(entry_changed), target);
        g_signal_connect(client, "notify::cursor-position", G_CALLBACK(selection_changed), target);
        g_signal_connect(client, "notify::selection-bound", G_CALLBACK(selection_changed), target);
    }
    attach_buffer(target);
}
Target *tl_lookup(const char *id) {
    Target *target = targets && id ? g_hash_table_lookup(targets, id) : NULL;
    return target && !target->dead ? target : NULL;
}
Target *tl_focused(void) {
    GList *windows = gtk_window_list_toplevels();
    Target *result = NULL;
    for (GList *item = windows; item; item = item->next) {
        GtkWindow *window = GTK_WINDOW(item->data);
        if (!gtk_window_is_active(window)) continue;
        GtkWidget *widget = gtk_window_get_focus(window);
        if (widget && target_quark) result = g_object_get_qdata(G_OBJECT(widget), target_quark);
        if (result && !result->dead) break;
    }
    g_list_free(windows);
    return result && !result->dead ? result : NULL;
}
static gboolean supported(Target *target) {
    return (standard_entry(target) || (standard_textview(target) && plain_text_buffer(GTK_TEXT_BUFFER(target->buffer))))
        && gtk_widget_get_realized(target->widget)
        && gtk_widget_get_realized(target->client);
}
static gboolean sensitive(Target *target) {
    if (GTK_IS_TEXT_VIEW(target->widget)) {
        GtkInputPurpose purpose = gtk_text_view_get_input_purpose(GTK_TEXT_VIEW(target->widget));
        return purpose == GTK_INPUT_PURPOSE_PASSWORD || purpose == GTK_INPUT_PURPOSE_PIN;
    }
    GtkInputPurpose purpose = gtk_text_get_input_purpose(GTK_TEXT(target->client));
    return !gtk_entry_get_visibility(GTK_ENTRY(target->widget))
        || !gtk_text_get_visibility(GTK_TEXT(target->client))
        || purpose == GTK_INPUT_PURPOSE_PASSWORD || purpose == GTK_INPUT_PURPOSE_PIN;
}
static gboolean editable(Target *target) {
    if (GTK_IS_TEXT_VIEW(target->widget)) return gtk_text_view_get_editable(GTK_TEXT_VIEW(target->widget));
    return gtk_editable_get_editable(GTK_EDITABLE(target->widget))
        && gtk_editable_get_editable(GTK_EDITABLE(target->client));
}
static JsonObject *rectangle(gint x, gint y, gint width, gint height) {
    JsonObject *value = json_object_new();
    json_object_set_int_member(value, "x", x);
    json_object_set_int_member(value, "y", y);
    json_object_set_int_member(value, "width", width);
    json_object_set_int_member(value, "height", height);
    return value;
}
JsonObject *tl_geometry(Target *target) {
    if (!supported(target)) return NULL;
    GtkWidget *widget = target->widget;
    GtkRoot *root = gtk_widget_get_root(widget);
    if (!GTK_IS_WINDOW(root) || !gtk_widget_get_mapped(widget)) return NULL;
    GtkWidget *top = GTK_WIDGET(root);
    graphene_rect_t bounds;
    if (!gtk_widget_compute_bounds(widget, top, &bounds)) return NULL;
    gint width = gtk_widget_get_width(top), height = gtk_widget_get_height(top);
    for (GtkWidget *parent = gtk_widget_get_parent(widget); parent && parent != top;
         parent = gtk_widget_get_parent(parent)) {
        graphene_rect_t clip;
        if (!gtk_widget_get_mapped(parent) || !gtk_widget_compute_bounds(parent, top, &clip)
            || !graphene_rect_intersection(&bounds, &clip, &bounds)) return NULL;
    }
    gint x = MAX(0, (gint)ceil(bounds.origin.x));
    gint y = MAX(0, (gint)ceil(bounds.origin.y));
    gint right = MIN(width, (gint)floor(bounds.origin.x + bounds.size.width));
    gint lower = MIN(height, (gint)floor(bounds.origin.y + bounds.size.height));
    if (right <= x || lower <= y) return NULL;
    JsonObject *result = json_object_new();
    json_object_set_string_member(result, "space", "gtk4-toplevel");
    json_object_set_object_member(result, "bounds", rectangle(x, y, right - x, lower - y));
    json_object_set_int_member(result, "frameWidth", width);
    json_object_set_int_member(result, "frameHeight", height);
    json_object_set_int_member(result, "scale", gtk_widget_get_scale_factor(widget));
    return result;
}
static JsonObject *metadata(Target *target) {
    if (!supported(target)) return NULL;
    GtkWidget *widget = target->widget;
    GString *labels = g_string_new(NULL);
    GList *list = gtk_widget_list_mnemonic_labels(widget);
    for (GList *item = list; item && labels->len < 4096; item = item->next) {
        if (GTK_IS_LABEL(item->data)) {
            g_string_append(labels, gtk_label_get_text(GTK_LABEL(item->data)));
            g_string_append_c(labels, ' ');
        }
    }
    g_list_free(list);
    const char *name = GTK_IS_BUILDABLE(widget) ? gtk_buildable_get_buildable_id(GTK_BUILDABLE(widget)) : NULL;
    const char *tooltip = gtk_widget_get_tooltip_text(widget);
    JsonObject *value = json_object_new();
    json_object_set_string_member(value, "process", ""); /* authoritative /proc identity is added by the server */
    json_object_set_string_member(value, "label", labels->str);
    json_object_set_string_member(value, "automationId", name ? name : "");
    json_object_set_string_member(value, "help", tooltip ? tooltip : "");
    json_object_set_string_member(value, "aria", G_OBJECT_TYPE_NAME(widget));
    json_object_set_boolean_member(value, "password", sensitive(target));
    json_object_set_boolean_member(value, "enabled", gtk_widget_is_sensitive(widget));
    json_object_set_boolean_member(value, "readOnly", !editable(target));
    json_object_set_boolean_member(value, "offscreen", !gtk_widget_get_mapped(widget));
    json_object_set_boolean_member(value, "inDocument", FALSE);
    json_object_set_boolean_member(value, "multiline", GTK_IS_TEXT_VIEW(widget));
    json_object_set_boolean_member(value, "hasValue", TRUE);
    json_object_set_boolean_member(value, "hasTextEdit", published_context(target) != NULL);
    g_string_free(labels, TRUE);
    return value;
}
static char *metadata_key(JsonObject *value) {
    JsonNode *node = json_node_new(JSON_NODE_OBJECT);
    json_node_set_object(node, value);
    char *text = json_to_string(node, FALSE);
    char *hash = g_compute_checksum_for_string(G_CHECKSUM_SHA256, text, -1);
    json_node_free(node); g_free(text);
    return hash;
}
static JsonObject *authority(Target *target, const char *key) {
    JsonObject *value = json_object_new();
    json_object_set_string_member(value, "target", target->id);
    json_object_set_string_member(value, "metadataKey", key);
    json_object_set_int_member(value, "contentRevision", (gint64)target->content_revision);
    json_object_set_int_member(value, "inputRevision", (gint64)target->input_revision);
    json_object_set_int_member(value, "focusRevision", (gint64)target->focus_revision);
    if (target->im_observed) {
        JsonObject *stamp = json_object_new();
        json_object_set_int_member(stamp, "context", (gint64)target->im_state.context_generation);
        json_object_set_int_member(stamp, "epoch", (gint64)target->im_state.epoch);
        json_object_set_int_member(stamp, "input", (gint64)target->im_state.input_epoch);
        json_object_set_int_member(stamp, "server", (gint64)target->im_state.server_serial);
        json_object_set_int_member(stamp, "applied", (gint64)target->im_state.applied_serial);
        json_object_set_int_member(stamp, "flags", target->im_state.flags);
        json_object_set_object_member(value, "contextStamp", stamp);
    }
    json_object_set_int_member(value, "observedAtMs", tl_now());
    JsonObject *bindings = tl_session_bindings(target);
    if (bindings) json_object_set_object_member(value, "sessionBindings", bindings);
    return value;
}
JsonObject *tl_describe(Target *target) {
    if (!target || target->mutating || target->im_probing || target->thread != g_thread_self()) return NULL;
    (void)inspect_ime(target);
    JsonObject *fields = metadata(target);
    if (!fields) return NULL;
    char *key = metadata_key(fields);
    JsonObject *value = json_object_new(), *bounds = tl_geometry(target);
    json_object_set_string_member(value, "target", target->id);
    json_object_set_object_member(value, "metadata", fields);
    json_object_set_object_member(value, "authority", authority(target, key));
    JsonObject *bindings = tl_session_bindings(target);
    if (bindings) json_object_set_object_member(value, "sessionBindings", bindings);
    if (bounds) json_object_set_object_member(value, "geometry", bounds);
    else json_object_set_null_member(value, "geometry");
    json_object_set_boolean_member(value, "focused", tl_focused() == target);
    json_object_set_boolean_member(value, "sourceGlossarySupported", tl_source_glossary_supported(target));
    JsonObject *glossary = tl_overlay_glossary_source(target);
    if (glossary) json_object_set_object_member(value, "glossarySource", glossary);
    g_free(key);
    return value;
}
gboolean tl_metadata_matches(Target *target, JsonObject *approved) {
    if (!approved || !supported(target) || g_strcmp0(tl_string(approved, "target"), target->id) != 0) return FALSE;
    JsonObject *fields = metadata(target);
    if (!fields) return FALSE;
    char *key = metadata_key(fields);
    gboolean allowed = g_strcmp0(key, tl_string(approved, "metadataKey")) == 0 && !sensitive(target)
        && editable(target) && gtk_widget_is_sensitive(target->widget);
    json_object_unref(fields); g_free(key);
    return allowed;
}
static gint utf16_length(const char *text) {
    if (!text || !g_utf8_validate(text, -1, NULL)) return -1;
    gint length = 0;
    for (const char *p = text; *p; p = g_utf8_next_char(p)) {
        length += g_utf8_get_char(p) > 0xffff ? 2 : 1;
        if (length > TL_MAX_TEXT) return -1;
    }
    return length;
}
static gint char_to_utf16(const char *text, gint offset) {
    gint count = 0, units = 0;
    for (const char *p = text; *p && count < offset; p = g_utf8_next_char(p), count++)
        units += g_utf8_get_char(p) > 0xffff ? 2 : 1;
    return count == offset ? units : -1;
}
static gint utf16_to_char(const char *text, gint offset) {
    gint count = 0, units = 0;
    const char *p = text;
    while (*p && units < offset) {
        units += g_utf8_get_char(p) > 0xffff ? 2 : 1;
        p = g_utf8_next_char(p); count++;
    }
    return units == offset ? count : -1;
}
static char *read_text(Target *target, gint *start, gint *end) {
    char *text;
    if (standard_entry(target)) {
        text = g_strdup(gtk_entry_buffer_get_text(GTK_ENTRY_BUFFER(target->buffer)));
        if (!gtk_editable_get_selection_bounds(GTK_EDITABLE(target->widget), start, end))
            *start = *end = gtk_editable_get_position(GTK_EDITABLE(target->widget));
    } else if (standard_textview(target) && plain_text_buffer(GTK_TEXT_BUFFER(target->buffer))) {
        GtkTextBuffer *buffer = GTK_TEXT_BUFFER(target->buffer);
        GtkTextIter first, last;
        gtk_text_buffer_get_bounds(buffer, &first, &last);
        text = gtk_text_buffer_get_text(buffer, &first, &last, TRUE);
        gtk_text_buffer_get_selection_bounds(buffer, &first, &last);
        *start = gtk_text_iter_get_offset(&first); *end = gtk_text_iter_get_offset(&last);
    } else return NULL;
    if (utf16_length(text) < 0) { g_free(text); return NULL; }
    *start = char_to_utf16(text, *start); *end = char_to_utf16(text, *end);
    if (*start < 0 || *end < 0) { g_free(text); return NULL; }
    return text;
}
static gboolean stamp_matches(Target *target, JsonObject *approved) {
    JsonObject *stamp = tl_object(approved, "contextStamp");
    return stamp && target->im_observed
        && (gint64)target->im_state.context_generation == tl_integer(stamp, "context", -1)
        && (gint64)target->im_state.epoch == tl_integer(stamp, "epoch", -1)
        && (gint64)target->im_state.input_epoch == tl_integer(stamp, "input", -1)
        && (gint64)target->im_state.server_serial == tl_integer(stamp, "server", -1)
        && (gint64)target->im_state.applied_serial == tl_integer(stamp, "applied", -1)
        && (gint64)target->im_state.flags == tl_integer(stamp, "flags", -1);
}
JsonObject *tl_capture(Target *target, JsonObject *approved) {
    if (!tl_ime_clear(target) || target != tl_focused() || !stamp_matches(target, approved)) return NULL;
    gint64 age = tl_now() - tl_integer(approved, "observedAtMs", 0);
    if (age < 0 || age > 150 || !tl_metadata_matches(target, approved)) return NULL;
    guint64 storage = target->storage_revision, input = target->input_revision, focus = target->focus_revision;
    gint start, end;
    char *text = read_text(target, &start, &end);
    if (!text) return NULL;
    JsonObject *description = tl_describe(target);
    age = tl_now() - tl_integer(approved, "observedAtMs", 0);
    if (!description || !target->ime_known || target->composing || !stamp_matches(target, approved)
        || age < 0 || age > 150 || target != tl_focused() || target->storage_revision != storage
        || target->input_revision != input || target->focus_revision != focus
        || !tl_metadata_matches(target, approved)) {
        if (description) json_object_unref(description);
        g_free(text); return NULL;
    }
    json_object_set_string_member(description, "text", text);
    JsonObject *selection = json_object_new();
    json_object_set_int_member(selection, "start", start); json_object_set_int_member(selection, "end", end);
    json_object_set_object_member(description, "selection", selection);
    json_object_set_string_member(description, "ime", "Clear");
    json_object_set_boolean_member(description, "writeSupported", !target->quarantined
        && !tl_object(description, "glossarySource"));
    json_object_set_int_member(description, "observedAtMs", tl_now());
    g_free(text);
    return description;
}
static JsonObject *receipt(const char *status, const char *reason) {
    JsonObject *value = json_object_new();
    json_object_set_string_member(value, "status", status);
    json_object_set_string_member(value, "reason", reason);
    return value;
}
static gboolean write_check(gboolean allowed, const char *reason, const char **failure) {
    if (!allowed) *failure = reason;
    return allowed;
}
static const char *stamp_difference(const TlFcitxStateV1 *before, const TlFcitxStateV1 *after) {
    if (before->context_generation != after->context_generation) return "im-context";
    if (before->epoch != after->epoch) return "im-epoch";
    if (before->input_epoch != after->input_epoch) return "im-input";
    if (before->server_serial != after->server_serial) return "im-server";
    if (before->applied_serial != after->applied_serial) return "im-applied";
    return "im-flags";
}
static gboolean direct_authority_matches(Target *target, JsonObject *basis, JsonObject *approved) {
    JsonObject *original = tl_object(basis, "contextStamp");
    gint64 age = tl_now() - tl_integer(approved, "observedAtMs", 0);
    /* Focus/reset may advance the transport proof, never the frozen source.
       The current metadata approval authorizes a fresh proof for this same IC. */
    return original && target->im_observed
        && (gint64)target->im_state.context_generation == tl_integer(original, "context", -1)
        && (gint64)target->im_state.input_epoch == tl_integer(original, "input", -1)
        && (gint64)target->input_revision == tl_integer(basis, "inputRevision", -1)
        && (gint64)target->content_revision == tl_integer(basis, "contentRevision", -1)
        && stamp_matches(target, approved)
        && (gint64)target->input_revision == tl_integer(approved, "inputRevision", -1)
        && (gint64)target->content_revision == tl_integer(approved, "contentRevision", -1)
        && (gint64)target->focus_revision == tl_integer(approved, "focusRevision", -1)
        && age >= 0 && age <= 150;
}
typedef struct {
    GtkWidget *top, *focused;
    gboolean background, changed;
} EntryFocusGuard;
static void entry_focus_changed(GObject *object, gpointer detail, gpointer data) {
    (void)object; (void)detail;
    ((EntryFocusGuard *)data)->changed = TRUE;
}
static void entry_focus_clear(EntryFocusGuard *guard) {
    if (guard->top) g_signal_handlers_disconnect_by_data(guard->top, guard);
    g_clear_object(&guard->focused);
    g_clear_object(&guard->top);
}
G_DEFINE_AUTO_CLEANUP_CLEAR_FUNC(EntryFocusGuard, entry_focus_clear)

static gboolean entry_focus_matches(Target *target, const EntryFocusGuard *guard) {
    if (!standard_entry(target) || guard->changed || !guard->top || !guard->focused
        || gtk_widget_in_destruction(guard->top) || gtk_widget_in_destruction(guard->focused)
        || !gtk_widget_get_mapped(target->widget) || !gtk_widget_get_mapped(guard->focused)
        || GTK_WIDGET(gtk_widget_get_root(target->widget)) != guard->top
        || GTK_WIDGET(gtk_widget_get_root(guard->focused)) != guard->top
        || !gtk_window_is_active(GTK_WINDOW(guard->top))
        || gtk_window_get_focus(GTK_WINDOW(guard->top)) != guard->focused)
        return FALSE;
    if (!guard->background) return tl_focused() == target && gtk_widget_has_focus(target->client);
    Target *other = tl_focused();
    return !gtk_widget_has_focus(target->client) && !(guard->focused == target->client)
        && !tl_overlay_contains(guard->focused)
        && (!other || (other != target && other->buffer != target->buffer));
}
static gboolean entry_focus_init(Target *target, gboolean background, EntryFocusGuard *guard) {
    GtkWidget *top = GTK_WIDGET(gtk_widget_get_root(target->widget));
    if (!GTK_IS_WINDOW(top)) return FALSE;
    GtkWidget *focused_widget = gtk_window_get_focus(GTK_WINDOW(top));
    if (!focused_widget) return FALSE;
    guard->top = g_object_ref(top);
    guard->focused = g_object_ref(focused_widget);
    guard->background = background;
    g_signal_connect(top, "notify::focus-widget", G_CALLBACK(entry_focus_changed), guard);
    g_signal_connect(top, "notify::is-active", G_CALLBACK(entry_focus_changed), guard);
    return entry_focus_matches(target, guard);
}
/* Compile-time hooks for the shared TextBuffer transaction. The ticket stays
 * owned by the source dispatcher; this writer neither retains nor renews it. */
static gboolean textview_ticket_check(gpointer ticket, Target *target, gboolean source, gboolean proof) {
    return !ticket || tl_ordered_check(ticket, target, source, proof);
}
static gboolean textview_ticket_prepare(gpointer ticket, Target *target, const TlFcitxStateV1 *state) {
    return ticket && tl_ordered_prepare(ticket, target, state);
}
static void textview_take(gpointer ticket) {
    if (ticket) tl_drop_publication();
    else tl_clear_permit();
}
static gboolean textview_ticket_claim(gpointer ticket, Target *target) {
    return !ticket || tl_ordered_claim(ticket, target);
}
#include "../common/gtk-textbuffer-write.inc"

/* Failure-only comparisons of held state; never query the provider or body. */
static JsonObject *direct_epoch_rejected(Target *target, JsonObject *basis, JsonObject *approved,
    gboolean direct, const char *phase) {
    static guint emitted;
    if (direct && g_getenv("TYPELAY_TRACE") && emitted < 16) {
        emitted++;
        JsonObject *stamp = tl_object(basis, "contextStamp");
        gint64 age = tl_now() - tl_integer(approved, "observedAtMs", 0);
        g_printerr("TypeLay GTK4 direct rejection phase=%s basisInput=%d basisContent=%d "
            "approvedInput=%d approvedContent=%d approvedFocus=%d "
            "basisContext=%d basisImeInput=%d approvedStamp=%d approvedAge=%d\n", phase,
            (gint64)target->input_revision == tl_integer(basis, "inputRevision", -1),
            (gint64)target->content_revision == tl_integer(basis, "contentRevision", -1),
            (gint64)target->input_revision == tl_integer(approved, "inputRevision", -1),
            (gint64)target->content_revision == tl_integer(approved, "contentRevision", -1),
            (gint64)target->focus_revision == tl_integer(approved, "focusRevision", -1),
            (gint64)target->im_state.context_generation == tl_integer(stamp, "context", -1),
            (gint64)target->im_state.input_epoch == tl_integer(stamp, "input", -1),
            stamp_matches(target, approved), age >= 0 && age <= 150);
    }
    return receipt("RejectedBeforeMutation", "EpochChanged");
}

static JsonObject *write_source(Target *target, JsonObject *request, gboolean direct, OrderedTicket *ticket) {
    JsonObject *plan = tl_object(request, "plan"), *basis = tl_object(request, "authority");
    JsonObject *approved = direct ? tl_object(request, "approved") : basis;
    JsonObject *expected = tl_object(plan, "expected");
    if (!target || !plan || !expected || !basis || !approved)
        return receipt("RejectedBeforeMutation", "BadPlan");
    const char *generation = tl_string(request, "generation");
    if (!tl_configuration_current(generation)
        || (!direct && !tl_session_matches(tl_object(basis, "sessionBindings"))))
        return direct_epoch_rejected(target, basis, approved, direct, "generation-before-ime");
    gboolean direct_entry = direct && standard_entry(target);
    if (target->mutating || target->quarantined || !inspect_ime(target)
        || !write_ime_clear(target, direct_entry))
        return receipt("RejectedBeforeMutation", "ImeUnclear");
    if (!tl_configuration_current(generation)
        || (!direct && !tl_session_matches(tl_object(basis, "sessionBindings"))))
        return direct_epoch_rejected(target, basis, approved, direct, "generation-after-ime");
    gint64 now = tl_now(), until = tl_integer(request, "expiresAtMs", 0);
    if (until < now || until - now > (direct ? 250 : 100))
        return receipt("RejectedBeforeMutation", "StaleObservation");
    if (!tl_metadata_matches(target, approved) || !tl_metadata_matches(target, basis))
        return receipt("RejectedBeforeMutation", "Sensitive");
    if (ticket ? !tl_ordered_check(ticket, target, TRUE, TRUE)
        : direct_entry ? !direct_authority_matches(target, basis, approved) : !stamp_matches(target, basis))
        return direct_epoch_rejected(target, basis, approved, direct, "authority");
    if ((gint64)target->content_revision != tl_integer(basis, "contentRevision", -1))
        return receipt("RejectedBeforeMutation", "SourceChanged");
    if ((gint64)target->input_revision != tl_integer(basis, "inputRevision", -1)
        || (!direct_entry && (tl_focused() != target
            || (gint64)target->focus_revision != tl_integer(basis, "focusRevision", -1))))
        return direct_epoch_rejected(target, basis, approved, direct, "input-or-focus");
    g_auto(EntryFocusGuard) focus_guard = {0};
    if (standard_entry(target) && !entry_focus_init(target,
        target->im_state.flags == (TL_FCITX_KNOWN | TL_FCITX_DETACHED), &focus_guard))
        return direct_epoch_rejected(target, basis, approved, direct, "entry-focus");
    gint start, end;
    char *text = read_text(target, &start, &end);
    const char *original = tl_string(expected, "text"), *replacement = tl_string(plan, "replacement");
    const char *result = tl_string(plan, "resultText");
    if (!text || !original || !replacement || !result || strcmp(original, text) != 0) {
        g_free(text); return receipt("RejectedBeforeMutation", "SourceChanged");
    }
    JsonObject *selection = tl_object(expected, "selection");
    if (start != tl_integer(selection, "start", -1) || end != tl_integer(selection, "end", -1)) {
        g_free(text); return receipt("RejectedBeforeMutation", "SelectionChanged");
    }
    gint units = utf16_length(text), replacement_units = utf16_length(replacement);
    JsonObject *scope = tl_object(plan, "scope");
    gint first = scope ? (gint)tl_integer(scope, "start", -1) : 0;
    gint last = scope ? (gint)tl_integer(scope, "end", -1) : units;
    if (first < 0 || last < first || last > units || (scope && first == last)
        || replacement_units <= 0 || utf16_length(result) < 0
        || (GTK_IS_ENTRY(target->widget) && strpbrk(replacement, "\r\n"))) {
        g_free(text); return receipt("RejectedBeforeMutation", "InvalidText");
    }
    gint from = utf16_to_char(text, first), to = utf16_to_char(text, last);
    gint caret = (gint)tl_integer(plan, "caret", -1);
    if (from < 0 || to < 0 || caret != first + replacement_units) {
        g_free(text); return receipt("RejectedBeforeMutation", "BadPlan");
    }
    const char *begin = g_utf8_offset_to_pointer(text, from), *finish = g_utf8_offset_to_pointer(text, to);
    GString *deleted = g_string_new_len(text, (gssize)(begin - text));
    g_string_append(deleted, finish);
    char *after_delete = g_string_free(deleted, FALSE);
    GString *computed = g_string_new_len(text, (gssize)(begin - text));
    g_string_append(computed, replacement); g_string_append(computed, finish);
    gboolean matches = strcmp(computed->str, result) == 0 && utf16_length(computed->str) >= 0;
    g_string_free(computed, TRUE);
    if (!matches) { g_free(after_delete); g_free(text); return receipt("RejectedBeforeMutation", "BadPlan"); }
    if (standard_textview(target)) {
        JsonObject *reply = write_textview(target, plan, basis, approved, text, after_delete, from, to, caret, until, generation, !direct, ticket);
        g_free(after_delete); g_free(text);
        return reply;
    }
    gint limit = gtk_entry_buffer_get_max_length(GTK_ENTRY_BUFFER(target->buffer));
    if (limit > 0 && g_utf8_strlen(result, -1) > limit) {
        g_free(after_delete); g_free(text); return receipt("RejectedBeforeMutation", "InvalidText");
    }
    /* The actual server inspection above establishes the pre-mutation barrier.
       From the first storage change onward this is a non-yielding local GTK
       transaction. Never insert an IPC timeout or renew the consumed permit in
       the middle of a partially changed field. */
    GtkWidget *widget = g_object_ref(target->widget);
    GObject *buffer = g_object_ref(target->buffer);
    guint64 storage = target->storage_revision, input = target->input_revision, focus = target->focus_revision;
    guint64 content = target->content_revision;
    gint old_cursor = gtk_editable_get_position(GTK_EDITABLE(widget)), old_bound = 0;
    g_object_get(target->client, "selection-bound", &old_bound, NULL);
    gint deleted_cursor = old_cursor - CLAMP(old_cursor - from, 0, to - from);
    gint deleted_bound = old_bound - CLAMP(old_bound - from, 0, to - from);
    TlFcitxStateV1 state = target->im_state;
    gboolean local_valid = validate_ime_locally(target, &state) && entry_focus_matches(target, &focus_guard);
    if (!local_valid || tl_now() > until) {
        g_object_unref(buffer); g_object_unref(widget); g_free(after_delete); g_free(text);
        return receipt("RejectedBeforeMutation", local_valid ? "StaleObservation" : "ImeUnclear");
    }
    gint replacement_chars = (gint)g_utf8_strlen(replacement, -1);
    gint expected_cursor = deleted_cursor + (deleted_cursor > from ? replacement_chars : 0);
    gint expected_bound = deleted_bound + (deleted_bound > from ? replacement_chars : 0);
    if (expected_cursor != from + replacement_chars || expected_bound != from + replacement_chars) {
        /* GtkEntry's final caret move normally resets its IM after changing the
           buffer. Move that same reset before the first storage mutation, when
           the original snapshot is still intact. The provider batches genuine
           reset output; an ordinary asynchronous reset would leave server and
           applied serials unequal until a later UI dispatch. Never reset an IM
           that was not already proven Clear by the original authorization. */
        GtkIMContext *context = g_object_ref(target->im_context);
        const TlFcitxCapabilityV1 *capability = im_capability(context);
        target->im_probing = TRUE;
        gboolean prepared = ticket ? tl_ordered_prepare(ticket, target, &state)
            : capability && capability->prepare_caret(context, target->client, &state);
        target->im_probing = FALSE;
        const char *preparation_failure = NULL;
        gboolean ready = write_check(prepared, "caret-reset", &preparation_failure)
            && write_check(!target->dead && target->widget == widget, "destroyed", &preparation_failure)
            && write_check(!target->quarantined, "quarantined", &preparation_failure)
            && write_check(target->buffer == buffer, "buffer", &preparation_failure)
            && write_check(target->im_context == context, "im-context", &preparation_failure)
            && write_check(!ticket || tl_ordered_check(ticket, target, TRUE, FALSE), "ticket", &preparation_failure)
            && write_check(inspect_ime(target) && write_ime_clear(target, direct_entry),
                "im-unclear", &preparation_failure)
            && write_check(!ticket || tl_ordered_check(ticket, target, TRUE, TRUE), "ticket-proof", &preparation_failure)
            && write_check(target->im_state.context_generation == state.context_generation,
                "im-context", &preparation_failure)
            && write_check(target->im_state.input_epoch == state.input_epoch
                && target->input_revision == input, "input-revision", &preparation_failure)
            && write_check(target->focus_revision == focus && entry_focus_matches(target, &focus_guard),
                "focus", &preparation_failure)
            && write_check(target->storage_revision == storage && target->content_revision == content,
                "storage-revision", &preparation_failure)
            && write_check(tl_metadata_matches(target, approved) && tl_metadata_matches(target, basis),
                "metadata", &preparation_failure);
        gint prepared_bound = -1;
        if (ready) g_object_get(target->client, "selection-bound", &prepared_bound, NULL);
        ready = ready && write_check(gtk_editable_get_position(GTK_EDITABLE(widget)) == old_cursor
                && prepared_bound == old_bound, "selection", &preparation_failure)
            && write_check(strcmp(gtk_entry_buffer_get_text(GTK_ENTRY_BUFFER(buffer)), text) == 0,
                "body", &preparation_failure);
        if (ready) {
            limit = gtk_entry_buffer_get_max_length(GTK_ENTRY_BUFFER(buffer));
            ready = write_check(limit <= 0 || g_utf8_strlen(result, -1) <= limit,
                "max-length", &preparation_failure);
        }
        if (ready) {
            state = target->im_state;
            ready = write_check(validate_ime_locally(target, &state), "im-local", &preparation_failure)
                && write_check(tl_now() <= until, "expiry", &preparation_failure);
        }
        g_object_unref(context);
        /* GTK3 may replace its IM delegate during reset. Releasing our last
           temporary reference must expose that unbinding before storage changes. */
        ready = ready && target->im_context == context
            && validate_ime_locally(target, &state) && entry_focus_matches(target, &focus_guard);
        if (!ready) {
            if (g_getenv("TYPELAY_TRACE"))
                g_printerr("TypeLay GTK4 write guard phase=prepare check=%s remainingMs=%" G_GINT64_FORMAT "\n",
                    preparation_failure ? preparation_failure : "unknown", until - tl_now());
            g_object_unref(buffer); g_object_unref(widget); g_free(after_delete); g_free(text);
            return receipt("RejectedBeforeMutation", tl_now() > until ? "StaleObservation" : "ImeUnclear");
        }
    }
    if (ticket) tl_drop_publication();
    else tl_clear_permit();
    if (!tl_configuration_current(generation)
        || (!direct && !tl_session_matches(tl_object(basis, "sessionBindings")))) {
        JsonObject *rejected = direct_epoch_rejected(target, basis, approved, direct, "after-publication-take");
        g_object_unref(buffer); g_object_unref(widget); g_free(after_delete); g_free(text);
        return rejected;
    }
    gint final_bound = -1;
    if (!target->dead) g_object_get(target->client, "selection-bound", &final_bound, NULL);
    if (target->dead || target->widget != widget || target->buffer != buffer
        || target->storage_revision != storage || target->content_revision != content
        || target->input_revision != input || target->focus_revision != focus
        || !validate_ime_locally(target, &state) || !entry_focus_matches(target, &focus_guard)
        || !tl_metadata_matches(target, approved) || !tl_metadata_matches(target, basis)
        || gtk_editable_get_position(GTK_EDITABLE(widget)) != old_cursor || final_bound != old_bound
        || strcmp(gtk_entry_buffer_get_text(GTK_ENTRY_BUFFER(buffer)), text) != 0
        || tl_now() > until || !tl_configuration_current(generation)
        || (!direct && !tl_session_matches(tl_object(basis, "sessionBindings")))) {
        JsonObject *rejected = direct_epoch_rejected(target, basis, approved, direct, "before-mutation");
        g_object_unref(buffer); g_object_unref(widget); g_free(after_delete); g_free(text);
        return rejected;
    }
    if (ticket && !tl_ordered_claim(ticket, target)) {
        JsonObject *rejected = direct_epoch_rejected(target, basis, approved, direct, "ticket-claim");
        g_object_unref(buffer); g_object_unref(widget); g_free(after_delete); g_free(text);
        return rejected;
    }
    target->mutating = TRUE;
    /* Use the standard native buffer operation in this same GUI dispatch.
       GTK4 deleted-text runs its storage closure after ordinary handlers. Any
       application rewriting the field or dispatching nested input invalidates
       the local proof/receipt; never retry or restore over its newer content. */
    guint removed = gtk_entry_buffer_delete_text(GTK_ENTRY_BUFFER(buffer), (guint)from, to - from);
    gint after_bound = -1;
    if (!target->dead) g_object_get(target->client, "selection-bound", &after_bound, NULL);
    guint64 expected_storage = storage + (to > from ? 1u : 0u);
    const char *failure = NULL, *phase = "delete";
    gboolean intermediate = write_check(!ticket || tl_ordered_check(ticket, target, FALSE, TRUE), "ticket", &failure)
        && write_check(tl_configuration_current(generation)
            && (direct || tl_session_matches(tl_object(basis, "sessionBindings"))),
            "generation", &failure)
        && write_check(removed == (guint)(to - from), "delete-count", &failure)
        && write_check(!target->dead, "destroyed", &failure)
        && write_check(!target->quarantined, "quarantined", &failure)
        && write_check(target->buffer == buffer, "buffer", &failure)
        && write_check(target->storage_revision == expected_storage, "storage-revision", &failure)
        && write_check(validate_ime_locally(target, &state), "im-local", &failure)
        && write_check(write_ime_clear(target, direct_entry), "im-unclear", &failure)
        && write_check(same_state(&state, &target->im_state), stamp_difference(&state, &target->im_state), &failure)
        && write_check(target->input_revision == input, "input-revision", &failure)
        && write_check(target->focus_revision == focus && entry_focus_matches(target, &focus_guard), "focus", &failure)
        && write_check(gtk_editable_get_position(GTK_EDITABLE(widget)) == deleted_cursor
            && after_bound == deleted_bound, "selection", &failure)
        && write_check(tl_metadata_matches(target, approved), "metadata", &failure)
        && write_check(strcmp(gtk_entry_buffer_get_text(GTK_ENTRY_BUFFER(buffer)), after_delete) == 0, "body", &failure)
        && write_check(tl_configuration_current(generation)
            && (direct || tl_session_matches(tl_object(basis, "sessionBindings"))), "generation", &failure);
    guint inserted = 0;
    gboolean positioned = FALSE;
    if (intermediate) {
        phase = "insert";
        inserted = gtk_entry_buffer_insert_text(GTK_ENTRY_BUFFER(buffer), (guint)from, replacement, replacement_chars);
        expected_storage++;
        gint inserted_bound = -1;
        if (!target->dead) g_object_get(target->client, "selection-bound", &inserted_bound, NULL);
        if (write_check(!ticket || tl_ordered_check(ticket, target, FALSE, TRUE), "ticket", &failure)
            && write_check(!target->dead, "destroyed", &failure)
            && write_check(!target->quarantined, "quarantined", &failure)
            && write_check(target->buffer == buffer, "buffer", &failure)
            && write_check(target->storage_revision == expected_storage, "storage-revision", &failure)
            && write_check(inserted == (guint)replacement_chars, "insert-count", &failure)
            && write_check(validate_ime_locally(target, &state), "im-local", &failure)
            && write_check(write_ime_clear(target, direct_entry), "im-unclear", &failure)
            && write_check(same_state(&state, &target->im_state), stamp_difference(&state, &target->im_state), &failure)
            && write_check(target->input_revision == input, "input-revision", &failure)
            && write_check(target->focus_revision == focus && entry_focus_matches(target, &focus_guard), "focus", &failure)
            && write_check(gtk_editable_get_position(GTK_EDITABLE(widget)) == expected_cursor
                && inserted_bound == expected_bound, "selection", &failure)
            && write_check(tl_metadata_matches(target, approved), "metadata", &failure)
            && write_check(tl_configuration_current(generation)
                && (direct || tl_session_matches(tl_object(basis, "sessionBindings"))), "generation", &failure)) {
            positioned = TRUE;
            phase = "caret";
            gtk_editable_set_position(GTK_EDITABLE(widget), from + replacement_chars);
        }
    }
    gboolean clear = positioned
        && write_check(!ticket || tl_ordered_check(ticket, target, FALSE, TRUE), "ticket", &failure)
        && write_check(validate_ime_locally(target, &state), "im-local", &failure)
        && write_check(!target->quarantined, "quarantined", &failure)
        && write_check(write_ime_clear(target, direct_entry), "im-unclear", &failure)
        && write_check(same_state(&state, &target->im_state), stamp_difference(&state, &target->im_state), &failure)
        && write_check(target->input_revision == input, "input-revision", &failure)
        && write_check(target->focus_revision == focus && entry_focus_matches(target, &focus_guard), "focus", &failure)
        && write_check(target->buffer == buffer && target->storage_revision == expected_storage, "storage-revision", &failure)
        && write_check(tl_metadata_matches(target, approved), "metadata", &failure);
    char *after = clear ? read_text(target, &start, &end) : NULL;
    gboolean verified = clear
        && write_check(!ticket || tl_ordered_check(ticket, target, FALSE, TRUE), "ticket", &failure)
        && write_check(after && strcmp(after, result) == 0, "body", &failure)
        && write_check(start == caret && end == caret, "selection", &failure)
        && write_check(tl_configuration_current(generation)
            && (direct || tl_session_matches(tl_object(basis, "sessionBindings"))), "generation", &failure);
    if (!verified && g_getenv("TYPELAY_TRACE"))
        g_printerr("TypeLay GTK4 write guard phase=%s check=%s remainingMs=%" G_GINT64_FORMAT
            " storageDelta=%" G_GUINT64_FORMAT " epochBefore=%" G_GUINT64_FORMAT " cachedEpochAfter=%" G_GUINT64_FORMAT "\n",
            phase, failure ? failure : "unknown", until - tl_now(), target->storage_revision - storage,
            state.epoch, target->im_state.epoch);
    target->mutating = FALSE;
    target->content_revision++;
    JsonObject *reply = receipt(verified ? "Verified" : "OutcomeUnknown", verified ? "None" : "UnknownOutcome");
    if (verified) {
        JsonObject *snapshot = tl_copy(expected);
        json_object_set_string_member(snapshot, "text", after);
        JsonObject *at = json_object_new();
        json_object_set_int_member(at, "start", caret); json_object_set_int_member(at, "end", caret);
        json_object_set_object_member(snapshot, "selection", at);
        json_object_set_int_member(snapshot, "observedAtMs", tl_now());
        json_object_set_string_member(snapshot, "ime", "Clear");
        json_object_set_string_member(snapshot, "access", "Supported");
        json_object_set_object_member(reply, "verifiedSnapshot", snapshot);
    } else target->quarantined = TRUE;
    g_free(after_delete); g_free(after); g_free(text); g_object_unref(buffer); g_object_unref(widget);
    return reply;
}
JsonObject *tl_write(Target *target, JsonObject *request, gboolean direct) {
    return write_source(target, request, direct, NULL);
}
JsonObject *tl_write_ordered(Target *target, JsonObject *request, OrderedTicket *ticket) {
    if (!ticket || !(standard_entry(target) || standard_textview(target))
        || !tl_ordered_check(ticket, target, TRUE, FALSE))
        return receipt("RejectedBeforeMutation", "EpochChanged");
    return write_source(target, request, FALSE, ticket);
}
static gboolean return_authorized(Target *target, JsonObject *basis) {
    JsonObject *stamp = tl_object(basis, "contextStamp");
    const char *failure = NULL;
    gboolean allowed = write_check(tl_session_matches(tl_object(basis, "sessionBindings")), "generation", &failure)
        && write_check(target && !target->dead, "target", &failure)
        && write_check(!target->mutating && !target->im_probing && !target->quarantined, "busy", &failure)
        && write_check(target->thread == g_thread_self() && g_main_depth() == 1, "dispatch", &failure)
        && write_check(bind_context(target) && target->im_observed && stamp, "binding", &failure)
        && write_check((gint64)target->im_state.context_generation == tl_integer(stamp, "context", -1), "context", &failure)
        && write_check((gint64)target->input_revision == tl_integer(basis, "inputRevision", -1), "input", &failure)
        && write_check((gint64)target->content_revision == tl_integer(basis, "contentRevision", -1), "content", &failure)
        && write_check(tl_metadata_matches(target, basis), "metadata", &failure)
        && write_check(tl_session_matches(tl_object(basis, "sessionBindings")), "generation", &failure);
    if (!allowed && g_getenv("TYPELAY_TRACE"))
        g_printerr("TypeLay GTK4 return check=%s input=%" G_GUINT64_FORMAT " expectedInput=%" G_GINT64_FORMAT
            " atMs=%" G_GINT64_FORMAT "\n", failure, target ? target->input_revision : 0,
            tl_integer(basis, "inputRevision", -1), tl_now());
    return allowed;
}
gboolean tl_return_ready(Target *target, JsonObject *basis) {
    return return_authorized(target, basis) && tl_focused() == target && tl_ime_clear(target)
        && return_authorized(target, basis);
}
gboolean tl_return(Target *target, JsonObject *basis) {
    if (g_getenv("TYPELAY_TRACE"))
        g_printerr("TypeLay GTK4 return check=request atMs=%" G_GINT64_FORMAT "\n", tl_now());
    if (!return_authorized(target, basis)) return FALSE;
    GtkWidget *top = GTK_WIDGET(gtk_widget_get_root(target->widget));
    if (!GTK_IS_WINDOW(top) || !gtk_window_is_active(GTK_WINDOW(top))) {
        if (g_getenv("TYPELAY_TRACE")) g_printerr("TypeLay GTK4 return check=window\n");
        return FALSE;
    }
    if (tl_focused() != target && !tl_overlay_can_return(target)) {
        if (g_getenv("TYPELAY_TRACE")) g_printerr("TypeLay GTK4 return check=editor-owner\n");
        return FALSE;
    }
    /* Restore only the saved permitted widget. Its IM may be unknown while the
       overlay owns focus; no body is read here. The caller awaits real Clear
       before reporting return completion and allowing the next capture. */
    if (GTK_IS_ENTRY(target->widget)) gtk_entry_grab_focus_without_selecting(GTK_ENTRY(target->widget));
    else gtk_widget_grab_focus(target->widget);
    return return_authorized(target, basis) && tl_focused() == target;
}
