#define _GNU_SOURCE
#include "gtk-target.h"
#include "gtk-overlay.h"
#include "typelay-gtk4.h"
#include <gdk/wayland/gdkwayland.h>
#include <errno.h>
#include <fcntl.h>
#include <stddef.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <time.h>
#include <unistd.h>

static gint connection = -1;
static guint input_watch, output_watch, publication_timer;
static GByteArray *incoming, *outgoing;
static char *lifetime;
static JsonObject *publication, *shortcut_configuration, *shortcut_authority;
static guint shortcut_key;
static gboolean shortcut_consumed;
/* Bounded session candidates, retained across configuration, field focus and
 * popover destruction. A refused physical down never becomes a later accept. */
static struct { guint keycode, keyval; guint64 sequence; gboolean consumed; } held_keys[64];
static guint64 held_sequence, configuration_sequence;
static gboolean configuring, held_overflow;
static gboolean initialized;
struct OrderedTicket {
    guint refs;
    guint64 id, connection, configuration, content, input, focus, storage;
    guint keycode, time, claim_keycode;
    guint64 claim_sequence;
    gint64 accept_until, exit_until;
    Target *target;
    GtkWidget *widget, *client; /* Existing weak Target owns identities, never references. */
    GObject *buffer;
    GtkIMContext *context;
    TlFcitxStateV1 before;
    JsonObject *publication;
    gboolean finished, finishing, revoked, claimed, deciding;
};
static OrderedTicket *ordered;
static guint64 ordered_sequence;
static void ordered_revoke(void);
static void ordered_begin(GdkEvent *event);
static gboolean same_ordered_publication(JsonObject *next);

static guint64 connection_generation, return_generation, shortcut_sequence;
static gint64 shortcut_started_at = -1;
static GQueue consumed_ids = G_QUEUE_INIT;
static gboolean trigger_shortcut(const char *kind, Target *target, JsonObject *approved, gint64 triggered_at, gboolean require_revisions);
static gboolean already_consumed(const char *id);

static void trace_connection(const char *check, gint error) {
    static const char *previous;
    static gint previous_error;
    if (!g_getenv("TYPELAY_TRACE")) return;
    if (g_strcmp0(previous, check) == 0 && previous_error == error) return;
    previous = check; previous_error = error;
    g_printerr("TypeLay GTK4 connect check=%s errno=%d\n", check, error);
}
static void trace_attach(const char *check) {
    static const char *previous;
    if (g_getenv("TYPELAY_TRACE") && g_strcmp0(previous, check) != 0) {
        previous = check;
        g_printerr("TypeLay GTK4 attach check=%s\n", check);
    }
}
static gboolean attach_check(gboolean valid, const char *check) {
    if (valid) return TRUE;
    trace_attach(check);
    return FALSE;
}

const char *tl_string(JsonObject *object, const char *member) {
    if (!object || !json_object_has_member(object, member)) return NULL;
    JsonNode *node = json_object_get_member(object, member);
    return JSON_NODE_HOLDS_VALUE(node) && json_node_get_value_type(node) == G_TYPE_STRING
        ? json_node_get_string(node) : NULL;
}
gint64 tl_integer(JsonObject *object, const char *member, gint64 fallback) {
    if (!object || !json_object_has_member(object, member)) return fallback;
    JsonNode *node = json_object_get_member(object, member);
    return JSON_NODE_HOLDS_VALUE(node) && json_node_get_value_type(node) == G_TYPE_INT64
        ? json_node_get_int(node) : fallback;
}
JsonObject *tl_object(JsonObject *object, const char *member) {
    if (!object || !json_object_has_member(object, member)) return NULL;
    JsonNode *node = json_object_get_member(object, member);
    return JSON_NODE_HOLDS_OBJECT(node) ? json_node_get_object(node) : NULL;
}
static gboolean boolean(JsonObject *object, const char *member) {
    JsonNode *node = object ? json_object_get_member(object, member) : NULL;
    return node && JSON_NODE_HOLDS_VALUE(node) && json_node_get_value_type(node) == G_TYPE_BOOLEAN
        && json_node_get_boolean(node);
}
static guint session_key(const char *value, gboolean accept) {
    if (g_strcmp0(value, accept ? "Tab" : "Esc") == 0) return accept ? GDK_KEY_Tab : GDK_KEY_Escape;
    /* Only canonical Ctrl+Fn is added; no aliases or other modifiers. */
    if (value && g_str_has_prefix(value, "Ctrl+")) value += 5;
    for (guint index = 1; index <= 24; index++) {
        char canonical[4];
        g_snprintf(canonical, sizeof canonical, "F%u", index);
        if (g_strcmp0(value, canonical) == 0) return GDK_KEY_F1 + index - 1;
    }
    return 0;
}
static gboolean control_binding(gboolean accept) {
    const char *value = tl_string(shortcut_configuration, accept ? "accept" : "exit");
    return value && g_str_has_prefix(value, "Ctrl+");
}
static gboolean control_pair(void) { return control_binding(TRUE) || control_binding(FALSE); }
static gboolean valid_pair(void) {
    guint accept = session_key(tl_string(shortcut_configuration, "accept"), TRUE);
    guint exit = session_key(tl_string(shortcut_configuration, "exit"), FALSE);
    return accept && exit && accept != exit;
}
gboolean tl_configuration_current(const char *generation) {
    return !configuring && generation && g_uuid_string_is_valid(generation)
        && g_strcmp0(generation, tl_string(shortcut_configuration, "generation")) == 0;
}
guint tl_session_keyval(gboolean accept) {
    return !configuring && boolean(shortcut_configuration, "sessionEnabled") && valid_pair()
        ? session_key(tl_string(shortcut_configuration, accept ? "accept" : "exit"), accept) : 0;
}
gboolean tl_session_matches(JsonObject *bindings) {
    return tl_session_keyval(TRUE) && tl_configuration_current(tl_string(bindings, "generation"))
        && g_strcmp0(tl_string(bindings, "accept"), tl_string(shortcut_configuration, "accept")) == 0
        && g_strcmp0(tl_string(bindings, "exit"), tl_string(shortcut_configuration, "exit")) == 0;
}
gboolean tl_controller_attached(GtkWidget *widget, const char *key) {
    GtkEventController *controller = widget ? g_object_get_data(G_OBJECT(widget), key) : NULL;
    /* The qdata owns its reference even if the host detaches the controller. */
    return controller && gtk_event_controller_get_widget(controller) == widget
        && gtk_event_controller_get_propagation_phase(controller) == GTK_PHASE_CAPTURE
        && gtk_event_controller_get_propagation_limit(controller) == GTK_LIMIT_SAME_NATIVE;
}
JsonObject *tl_session_bindings(Target *target) {
    const char *generation = tl_string(shortcut_configuration, "generation");
    if (!tl_configuration_current(generation) || !valid_pair()) return NULL;
    JsonObject *bindings = json_object_new();
    json_object_set_string_member(bindings, "generation", generation);
    json_object_set_string_member(bindings, "accept", tl_string(shortcut_configuration, "accept"));
    json_object_set_string_member(bindings, "exit", tl_string(shortcut_configuration, "exit"));
    GtkWidget *top = target && !target->dead ? GTK_WIDGET(gtk_widget_get_root(target->widget)) : NULL;
    gboolean source = initialized && target && !target->dead && GTK_IS_WINDOW(top)
        && !tl_overlay_auxiliary_active(target)
        && tl_controller_attached(top, "typelay-gtk4-controller") && boolean(shortcut_configuration, "sessionEnabled")
        && (!control_pair() || tl_prefix_supported(target));
    json_object_set_boolean_member(bindings, "sourceReady", source);
    /* Read-only rendering can create the editor before its separate capability
       is confirmed. Never infer an installed editor from a source hook/hello. */
    json_object_set_boolean_member(bindings, "editorReady", source && tl_overlay_bindings_ready(target));
    return bindings;
}
gboolean tl_view_context_matches(Target *target, JsonObject *view) {
    JsonObject *context = tl_object(view, "viewContext"), *token = tl_object(context, "token");
    JsonObject *source = tl_object(token, "target"), *bindings = tl_object(context, "bindings");
    return target && !target->dead && tl_session_keyval(TRUE)
        && tl_configuration_current(tl_string(token, "commandGeneration"))
        && g_strcmp0(tl_string(source, "backend"), "linux-gtk4") == 0
        && g_strcmp0(tl_string(source, "value"), target->id) == 0
        && g_strcmp0(tl_string(source, "lifetime"), lifetime) == 0
        && g_strcmp0(tl_string(bindings, "accept"), tl_string(shortcut_configuration, "accept")) == 0
        && g_strcmp0(tl_string(bindings, "exit"), tl_string(shortcut_configuration, "exit")) == 0;
}
JsonObject *tl_copy(JsonObject *object) {
    JsonObject *copy = json_object_new();
    GList *members = json_object_get_members(object);
    for (GList *item = members; item; item = item->next)
        json_object_set_member(copy, item->data, json_node_copy(json_object_get_member(object, item->data)));
    g_list_free(members);
    return copy;
}
gint64 tl_now(void) {
    struct timespec value;
    if (clock_gettime(CLOCK_BOOTTIME, &value) != 0) return G_MAXINT64;
    return (gint64)value.tv_sec * 1000 + value.tv_nsec / 1000000;
}
/* Numeric, bounded pre-Tab history only. Elapsed spans are wall time, not
 * CPU time; parent sequence/main-loop depth expose nested GTK dispatch. */
typedef enum {
    ACCEPTANCE_READ_INPUT = 1, ACCEPTANCE_EXPIRED = 2, ACCEPTANCE_OVERLAY = 3,
    ACCEPTANCE_DESCRIBE = 4, ACCEPTANCE_CAPTURE = 5, ACCEPTANCE_DIRECT = 6,
    ACCEPTANCE_RETURN = 7, ACCEPTANCE_QUARANTINE = 8, ACCEPTANCE_CONFIGURE = 9,
    ACCEPTANCE_TRIGGER = 10, ACCEPTANCE_PUBLISH_SHORTCUT = 11, ACCEPTANCE_REVOKE = 12,
    ACCEPTANCE_PUBLISH = 13, ACCEPTANCE_UNKNOWN = 14,
    ACCEPTANCE_TIMER_CANCEL = 15, ACCEPTANCE_TIMER_SET = 16
} AcceptanceOperation;
typedef struct {
    guint64 sequence, parent;
    AcceptanceOperation operation;
    gint64 started_at, finished_at, expires_before, expires_after;
    gint depth_before, depth_after;
    guint timer_before, timer_after, timer_marks; /* expired=1, cancelled=2, set=4 */
} AcceptanceSpan;
typedef struct { guint64 sequence, parent; } AcceptanceSpanScope;
typedef struct {
    AcceptanceSpan spans[32];
    guint64 sequence, active, truncated;
    guint count;
    gint depth;
} AcceptanceDelivery;
static AcceptanceDelivery acceptance_delivery;
static AcceptanceSpanScope acceptance_span_begin(AcceptanceOperation operation) {
    if (!g_getenv("TYPELAY_TRACE")) return (AcceptanceSpanScope){0};
    AcceptanceSpanScope scope = {.sequence = ++acceptance_delivery.sequence,
        .parent = acceptance_delivery.active};
    AcceptanceSpan *span = &acceptance_delivery.spans[(scope.sequence - 1)
        % G_N_ELEMENTS(acceptance_delivery.spans)];
    *span = (AcceptanceSpan){.sequence = scope.sequence, .parent = scope.parent,
        .operation = operation, .started_at = tl_now(), .depth_before = g_main_depth(),
        .timer_before = publication_timer,
        .expires_before = tl_integer(tl_object(publication, "permit"), "expiresAtMs", 0),
        .timer_marks = operation == ACCEPTANCE_EXPIRED ? 1u
            : operation == ACCEPTANCE_TIMER_CANCEL ? 2u : operation == ACCEPTANCE_TIMER_SET ? 4u : 0u};
    acceptance_delivery.active = scope.sequence;
    if (acceptance_delivery.count < G_N_ELEMENTS(acceptance_delivery.spans))
        acceptance_delivery.count++;
    else acceptance_delivery.truncated++;
    return scope;
}
static void acceptance_span_end(AcceptanceSpanScope scope) {
    if (!scope.sequence) return;
    AcceptanceSpan *span = &acceptance_delivery.spans[(scope.sequence - 1)
        % G_N_ELEMENTS(acceptance_delivery.spans)];
    /* A deeply nested callback may have displaced this slot. Never overwrite
       the newer record, and always restore the enclosing active sequence. */
    if (span->sequence == scope.sequence) {
        span->finished_at = tl_now();
        span->depth_after = g_main_depth();
        span->timer_after = publication_timer;
        span->expires_after = tl_integer(tl_object(publication, "permit"), "expiresAtMs", 0);
    }
    acceptance_delivery.active = scope.parent;
}
static AcceptanceOperation acceptance_command_operation(JsonObject *frame) {
    const char *operation = tl_string(frame, "op");
    if (!operation) return ACCEPTANCE_UNKNOWN;
    if (g_str_has_prefix(operation, "overlay-")) return ACCEPTANCE_OVERLAY;
    static const char *const names[] = {"describe", "capture", "direct", "return", "quarantine",
        "configure-shortcuts", "trigger-shortcut", "publish-shortcut", "revoke", "publish"};
    for (guint index = 0; index < G_N_ELEMENTS(names); index++)
        if (strcmp(operation, names[index]) == 0)
            return (AcceptanceOperation)(ACCEPTANCE_DESCRIBE + index);
    return ACCEPTANCE_UNKNOWN;
}
static void trace_publication(const char *stage) {
    static guint count;
    if (!g_getenv("TYPELAY_TRACE") || count >= 512) return;
    count++;
    JsonObject *permit = tl_object(publication, "permit");
    gint64 now = tl_now(), until = MAX((gint64)0, tl_integer(permit, "expiresAtMs", 0));
    g_printerr("TypeLay GTK4 publication stage=%s atMs=%" G_GINT64_FORMAT
        " present=%d remainingMs=%" G_GINT64_FORMAT "\n", stage, now,
        publication != NULL, permit ? until - now : -1);
}
void tl_drop_publication(void) {
    if (publication) trace_publication("clear");
    if (publication_timer) {
        AcceptanceSpanScope span = acceptance_span_begin(ACCEPTANCE_TIMER_CANCEL);
        g_source_remove(publication_timer); publication_timer = 0;
        acceptance_span_end(span);
    }
    if (publication) { json_object_unref(publication); publication = NULL; }
    tl_overlay_acceptance();
}
static OrderedTicket *ordered_ref(OrderedTicket *ticket) {
    if (ticket) ticket->refs++;
    return ticket;
}
static void ordered_unref(OrderedTicket *ticket) {
    if (ticket && --ticket->refs == 0) {
        json_object_unref(ticket->publication);
        g_free(ticket);
    }
}
static void ordered_revoke(void) {
    OrderedTicket *ticket = ordered;
    if (!ticket) return;
    ordered = NULL; /* Reentrant cancellation cannot restore this ticket. */
    ticket->revoked = TRUE;
    tl_prefix_cancel(ticket->target, ticket->context, ticket->id);
    ordered_unref(ticket);
}
void tl_clear_permit(void) {
    tl_overlay_control_cancel();
    ordered_revoke();
    tl_drop_publication();
}
static void clear_shortcut(void) {
    if (shortcut_authority) { json_object_unref(shortcut_authority); shortcut_authority = NULL; }
}
static void disconnect_peer(void) {
    connection_generation++;
    configuration_sequence++;
    configuring = FALSE;
    if (connection >= 0) {
        trace_connection("disconnected", 0);
        close(connection); connection = -1;
    }
    tl_overlay_hide();
    tl_clear_permit();
    clear_shortcut();
    if (shortcut_configuration) { json_object_unref(shortcut_configuration); shortcut_configuration = NULL; }
    if (input_watch) { g_source_remove(input_watch); input_watch = 0; }
    if (output_watch) { g_source_remove(output_watch); output_watch = 0; }
    if (incoming) g_byte_array_set_size(incoming, 0);
    if (outgoing) g_byte_array_set_size(outgoing, 0);
}
static gboolean flush_output(GIOChannel *channel, GIOCondition condition, gpointer data) {
    (void)channel; (void)data;
    if (condition & (G_IO_HUP | G_IO_ERR | G_IO_NVAL)) {
        output_watch = 0; disconnect_peer(); return G_SOURCE_REMOVE;
    }
    while (connection >= 0 && outgoing->len) {
        ssize_t count = send(connection, outgoing->data, outgoing->len, MSG_NOSIGNAL);
        if (count > 0) g_byte_array_remove_range(outgoing, 0, (guint)count);
        else if (count < 0 && errno == EINTR) continue;
        else if (count < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) return G_SOURCE_CONTINUE;
        else { output_watch = 0; disconnect_peer(); return G_SOURCE_REMOVE; }
    }
    output_watch = 0;
    return G_SOURCE_REMOVE;
}
static void send_frame(JsonObject *object) {
    if (connection < 0) { json_object_unref(object); return; }
    JsonNode *root = json_node_new(JSON_NODE_OBJECT);
    json_node_take_object(root, object);
    char *encoded = json_to_string(root, FALSE);
    gsize size = strlen(encoded);
    if (size == 0 || size > TL_MAX_FRAME || outgoing->len + size + 4 > 4 * TL_MAX_FRAME) {
        g_free(encoded); json_node_free(root); disconnect_peer(); return;
    }
    guint32 length = GUINT32_TO_LE((guint32)size);
    g_byte_array_append(outgoing, (guint8 *)&length, 4);
    g_byte_array_append(outgoing, (guint8 *)encoded, (guint)size);
    g_free(encoded); json_node_free(root);
    if (!output_watch) {
        GIOChannel *channel = g_io_channel_unix_new(connection);
        output_watch = g_io_add_watch(channel, G_IO_OUT | G_IO_HUP | G_IO_ERR | G_IO_NVAL, flush_output, NULL);
        g_io_channel_unref(channel);
    }
}
void tl_send_event(const char *event, JsonObject *data) {
    JsonObject *frame = json_object_new();
    json_object_set_string_member(frame, "event", event);
    json_object_set_object_member(frame, "data", data);
    send_frame(frame);
}
void tl_invalidate(Target *target, gboolean text_changed, gboolean focus_changed) {
    if (target->dead) return;
    /* Focus has its own revision. Entering/returning from our editor must not
       pretend that the source text or selection was edited. */
    if (!focus_changed) target->input_revision++;
    if (text_changed) target->content_revision++;
    if (focus_changed) {
        target->focus_revision++;
        clear_shortcut();
        /* A focus transition is not a physical key release. A held Tab may
           navigate to another field; its auto-repeat cannot accept there. */
    }
    tl_clear_permit();
    /* Input dispatched by an application's nested loop must still invalidate
       an in-progress transaction, even though its own storage signals are quiet. */
    if (target->mutating) return;
    JsonObject *event = json_object_new();
    json_object_set_string_member(event, "target", target->id);
    json_object_set_boolean_member(event, "textChanged", text_changed);
    json_object_set_boolean_member(event, "focusChanged", focus_changed);
    json_object_set_boolean_member(event, "focused", tl_focused() == target);
    json_object_set_int_member(event, "inputRevision", (gint64)target->input_revision);
    json_object_set_int_member(event, "contentRevision", (gint64)target->content_revision);
    json_object_set_int_member(event, "focusRevision", (gint64)target->focus_revision);
    tl_send_event("input", event);
}
void tl_forget(Target *target) {
    tl_overlay_forget(target);
    tl_clear_permit();
    clear_shortcut();
    JsonObject *event = json_object_new();
    json_object_set_string_member(event, "target", target->id);
    tl_send_event("destroy", event);
}
static void respond(gint64 id, JsonObject *data) {
    if (id <= 0) { if (data) json_object_unref(data); return; }
    JsonObject *frame = json_object_new();
    json_object_set_int_member(frame, "id", id);
    if (data) json_object_set_object_member(frame, "data", data);
    else json_object_set_null_member(frame, "data");
    send_frame(frame);
}
typedef struct {
    Target *target;
    JsonObject *basis;
    gint64 id, until;
    guint64 connection, generation;
} PendingReturn;
static void free_return(gpointer data) {
    PendingReturn *pending = data;
    json_object_unref(pending->basis);
    g_free(pending);
}
static gboolean await_return(gpointer data) {
    PendingReturn *pending = data;
    if (connection < 0 || pending->connection != connection_generation) return G_SOURCE_REMOVE;
    gboolean same = pending->generation == return_generation && !pending->target->dead
        && tl_focused() == pending->target && tl_metadata_matches(pending->target, pending->basis)
        && (gint64)pending->target->content_revision == tl_integer(pending->basis, "contentRevision", -1)
        && (gint64)pending->target->input_revision == tl_integer(pending->basis, "inputRevision", -1);
    gboolean ready = same && tl_now() <= pending->until && tl_return_ready(pending->target, pending->basis);
    if (!ready && same && tl_now() < pending->until) return G_SOURCE_CONTINUE;
    JsonObject *reply = json_object_new();
    json_object_set_boolean_member(reply, "returned", ready);
    respond(pending->id, reply);
    return G_SOURCE_REMOVE;
}
/* Call only after the command/key path's IME observation. This check reads
   metadata and revisions, never source text or a second provider RPC. */
static const char *publication_failure(Target *target, JsonObject *current) {
    if (!current || publication != current) return "not-published";
    if (!tl_configuration_current(tl_string(current, "generation"))
        || !tl_session_matches(tl_object(tl_object(current, "authority"), "sessionBindings"))) return "generation";
    if (!target || target->dead || target != tl_focused()) return "focus";
    if (target->quarantined || target->mutating || target->im_probing) return "unavailable";
    if (!target->ime_known || target->composing) return "ime";
    JsonObject *basis = tl_object(current, "authority"), *permit = tl_object(current, "permit");
    JsonObject *plan = tl_object(permit, "plan"), *expected = tl_object(plan, "expected");
    const char *id = tl_string(plan, "id");
    if (!id || !*id || strlen(id) > 256) return "no-permit";
    if (already_consumed(id)) return "consumed";
    if (g_strcmp0(target->id, tl_string(current, "target")) != 0
        || g_strcmp0(target->id, tl_string(basis, "target")) != 0
        || g_strcmp0(target->id, tl_string(tl_object(expected, "target"), "value")) != 0)
        return "target";
    if ((gint64)target->content_revision != tl_integer(basis, "contentRevision", -1)
        || (gint64)target->input_revision != tl_integer(basis, "inputRevision", -1)
        || (gint64)target->focus_revision != tl_integer(basis, "focusRevision", -1)) return "revision";
    JsonObject *stamp = tl_object(basis, "contextStamp");
    if (!stamp || !target->im_observed
        || (gint64)target->im_state.context_generation != tl_integer(stamp, "context", -1)
        || (gint64)target->im_state.epoch != tl_integer(stamp, "epoch", -1)
        || (gint64)target->im_state.input_epoch != tl_integer(stamp, "input", -1)
        || (gint64)target->im_state.server_serial != tl_integer(stamp, "server", -1)
        || (gint64)target->im_state.applied_serial != tl_integer(stamp, "applied", -1)
        || (gint64)target->im_state.flags != tl_integer(stamp, "flags", -1)) return "ime-stamp";
    if (!tl_metadata_matches(target, basis)) return "metadata";
    /* The provider call may have used most or all of the original deadline. */
    gint64 now = tl_now(), until = tl_integer(permit, "expiresAtMs", 0);
    if (now >= until || until - now > 100) return "stale";
    return publication == current ? NULL : "not-published";
}
static gboolean ordered_live(OrderedTicket *ticket, Target *target) {
    return ticket && ordered == ticket && !ticket->revoked && target == ticket->target
        && target && !target->dead && !target->quarantined && target == tl_focused()
        && target->widget == ticket->widget && target->client == ticket->client && target->buffer == ticket->buffer
        && tl_controller_attached(GTK_WIDGET(gtk_widget_get_root(target->widget)), "typelay-gtk4-controller")
        && target->im_context == ticket->context && connection >= 0
        && connection_generation == ticket->connection && configuration_sequence == ticket->configuration
        && tl_configuration_current(tl_string(ticket->publication, "generation"))
        && tl_session_matches(tl_object(tl_object(ticket->publication, "authority"), "sessionBindings"));
}
gboolean tl_ordered_check(OrderedTicket *ticket, Target *target, gboolean source, gboolean proof) {
    if (!ordered_live(ticket, target)) return FALSE;
    if (source && (target->content_revision != ticket->content || target->input_revision != ticket->input
        || target->focus_revision != ticket->focus || target->storage_revision != ticket->storage
        || !tl_metadata_matches(target, tl_object(ticket->publication, "authority")))) return FALSE;
    if (proof && (!ticket->finished || !tl_prefix_validate(target, ticket->id,
        &ticket->before, &target->im_state))) return FALSE;
    return ordered_live(ticket, target);
}
guint64 tl_ordered_pending_id(Target *target) {
    return ordered && ordered->target == target && !ordered->finished ? ordered->id : 0;
}
gboolean tl_ordered_finish(Target *target, guint64 id, TlFcitxStateV1 *fresh) {
    OrderedTicket *ticket = ordered_ref(ordered);
    gboolean ready = ticket && ticket->id == id && !ticket->finished && !ticket->finishing
        && tl_ordered_check(ticket, target, TRUE, FALSE);
    if (ready) {
        ticket->finishing = TRUE;
        ready = tl_prefix_finish(target, id, fresh)
            && tl_ordered_check(ticket, target, TRUE, FALSE);
        if (ready) {
            ticket->finished = TRUE;
            ready = tl_prefix_validate(target, id, &ticket->before, fresh)
                && tl_ordered_check(ticket, target, TRUE, FALSE);
        }
        ticket->finishing = FALSE;
    }
    if (!ready) {
        if (target) target->ime_known = FALSE;
        if (ordered == ticket) tl_clear_permit();
    }
    ordered_unref(ticket);
    return ready;
}
gboolean tl_ordered_transition(Target *target) {
    return ordered_live(ordered, target) && tl_prefix_transition(target, ordered->id);
}
gboolean tl_ordered_observation(Target *target, const TlFcitxStateV1 *current) {
    OrderedTicket *ticket = ordered_ref(ordered);
    gboolean valid = tl_ordered_check(ticket, target, TRUE, FALSE) && ticket->finished
        && tl_prefix_validate(target, ticket->id, &ticket->before, current)
        && ordered_live(ticket, target);
    ordered_unref(ticket);
    return valid;
}
gboolean tl_ordered_prepare(OrderedTicket *ticket, Target *target, const TlFcitxStateV1 *state) {
    return tl_ordered_check(ticket, target, TRUE, TRUE) && tl_now() < ticket->accept_until
        && tl_prefix_prepare(target, ticket->id, state) && ordered_live(ticket, target);
}
gboolean tl_ordered_claim(OrderedTicket *ticket, Target *target) {
    if (!tl_ordered_check(ticket, target, TRUE, TRUE) || ticket->claimed || !ticket->deciding
        || tl_now() >= (ticket->claim_keycode ? ticket->accept_until : 0)) return FALSE;
    for (guint index = 0; index < G_N_ELEMENTS(held_keys); index++) {
        if (held_keys[index].keycode == ticket->claim_keycode
            && held_keys[index].sequence == ticket->claim_sequence && !held_keys[index].consumed) {
            ticket->claimed = held_keys[index].consumed = TRUE;
            return TRUE;
        }
    }
    return FALSE;
}
static gboolean original_source_matches(Target *target, JsonObject *current) {
    JsonObject *basis = tl_object(current, "authority"), *stamp = tl_object(basis, "contextStamp");
    return target && !target->dead && !target->mutating && !target->quarantined
        && target == tl_focused() && target->ime_known && !target->composing && stamp
        && tl_session_matches(tl_object(basis, "sessionBindings"))
        && tl_configuration_current(tl_string(current, "generation"))
        && g_strcmp0(target->id, tl_string(current, "target")) == 0
        && g_strcmp0(target->id, tl_string(basis, "target")) == 0
        && (gint64)target->content_revision == tl_integer(basis, "contentRevision", -1)
        && (gint64)target->input_revision == tl_integer(basis, "inputRevision", -1)
        && (gint64)target->focus_revision == tl_integer(basis, "focusRevision", -1)
        && (gint64)target->im_state.context_generation == tl_integer(stamp, "context", -1)
        && (gint64)target->im_state.epoch == tl_integer(stamp, "epoch", -1)
        && (gint64)target->im_state.input_epoch == tl_integer(stamp, "input", -1)
        && (gint64)target->im_state.server_serial == tl_integer(stamp, "server", -1)
        && (gint64)target->im_state.applied_serial == tl_integer(stamp, "applied", -1)
        && (gint64)target->im_state.flags == tl_integer(stamp, "flags", -1)
        && tl_metadata_matches(target, basis);
}
static gint64 exit_deadline(JsonObject *current) {
    JsonObject *lease = tl_object(current, "lease");
    gint64 observed = tl_integer(tl_object(lease, "source"), "observedAtMs", -1), now = tl_now();
    return boolean(lease, "escapeAllowed") && observed >= 0 && observed <= now && now - observed <= 150
        && tl_integer(lease, "sessionRevision", -1) >= 0
        && g_strcmp0(tl_string(lease, "commandGeneration"), tl_string(current, "generation")) == 0
        ? observed + 150 : 0;
}
static void ordered_begin(GdkEvent *event) {
    if (ordered || !control_pair() || !tl_session_keyval(TRUE) || held_overflow
        || ordered_sequence == G_MAXUINT64 || g_main_depth() != 1) return;
    Target *target = tl_focused();
    JsonObject *current = publication ? json_object_ref(publication) : NULL;
    if (!current) return;
    if (!tl_prefix_supported(target) || !tl_ime_clear(target) || publication != current
        || !original_source_matches(target, current) || publication != current) {
        json_object_unref(current); return;
    }
    gint64 accept = control_binding(TRUE) && !publication_failure(target, current)
        ? tl_integer(tl_object(current, "permit"), "expiresAtMs", 0) : 0;
    gint64 exit = control_binding(FALSE) ? exit_deadline(current) : 0;
    if (!accept && !exit) { json_object_unref(current); return; }
    OrderedTicket *ticket = g_new0(OrderedTicket, 1);
    *ticket = (OrderedTicket){.refs = 1, .id = ++ordered_sequence,
        .connection = connection_generation, .configuration = configuration_sequence,
        .content = target->content_revision, .input = target->input_revision,
        .focus = target->focus_revision, .storage = target->storage_revision,
        .target = target, .widget = target->widget, .client = target->client, .buffer = target->buffer,
        .context = target->im_context, .before = target->im_state, .publication = current,
        .keycode = gdk_key_event_get_keycode(event), .time = gdk_event_get_time(event),
        .accept_until = accept, .exit_until = exit};
    ordered = ticket; /* Reserve before invoking any provider/application code. */
    ordered_ref(ticket);
    if (!tl_prefix_begin(target, ticket->id, event) || !ordered_live(ticket, target)) {
        if (ordered == ticket) tl_clear_permit();
    }
    ordered_unref(ticket);
}
static void normalize_ordered_publication(JsonObject *value) {
    JsonObject *basis = tl_object(value, "authority"), *stamp = tl_object(basis, "contextStamp");
    JsonObject *permit = tl_object(value, "permit"), *plan = tl_object(permit, "plan");
    JsonObject *expected = tl_object(plan, "expected"), *source = tl_object(tl_object(value, "lease"), "source");
    if (basis) json_object_remove_member(basis, "observedAtMs");
    if (stamp) json_object_remove_member(stamp, "epoch");
    if (permit) { json_object_remove_member(permit, "observedAtMs"); json_object_remove_member(permit, "expiresAtMs"); }
    if (plan) json_object_remove_member(plan, "id");
    if (expected) json_object_remove_member(expected, "observedAtMs");
    if (source) json_object_remove_member(source, "observedAtMs");
}
static gboolean same_ordered_publication(JsonObject *next) {
    if (!ordered || ordered->revoked || !next) return FALSE;
    JsonNode *old = json_node_new(JSON_NODE_OBJECT), *next_node = json_node_new(JSON_NODE_OBJECT);
    json_node_take_object(old, tl_copy(ordered->publication));
    json_node_take_object(next_node, tl_copy(next));
    normalize_ordered_publication(json_node_get_object(old));
    normalize_ordered_publication(json_node_get_object(next_node));
    gboolean same = json_node_equal(old, next_node);
    json_node_free(old); json_node_free(next_node);
    return same; /* Retention only, never a new grant or a replaced source stamp. */
}
gboolean tl_acceptance_ready(Target *target) {
    JsonObject *current = publication ? json_object_ref(publication) : NULL;
    gboolean ready = publication_failure(target, current) == NULL;
    if (current) json_object_unref(current);
    return ready;
}
static void refresh_acceptance(void);
static gboolean acceptance_expired(gpointer data) {
    (void)data;
    AcceptanceSpanScope span = acceptance_span_begin(ACCEPTANCE_EXPIRED);
    publication_timer = 0;
    /* Keep the independently timed Escape lease; only Tab readiness expires. */
    refresh_acceptance();
    acceptance_span_end(span);
    return G_SOURCE_REMOVE;
}
static void refresh_acceptance(void) {
    if (publication_timer) {
        AcceptanceSpanScope span = acceptance_span_begin(ACCEPTANCE_TIMER_CANCEL);
        g_source_remove(publication_timer); publication_timer = 0;
        acceptance_span_end(span);
    }
    tl_overlay_acceptance();
    gint64 now = tl_now(), until = tl_integer(tl_object(publication, "permit"), "expiresAtMs", 0);
    if (until > now && until - now <= 100) {
        AcceptanceSpanScope span = acceptance_span_begin(ACCEPTANCE_TIMER_SET);
        publication_timer = g_timeout_add((guint)(until - now), acceptance_expired, NULL);
        acceptance_span_end(span);
    }
}
static JsonObject *publication_state(Target *target) {
    JsonObject *state = json_object_new();
    JsonObject *current = publication ? json_object_ref(publication) : NULL;
    gboolean ready = publication_failure(target, current) == NULL;
    json_object_set_boolean_member(state, "ready", ready);
    if (ready) {
        JsonObject *permit = tl_object(current, "permit");
        json_object_set_string_member(state, "planId", tl_string(tl_object(permit, "plan"), "id"));
        json_object_set_int_member(state, "readyUntilHostMs", tl_integer(permit, "expiresAtMs", 0));
    } else {
        json_object_set_null_member(state, "planId");
        json_object_set_null_member(state, "readyUntilHostMs");
    }
    if (current) json_object_unref(current);
    return state;
}
static void trace_tab(const char *reason, Target *target, JsonObject *current, JsonObject *reply) {
    static guint count;
    if (!g_getenv("TYPELAY_TRACE") || count >= 96) return;
    count++;
    JsonObject *permit = tl_object(current, "permit"), *basis = tl_object(current, "authority");
    const char *id = tl_string(tl_object(permit, "plan"), "id");
    const char *receipt_reason = tl_string(reply, "reason"), *safe_receipt = "none";
    const char *reasons[] = {"BadPlan", "ImeUnclear", "StaleObservation", "Sensitive", "EpochChanged",
        "SourceChanged", "SelectionChanged", "InvalidText"};
    for (guint index = 0; index < G_N_ELEMENTS(reasons); index++)
        if (g_strcmp0(receipt_reason, reasons[index]) == 0) safe_receipt = reasons[index];
    gint64 now = tl_now(), until = MAX((gint64)0, tl_integer(permit, "expiresAtMs", 0));
    gint64 observed = tl_integer(basis, "observedAtMs", -1);
    /* Only opaque UUIDs and fixed reasons are logged, never arbitrary strings
       from the plan, field metadata, or the receipt. */
    g_printerr("TypeLay GTK4 Tab check=%s atMs=%" G_GINT64_FORMAT " planId=%s remainingMs=%" G_GINT64_FORMAT
        " authorityAgeMs=%" G_GINT64_FORMAT " inputRevision=%" G_GUINT64_FORMAT
        " expectedInputRevision=%" G_GINT64_FORMAT " receiptReason=%s\n", reason, now,
        id && g_uuid_string_is_valid(id) ? id : id ? "invalid" : "none", permit ? until - now : -1,
        observed >= 0 ? now - observed : -1, target ? target->input_revision : 0,
        tl_integer(basis, "inputRevision", -1), safe_receipt);
}
typedef struct {
    gint64 entered_at, inspect_started_at, inspect_finished_at, gate_at;
    gboolean inspected;
    guint publication_count;
} AcceptanceTiming;
typedef struct {
    AcceptanceTiming timing;
    char plan_id[37];
    gint64 finished_at, expires_at;
    gboolean allowed;
} AcceptancePhase;
static AcceptancePhase acceptance_publications[192];
static guint acceptance_published_count;
static void print_acceptance_phase(const char *operation, const AcceptancePhase *phase) {
    const AcceptanceTiming *timing = &phase->timing;
    g_printerr("TypeLay GTK%d acceptance-timing operation=%s planId=%s"
        " enteredAtMs=%" G_GINT64_FORMAT " inspectStartedAtMs=%" G_GINT64_FORMAT
        " inspectFinishedAtMs=%" G_GINT64_FORMAT " gateAtMs=%" G_GINT64_FORMAT
        " finishedAtMs=%" G_GINT64_FORMAT " expiresAtMs=%" G_GINT64_FORMAT
        " inspected=%d gateAllowed=%d\n", GTK_MAJOR_VERSION, operation, phase->plan_id,
        timing->entered_at, timing->inspect_started_at, timing->inspect_finished_at,
        timing->gate_at, phase->finished_at, phase->expires_at, timing->inspected, phase->allowed);
}
static void print_acceptance_delivery(gint64 key_entered_at, const AcceptanceDelivery *delivery) {
    if (!delivery) return;
    char output[16384];
    const gsize limit = sizeof(output) - 256; /* Reserve the explicit truncation footer. */
    gint written = g_snprintf(output, limit,
        "TypeLay GTK%d acceptance-timing operation=delivery-window keyEnteredAtMs=%" G_GINT64_FORMAT
        " activeSequence=%" G_GUINT64_FORMAT " mainDepth=%d spanCount=%u"
        " truncated=%" G_GUINT64_FORMAT "\n", GTK_MAJOR_VERSION, key_entered_at,
        delivery->active, delivery->depth, delivery->count, delivery->truncated);
    if (written < 0 || (gsize)written >= limit) return;
    gsize used = (gsize)written;
    guint emitted = 0;
    for (guint index = 0; index < delivery->count; index++) {
        guint64 sequence = delivery->sequence - delivery->count + index + 1;
        const AcceptanceSpan *span = &delivery->spans[(sequence - 1) % G_N_ELEMENTS(delivery->spans)];
        written = g_snprintf(output + used, limit - used,
            "TypeLay GTK%d acceptance-timing operation=delivery-span keyEnteredAtMs=%" G_GINT64_FORMAT
            " sequence=%" G_GUINT64_FORMAT " parentSequence=%" G_GUINT64_FORMAT
            " spanOperation=%d startedAtMs=%" G_GINT64_FORMAT " finishedAtMs=%" G_GINT64_FORMAT
            " depthBefore=%d depthAfter=%d timerBefore=%u timerAfter=%u timerMarks=%u"
            " expiresBeforeMs=%" G_GINT64_FORMAT " expiresAfterMs=%" G_GINT64_FORMAT "\n",
            GTK_MAJOR_VERSION, key_entered_at, span->sequence, span->parent, (gint)span->operation,
            span->started_at, span->finished_at, span->depth_before, span->depth_after,
            span->timer_before, span->timer_after, span->timer_marks,
            span->expires_before, span->expires_after);
        if (written < 0 || (gsize)written >= limit - used) break;
        used += (gsize)written;
        emitted++;
    }
    g_snprintf(output + used, sizeof(output) - used,
        "TypeLay GTK%d acceptance-timing operation=delivery-end keyEnteredAtMs=%" G_GINT64_FORMAT " omittedSpans=%u\n",
        GTK_MAJOR_VERSION, key_entered_at, delivery->count - emitted);
    /* One bounded write at the existing key-timing point, never per callback. */
    g_printerr("%s", output);
}
static void trace_acceptance_timing(const char *operation, JsonObject *request,
                                   const AcceptanceTiming *timing, gboolean allowed,
                                   const AcceptanceDelivery *delivery) {
    static guint key_count;
    JsonObject *permit = tl_object(request, "permit");
    const char *id = tl_string(tl_object(permit, "plan"), "id");
    if (!g_getenv("TYPELAY_TRACE") || !id || !g_uuid_string_is_valid(id)) return;
    AcceptancePhase phase = {.timing = *timing, .finished_at = tl_now(),
        .expires_at = tl_integer(permit, "expiresAtMs", 0), .allowed = allowed};
    g_strlcpy(phase.plan_id, id, sizeof(phase.plan_id));
    /* Publish completion means response queued, not received by the host.
       Keep only bounded owned values here: no diagnostic stderr write while
       the published permit is live, and no retained JSON/widget references. */
    if (strcmp(operation, "publish") == 0) {
        if (acceptance_published_count < G_N_ELEMENTS(acceptance_publications))
            acceptance_publications[acceptance_published_count++] = phase;
        return;
    }
    if (key_count >= 192) return;
    key_count++;
    /* Emit only after this key's refusal or original write receipt was queued.
       Snapshot the cache boundary before that probe: even a same-millisecond
       reentrant publication cannot stand in for the original plan. Missing
       history remains missing rather than fabricated. */
    for (guint index = timing->publication_count; index > 0; index--) {
        const AcceptancePhase *published = &acceptance_publications[index - 1];
        if (strcmp(published->plan_id, id) == 0 && published->expires_at == phase.expires_at
            && published->finished_at <= timing->inspect_started_at) {
            print_acceptance_phase("publish", published);
            break;
        }
    }
    print_acceptance_phase("key", &phase);
    /* Refusal may not have cleared publication yet, or a write callback may
       have replaced it. Never add history I/O while any current permit is live. */
    if (delivery && tl_integer(tl_object(publication, "permit"), "expiresAtMs", 0) <= tl_now())
        print_acceptance_delivery(timing->entered_at, delivery);
}
static void command(JsonObject *frame) {
    const char *operation = tl_string(frame, "op");
    gint64 id = tl_integer(frame, "id", 0);
    JsonObject *data = tl_object(frame, "data");
    if (!operation) { disconnect_peer(); return; }
    const char *target_id = tl_string(data, "target");
    Target *target = target_id ? tl_lookup(target_id) : tl_focused();
    if (g_str_has_prefix(operation, "overlay-")) respond(id, tl_overlay_command(operation, target, data));
    else if (strcmp(operation, "describe") == 0) {
        JsonObject *description = tl_describe(target);
        if (description) json_object_set_object_member(description, "publication", publication_state(target));
        respond(id, description);
    } else if (strcmp(operation, "capture") == 0)
        respond(id, tl_capture(target, tl_object(data, "authority")));
    else if (strcmp(operation, "direct") == 0) {
        tl_overlay_control_cancel();
        respond(id, tl_write(target, data, TRUE));
    } else if (strcmp(operation, "return") == 0) {
        return_generation++;
        JsonObject *basis = tl_object(data, "authority");
        gboolean focused = tl_configuration_current(tl_string(data, "generation"))
            && tl_session_matches(tl_object(basis, "sessionBindings")) && tl_return(target, basis);
        gboolean ready = focused && tl_return_ready(target, basis);
        if (focused && !ready) {
            PendingReturn *pending = g_new0(PendingReturn, 1);
            *pending = (PendingReturn){.target = target, .basis = tl_copy(basis), .id = id,
                .until = tl_now() + 450, .connection = connection_generation, .generation = return_generation};
            /* Poll a real bounded provider observation, never infer Clear from
               elapsed time. Leave room inside the host's 600 ms RPC deadline. */
            g_timeout_add_full(G_PRIORITY_DEFAULT, 10, await_return, pending, free_return);
            return;
        }
        JsonObject *reply = json_object_new();
        json_object_set_boolean_member(reply, "returned", ready);
        respond(id, reply);
    } else if (strcmp(operation, "quarantine") == 0) {
        if (target) target->quarantined = TRUE;
        tl_clear_permit(); clear_shortcut(); respond(id, NULL);
    } else if (strcmp(operation, "configure-shortcuts") == 0) {
        /* Revoke first. No callback may observe a new binding with an old permit.
           Nested GTK callbacks may install a newer request while revoking UI. */
        guint64 sequence = ++configuration_sequence;
        configuring = TRUE;
        tl_clear_permit();
        if (sequence != configuration_sequence) { respond(id, NULL); return; }
        clear_shortcut();
        return_generation++;
        tl_overlay_hide();
        if (sequence != configuration_sequence) { respond(id, NULL); return; }
        JsonObject *next = data ? tl_copy(data) : NULL;
        if (shortcut_configuration) json_object_unref(shortcut_configuration);
        shortcut_configuration = next;
        configuring = FALSE;
        respond(id, NULL);
    } else if (strcmp(operation, "trigger-shortcut") == 0) {
        JsonObject *basis = tl_object(data, "authority");
        gint64 stamp = tl_integer(data, "triggeredAtMs", 0), age = tl_now() - stamp;
        gboolean allowed = age >= 0 && age <= 150 && stamp > shortcut_started_at
            && g_strcmp0(tl_string(data, "generation"), tl_string(shortcut_configuration, "generation")) == 0;
        JsonObject *reply = json_object_new();
        json_object_set_boolean_member(reply, "triggered", allowed
            && trigger_shortcut(tl_string(data, "kind"), target, basis, stamp, TRUE));
        respond(id, reply);
    } else if (strcmp(operation, "publish-shortcut") == 0) {
        clear_shortcut();
        JsonObject *basis = tl_object(data, "authority");
        if (target && target == tl_focused() && tl_metadata_matches(target, basis)
            && g_strcmp0(tl_string(data, "generation"), tl_string(shortcut_configuration, "generation")) == 0)
            shortcut_authority = tl_copy(data);
        respond(id, NULL);
    } else if (strcmp(operation, "revoke") == 0) {
        trace_publication("host-revoke");
        if (tl_integer(data, "shortcuts", 0)) { tl_clear_permit(); clear_shortcut(); }
        else {
            /* Runtime withdraws source writes on every editing Tick. The
               independent editor token authorizes only return/exit, not a write. */
            ordered_revoke(); tl_drop_publication();
        }
        respond(id, NULL);
    } else if (strcmp(operation, "publish") == 0) {
        AcceptanceTiming timing = {.entered_at = tl_now()};
        if (same_ordered_publication(data)) tl_drop_publication();
        else tl_clear_permit();
        JsonObject *basis = tl_object(data, "authority");
        if (target && target == tl_focused() && tl_metadata_matches(target, basis)
            && tl_configuration_current(tl_string(data, "generation"))
            && tl_session_matches(tl_object(basis, "sessionBindings"))
            && g_strcmp0(tl_string(tl_object(data, "lease"), "commandGeneration"), tl_string(data, "generation")) == 0)
            publication = tl_copy(data);
        /* The observation may revoke publication; reacquire it only afterwards. */
        timing.inspect_started_at = tl_now();
        timing.inspected = publication != NULL;
        if (publication) (void)tl_ime_clear(target);
        timing.inspect_finished_at = tl_now();
        refresh_acceptance();
        JsonObject *state = publication_state(target), *reply = json_object_new();
        trace_publication("publish-observed");
        gboolean published = publication
            && (!tl_object(data, "permit") || json_object_get_boolean_member(state, "ready"));
        timing.gate_at = tl_now();
        json_object_set_boolean_member(reply, "published", published);
        if (target) {
            json_object_set_string_member(state, "target", target->id);
            tl_send_event("publication", state);
        } else json_object_unref(state);
        respond(id, reply);
        trace_acceptance_timing("publish", data, &timing, published, NULL);
    } else { disconnect_peer(); }
}
static gboolean read_input_frames(GIOChannel *channel, GIOCondition condition, gpointer data) {
    (void)channel; (void)data;
    if (condition & (G_IO_HUP | G_IO_ERR | G_IO_NVAL)) {
        input_watch = 0; disconnect_peer(); return G_SOURCE_REMOVE;
    }
    guint8 buffer[8192];
    for (;;) {
        ssize_t count = recv(connection, buffer, sizeof buffer, 0);
        if (count > 0) {
            if (incoming->len + (gsize)count > 2 * TL_MAX_FRAME) {
                input_watch = 0; disconnect_peer(); return G_SOURCE_REMOVE;
            }
            g_byte_array_append(incoming, buffer, (guint)count);
        } else if (count < 0 && errno == EINTR) continue;
        else if (count < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) break;
        else { input_watch = 0; disconnect_peer(); return G_SOURCE_REMOVE; }
    }
    while (incoming->len >= 4 && connection >= 0) {
        guint32 length;
        memcpy(&length, incoming->data, 4); length = GUINT32_FROM_LE(length);
        if (!length || length > TL_MAX_FRAME) { input_watch = 0; disconnect_peer(); return G_SOURCE_REMOVE; }
        if (incoming->len < length + 4) break;
        JsonParser *parser = json_parser_new();
        GError *error = NULL;
        gboolean valid = json_parser_load_from_data(parser, (char *)incoming->data + 4, length, &error);
        JsonNode *root = valid ? json_parser_get_root(parser) : NULL;
        /* Copy out before dispatch: callbacks can enqueue frames but cannot retain parser nodes. */
        JsonObject *frame = root && JSON_NODE_HOLDS_OBJECT(root) ? tl_copy(json_node_get_object(root)) : NULL;
        g_clear_error(&error); g_object_unref(parser);
        g_byte_array_remove_range(incoming, 0, length + 4);
        if (!frame) { input_watch = 0; disconnect_peer(); return G_SOURCE_REMOVE; }
        AcceptanceSpanScope span = {0};
        if (g_getenv("TYPELAY_TRACE")) span = acceptance_span_begin(acceptance_command_operation(frame));
        command(frame);
        acceptance_span_end(span);
        json_object_unref(frame);
    }
    return connection >= 0 ? G_SOURCE_CONTINUE : G_SOURCE_REMOVE;
}
static gboolean read_input(GIOChannel *channel, GIOCondition condition, gpointer data) {
    AcceptanceSpanScope span = acceptance_span_begin(ACCEPTANCE_READ_INPUT);
    gboolean result = read_input_frames(channel, condition, data);
    acceptance_span_end(span);
    return result;
}
static gboolean connect_peer(gpointer data) {
    (void)data;
    if (connection >= 0) return G_SOURCE_CONTINUE;
    const char *runtime = g_getenv("XDG_RUNTIME_DIR");
    if (!runtime || !g_path_is_absolute(runtime)) {
        trace_connection("runtime", 0); return G_SOURCE_CONTINUE;
    }
    char *identity = g_strconcat(g_getenv("WAYLAND_DISPLAY") ? g_getenv("WAYLAND_DISPLAY") : "", "\n",
        g_getenv("NIRI_SOCKET") ? g_getenv("NIRI_SOCKET") : "", NULL);
    char *hash = g_compute_checksum_for_string(G_CHECKSUM_SHA256, identity, -1);
    hash[16] = '\0';
    char *path = g_build_filename(runtime, "typelay", hash, "native.sock", NULL);
    g_free(hash); g_free(identity);
    struct stat info;
    struct sockaddr_un address = {.sun_family = AF_UNIX};
    const char *failure = NULL;
    gint error = 0;
    if (strlen(path) >= sizeof address.sun_path) failure = "path-length";
    else if (lstat(path, &info) != 0) { failure = "stat"; error = errno; }
    else if (!S_ISSOCK(info.st_mode)) failure = "socket-type";
    else if (info.st_uid != getuid()) failure = "socket-owner";
    else if ((info.st_mode & 0077) != 0) failure = "socket-mode";
    if (failure) { trace_connection(failure, error); g_free(path); return G_SOURCE_CONTINUE; }
    strcpy(address.sun_path, path); g_free(path);
    gint socket_fd = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
    if (socket_fd < 0) { trace_connection("socket", errno); return G_SOURCE_CONTINUE; }
    /* Unix-domain local connect is immediate or fails; never block the application's UI. */
    if (connect(socket_fd, (struct sockaddr *)&address, sizeof address) != 0) {
        trace_connection("connect", errno); close(socket_fd); return G_SOURCE_CONTINUE;
    }
    struct ucred peer;
    socklen_t length = sizeof peer;
    if (getsockopt(socket_fd, SOL_SOCKET, SO_PEERCRED, &peer, &length) != 0) {
        trace_connection("credentials", errno); close(socket_fd); return G_SOURCE_CONTINUE;
    }
    if (peer.uid != getuid()) { trace_connection("peer-owner", 0); close(socket_fd); return G_SOURCE_CONTINUE; }
    connection = socket_fd;
    trace_connection("connected", 0);
    if (!incoming) incoming = g_byte_array_new();
    if (!outgoing) outgoing = g_byte_array_new();
    GIOChannel *channel = g_io_channel_unix_new(connection);
    input_watch = g_io_add_watch(channel, G_IO_IN | G_IO_HUP | G_IO_ERR | G_IO_NVAL, read_input, NULL);
    g_io_channel_unref(channel);
    JsonObject *hello = json_object_new();
    json_object_set_int_member(hello, "protocol", 2);
    json_object_set_string_member(hello, "backend", "gtk4");
    json_object_set_string_member(hello, "lifetime", lifetime);
    json_object_set_int_member(hello, "pid", getpid());
    tl_send_event("hello", hello);
    return G_SOURCE_CONTINUE;
}
static gboolean already_consumed(const char *id) {
    for (GList *item = consumed_ids.head; item; item = item->next)
        if (g_strcmp0(item->data, id) == 0) return TRUE;
    return FALSE;
}
static void remember(const char *id) {
    g_queue_push_tail(&consumed_ids, g_strdup(id));
    while (g_queue_get_length(&consumed_ids) > 128) g_free(g_queue_pop_head(&consumed_ids));
}
typedef struct {
    JsonObject *configuration, *authority, *approved;
    Target *target;
    GtkWidget *widget; /* identity only; provider calls retain the actual widget */
    const char *kind;
    guint64 id, connection, content, input, focus;
    gint64 triggered_at;
    gboolean require_revisions;
} ShortcutCommand;
static gboolean shortcut_current(const ShortcutCommand *attempt) {
    return connection >= 0 && connection_generation == attempt->connection
        && shortcut_configuration == attempt->configuration && shortcut_sequence == attempt->id;
}
/* Only cached state and metadata are inspected here. Refusal classification
   must never probe the provider again or read a source body. */
static const char *shortcut_failure(const ShortcutCommand *attempt, gboolean inspected, const char **reason) {
    Target *target = attempt->target;
    *reason = "PlatformFailure";
    if (connection < 0 || connection_generation != attempt->connection) return "connection";
    *reason = "EpochChanged";
    if (shortcut_configuration != attempt->configuration) return "configuration";
    if (shortcut_sequence != attempt->id) return "superseded";
    *reason = "Unsupported";
    if (!target) return "target-missing";
    *reason = "TargetChanged";
    if (target->dead || target->widget != attempt->widget) return "target-destroyed";
    *reason = "NotSourceFocus";
    if (target != tl_focused() || target->focus_revision != attempt->focus
        || (attempt->require_revisions
            && (gint64)target->focus_revision != tl_integer(attempt->approved, "focusRevision", -1))) return "focus";
    *reason = "Unsupported";
    if (target->quarantined || target->mutating || target->im_probing) return "unavailable";
    *reason = "ImeUnclear";
    if (inspected && (!target->ime_known || target->composing)) return "ime";
    if (!attempt->authority) {
        *reason = target->ime_known && !target->composing ? "StaleObservation" : "ImeUnclear";
        return "authority-missing";
    }
    *reason = "EpochChanged";
    if (shortcut_authority != attempt->authority) return "authority-revoked";
    if (g_strcmp0(tl_string(attempt->authority, "generation"),
            tl_string(attempt->configuration, "generation")) != 0) return "authority-generation";
    *reason = "TargetChanged";
    if (g_strcmp0(target->id, tl_string(attempt->authority, "target")) != 0) return "authority-target";
    *reason = "SourceChanged";
    if (target->content_revision != attempt->content || (attempt->require_revisions
        && (gint64)target->content_revision != tl_integer(attempt->approved, "contentRevision", -1))) return "source-revision";
    *reason = "EpochChanged";
    if (target->input_revision != attempt->input || (attempt->require_revisions
        && (gint64)target->input_revision != tl_integer(attempt->approved, "inputRevision", -1))) return "input-revision";
    if (inspected) {
        *reason = "StaleObservation";
        gint64 observed = tl_integer(attempt->approved, "observedAtMs", -1), now = tl_now();
        if (observed < 0 || now < observed || now - observed > 150) return "authority-age";
        *reason = "Unsupported";
        if (!tl_metadata_matches(target, attempt->approved)) return "metadata";
    }
    return NULL;
}
static void trace_shortcut(const ShortcutCommand *attempt, const char *phase, const char *gate, const char *reason) {
    static guint emitted;
    if (!g_getenv("TYPELAY_TRACE") || emitted >= 192) return;
    emitted++;
    gint64 now = tl_now(), observed = tl_integer(attempt->approved, "observedAtMs", -1);
    /* Phases, gates and reasons are fixed literals; no body, metadata, target
       identity or caller-supplied generation enters this bounded diagnostic. */
    g_printerr("TypeLay GTK4 shortcut phase=%s gate=%s reason=%s commandId=%" G_GUINT64_FORMAT
        " atMs=%" G_GINT64_FORMAT " authorityAgeMs=%" G_GINT64_FORMAT " elapsedMs=%" G_GINT64_FORMAT "\n",
        phase, gate, reason, attempt->id, now, observed >= 0 ? now - observed : -1, now - attempt->triggered_at);
}
static JsonObject *shortcut_message(const ShortcutCommand *attempt) {
    JsonObject *message = json_object_new();
    json_object_set_string_member(message, "kind", attempt->kind);
    json_object_set_string_member(message, "generation", tl_string(attempt->configuration, "generation"));
    json_object_set_int_member(message, "commandId", (gint64)attempt->id);
    return message;
}
static gboolean trigger_shortcut(const char *kind, Target *target, JsonObject *approved, gint64 triggered_at, gboolean require_revisions) {
    if (!kind || (strcmp(kind, "wake") != 0 && strcmp(kind, "direct") != 0)
        || connection < 0 || !shortcut_configuration || !tl_string(shortcut_configuration, "generation")
        || !json_object_has_member(shortcut_configuration, "enabled")
        || !json_object_get_boolean_member(shortcut_configuration, "enabled")
        || triggered_at < shortcut_started_at || shortcut_sequence >= G_GUINT64_CONSTANT(9007199254740991)) return FALSE;
    ShortcutCommand attempt = {.configuration = json_object_ref(shortcut_configuration),
        .authority = shortcut_authority ? json_object_ref(shortcut_authority) : NULL,
        .target = target, .widget = target ? target->widget : NULL, .kind = kind,
        .id = ++shortcut_sequence, .connection = connection_generation, .triggered_at = triggered_at,
        .content = target ? target->content_revision : 0, .input = target ? target->input_revision : 0,
        .focus = target ? target->focus_revision : 0, .require_revisions = require_revisions};
    if (!approved && !require_revisions) approved = tl_object(attempt.authority, "authority");
    attempt.approved = approved ? json_object_ref(approved) : NULL;
    shortcut_started_at = triggered_at;
    JsonObject *message = shortcut_message(&attempt);
    json_object_set_int_member(message, "triggeredAtMs", triggered_at);
    tl_send_event("shortcut-started", message);
    trace_shortcut(&attempt, "started", "none", "none");
    JsonObject *description = NULL, *read = NULL;
    const char *phase = "authority", *reason = NULL;
    const char *failure = shortcut_failure(&attempt, FALSE, &reason);
    gboolean dispatched = FALSE;
    if (failure) goto rejected;
    phase = "ime";
    gboolean clear = tl_ime_clear(target);
    /* Input revision also includes selection callbacks. A provider epoch
       reconciliation cannot be assumed to be the only change during a probe. */
    failure = shortcut_failure(&attempt, TRUE, &reason);
    if (failure) goto rejected;
    if (!clear) { failure = "ime"; reason = "ImeUnclear"; goto rejected; }
    phase = "describe";
    description = tl_describe(target);
    failure = shortcut_failure(&attempt, TRUE, &reason);
    if (failure) goto rejected;
    if (!description) { failure = "description"; reason = "Unsupported"; goto rejected; }
    phase = "capture";
    read = tl_capture(target, tl_object(description, "authority"));
    failure = shortcut_failure(&attempt, TRUE, &reason);
    if (failure) goto rejected;
    if (!read) { failure = "capture"; reason = "Unsupported"; goto rejected; }
    tl_clear_permit();
    failure = shortcut_failure(&attempt, TRUE, &reason);
    if (failure) goto rejected;
    clear_shortcut();
    message = shortcut_message(&attempt);
    json_object_set_object_member(message, "native", read);
    read = NULL;
    json_object_set_int_member(message, "acceptedAtMs", tl_now());
    tl_send_event("shortcut", message);
    dispatched = shortcut_current(&attempt);
    trace_shortcut(&attempt, dispatched ? "dispatched" : "send", dispatched ? "none" : "connection",
        dispatched ? "none" : "PlatformFailure");
    goto finished;
rejected:
    trace_shortcut(&attempt, phase, failure, reason);
    if (shortcut_current(&attempt)) {
        message = shortcut_message(&attempt);
        json_object_set_string_member(message, "reason", reason);
        static guint diagnostic_count;
        if (g_getenv("TYPELAY_TRACE") && diagnostic_count < 96) {
            diagnostic_count++;
            JsonObject *diagnostic = json_object_new();
            json_object_set_string_member(diagnostic, "phase", phase);
            json_object_set_string_member(diagnostic, "gate", failure);
            json_object_set_object_member(message, "diagnostic", diagnostic);
        }
        tl_send_event("shortcut-rejected", message);
    }
finished:
    if (read) json_object_unref(read);
    if (description) json_object_unref(description);
    if (attempt.approved) json_object_unref(attempt.approved);
    if (attempt.authority) json_object_unref(attempt.authority);
    json_object_unref(attempt.configuration);
    return dispatched;
}
typedef struct {
    guint keyval, hardware_keycode;
    GdkModifierType state;
    gboolean down, physical;
} KeyEvent;
static gboolean shortcut_event(const KeyEvent *event) {
    if (!event->physical || !event->hardware_keycode) return FALSE;
    gboolean down = event->down;
    if (shortcut_key && event->hardware_keycode == shortcut_key) {
        gboolean consumed = shortcut_consumed;
        if (!down) { shortcut_key = 0; shortcut_consumed = FALSE; }
        return consumed;
    }
    if (!down || shortcut_key || !shortcut_configuration
        || !json_object_has_member(shortcut_configuration, "enabled")
        || !json_object_get_boolean_member(shortcut_configuration, "enabled")) return FALSE;
    guint modifiers = event->state & (GDK_SHIFT_MASK | GDK_CONTROL_MASK | GDK_ALT_MASK | GDK_SUPER_MASK | GDK_HYPER_MASK | GDK_META_MASK);
    guint key = gdk_keyval_to_upper(event->keyval);
    const char *kind = NULL;
    const char *names[] = {"wake", "direct"};
    for (guint index = 0; index < G_N_ELEMENTS(names); index++) {
        JsonObject *binding = tl_object(shortcut_configuration, names[index]);
        if ((gint64)key == tl_integer(binding, "keyval", -1)
            && (gint64)modifiers == tl_integer(binding, "modifiers", -1)) kind = names[index];
    }
    if (!kind) return FALSE;
    /* A refused shortcut still has one physical press/release pair. Remember
       it before provider callbacks so focus/config changes cannot turn a held
       key into repeated commands. Refusal never acquires the application's key. */
    shortcut_key = event->hardware_keycode;
    shortcut_consumed = FALSE;
    gboolean consumed = trigger_shortcut(kind, tl_focused(), NULL, tl_now(), FALSE);
    if (shortcut_key == event->hardware_keycode) shortcut_consumed = consumed;
    return consumed;
}
static gboolean acceptance_event(const KeyEvent *event, gint64 entered_at,
                                 const AcceptanceDelivery *delivery) {
    gboolean is_accept = event->keyval == tl_session_keyval(TRUE);
    gboolean is_exit = event->keyval == tl_session_keyval(FALSE);
    if (!is_accept && !is_exit) return FALSE;
    if ((is_accept && control_binding(TRUE)) || (is_exit && control_binding(FALSE))
        || (control_pair() && !tl_prefix_supported(tl_focused()))) return FALSE;
    gboolean consumed = FALSE;
    Target *target = tl_focused();
    JsonObject *current = publication ? json_object_ref(publication) : NULL;
    AcceptanceTiming timing = {.entered_at = entered_at, .inspect_started_at = tl_now(),
        .inspected = TRUE, .publication_count = acceptance_published_count};
    gboolean clear = tl_ime_clear(target);
    timing.inspect_finished_at = tl_now();
    guint modified = event->state & (GDK_SHIFT_MASK | GDK_CONTROL_MASK | GDK_ALT_MASK | GDK_SUPER_MASK | GDK_HYPER_MASK | GDK_META_MASK);
    if (!clear || !current || publication != current || !tl_session_matches(tl_object(tl_object(current, "authority"), "sessionBindings")) || modified || !event->physical) {
        timing.gate_at = tl_now();
        if (is_accept) trace_acceptance_timing("key", current, &timing, FALSE, delivery);
        if (is_accept) trace_tab(!clear ? "ime" : !publication ? "not-published"
            : modified ? "modified" : "nonphysical", target, current, NULL);
        if (current) json_object_unref(current);
        tl_clear_permit(); return FALSE;
    }
    JsonObject *basis = tl_object(current, "authority");
    if (g_strcmp0(target->id, tl_string(basis, "target")) != 0
        || (gint64)target->input_revision != tl_integer(basis, "inputRevision", -1)
        || (gint64)target->focus_revision != tl_integer(basis, "focusRevision", -1)
        || !tl_metadata_matches(target, basis)) {
        timing.gate_at = tl_now();
        if (is_accept) trace_acceptance_timing("key", current, &timing, FALSE, delivery);
        if (is_accept) trace_tab("authority", target, current, NULL);
        json_object_unref(current);
        tl_clear_permit(); return FALSE;
    }
    if (is_exit) {
        JsonObject *lease = tl_object(current, "lease");
        JsonObject *source = tl_object(lease, "source");
        gint64 age = tl_now() - tl_integer(source, "observedAtMs", 0);
        gboolean allowed = lease && json_object_has_member(lease, "escapeAllowed")
            && json_object_get_boolean_member(lease, "escapeAllowed") && age >= 0 && age <= 150
            && tl_integer(lease, "sessionRevision", -1) >= 0
            && tl_configuration_current(tl_string(current, "generation"))
            && g_strcmp0(tl_string(lease, "commandGeneration"), tl_string(current, "generation")) == 0;
        JsonObject *message = json_object_new();
        if (allowed) {
            json_object_set_string_member(message, "target", target->id);
            json_object_set_string_member(message, "generation", tl_string(current, "generation"));
            json_object_set_int_member(message, "sessionRevision", tl_integer(lease, "sessionRevision", -1));
        }
        json_object_unref(current);
        tl_clear_permit();
        if (allowed) { tl_send_event("escape", message); consumed = TRUE; }
        else json_object_unref(message);
        return consumed;
    }
    JsonObject *permit = tl_object(current, "permit"), *plan = tl_object(permit, "plan");
    const char *id = tl_string(plan, "id");
    const char *failure = publication_failure(target, current);
    timing.gate_at = tl_now();
    if (failure) {
        trace_acceptance_timing("key", current, &timing, FALSE, delivery);
        trace_tab(failure, target, current, NULL);
        json_object_unref(current);
        tl_clear_permit(); return FALSE;
    }
    JsonObject *request = json_object_new();
    json_object_set_string_member(request, "generation", tl_string(current, "generation"));
    json_object_set_object_member(request, "plan", tl_copy(plan));
    json_object_set_object_member(request, "authority", tl_copy(basis));
    json_object_set_int_member(request, "expiresAtMs", tl_integer(permit, "expiresAtMs", 0));
    char *plan_id = g_strdup(id);
    remember(id); /* single use, even if a later publication repeats a rejected plan */
    gint64 accepted = tl_now();
    tl_clear_permit();
    JsonObject *reply = tl_write(target, request, FALSE);
    json_object_unref(request);
    const char *status = tl_string(reply, "status");
    if (status && strcmp(status, "RejectedBeforeMutation") != 0) {
        JsonObject *message = json_object_new();
        json_object_set_string_member(message, "planId", plan_id);
        json_object_set_int_member(message, "acceptedAtMs", accepted);
        json_object_set_object_member(message, "receipt", reply);
        tl_send_event("accepted", message);
        consumed = TRUE;
    } else {
        trace_tab("write-rejected", target, current, reply);
        json_object_unref(reply);
    }
    trace_acceptance_timing("key", current, &timing, TRUE, delivery);
    json_object_unref(current);
    g_free(plan_id);
    return consumed;
}
static gboolean ordered_acceptance(const KeyEvent *event, guint64 sequence) {
    gboolean accept = event->keyval == tl_session_keyval(TRUE) && control_binding(TRUE);
    gboolean exit = event->keyval == tl_session_keyval(FALSE) && control_binding(FALSE);
    guint modifiers = event->state & (GDK_SHIFT_MASK | GDK_CONTROL_MASK | GDK_ALT_MASK | GDK_SUPER_MASK | GDK_HYPER_MASK | GDK_META_MASK);
    if ((!accept && !exit) || modifiers != GDK_CONTROL_MASK || !event->physical) return FALSE;
    OrderedTicket *ticket = ordered_ref(ordered);
    if (!ticket) return FALSE;
    gboolean consumed = FALSE;
    if (ticket->deciding || !tl_ordered_check(ticket, ticket->target, TRUE, FALSE)
        || !tl_ime_clear(ticket->target) || !tl_ordered_check(ticket, ticket->target, TRUE, TRUE)) goto finished;
    gint64 until = accept ? ticket->accept_until : ticket->exit_until;
    if (!until || tl_now() >= until) goto finished;
    ticket->deciding = TRUE;
    ticket->claim_keycode = event->hardware_keycode;
    ticket->claim_sequence = sequence;
    if (exit) {
        /* Same original pair claim, but the independent exit clock is not an
           acceptance extension. No writer sees or changes accept_until. */
        if (!tl_ordered_check(ticket, ticket->target, TRUE, TRUE) || tl_now() >= ticket->exit_until)
            goto finished;
        for (guint index = 0; index < G_N_ELEMENTS(held_keys); index++) {
            if (held_keys[index].keycode != ticket->claim_keycode
                || held_keys[index].sequence != sequence || held_keys[index].consumed) continue;
            held_keys[index].consumed = ticket->claimed = TRUE;
            JsonObject *message = json_object_new(), *lease = tl_object(ticket->publication, "lease");
            json_object_set_string_member(message, "target", ticket->target->id);
            json_object_set_string_member(message, "generation", tl_string(ticket->publication, "generation"));
            json_object_set_int_member(message, "sessionRevision", tl_integer(lease, "sessionRevision", -1));
            tl_send_event("escape", message);
            break;
        }
    } else {
        JsonObject *permit = tl_object(ticket->publication, "permit"), *plan = tl_object(permit, "plan");
        const char *id = tl_string(plan, "id");
        if (!id || already_consumed(id)) goto finished;
        JsonObject *request = json_object_new();
        json_object_set_string_member(request, "generation", tl_string(ticket->publication, "generation"));
        json_object_set_object_member(request, "plan", tl_copy(plan));
        json_object_set_object_member(request, "authority", tl_copy(tl_object(ticket->publication, "authority")));
        json_object_set_int_member(request, "expiresAtMs", ticket->accept_until);
        remember(id);
        gint64 accepted = tl_now();
        tl_drop_publication();
        JsonObject *reply = tl_write_ordered(ticket->target, request, ticket);
        json_object_unref(request);
        if (ticket->claimed) {
            JsonObject *message = json_object_new();
            json_object_set_string_member(message, "planId", id);
            json_object_set_int_member(message, "acceptedAtMs", accepted);
            json_object_set_object_member(message, "receipt", reply);
            tl_send_event("accepted", message);
        } else json_object_unref(reply);
    }
finished:
    consumed = ticket->claimed;
    if (ordered == ticket) tl_clear_permit();
    ordered_unref(ticket);
    return consumed;
}
/* Bounded diagnostics for the finite session-key candidates only. This
   observes dispatch decisions; it never changes ownership or sends a key. */
static void trace_session_key(guint keyval, const char *phase, gboolean consumed) {
    static guint count;
    const char *kind = keyval == GDK_KEY_Tab || keyval == GDK_KEY_ISO_Left_Tab ? "tab"
        : keyval == GDK_KEY_Escape ? "esc"
        : keyval >= GDK_KEY_F1 && keyval <= GDK_KEY_F24 ? "function" : NULL;
    if (!kind || !g_getenv("TYPELAY_TRACE") || count >= 256) return;
    count++;
    g_printerr("TypeLay GTK%d session-key kind=%s phase=%s consumed=%d atMonoMs=%" G_GINT64_FORMAT "\n",
        GTK_MAJOR_VERSION, kind, phase, consumed, g_get_monotonic_time() / 1000);
}
gboolean tl_overlay_key_claim(guint keycode, guint64 sequence, guint control_keycode, guint64 control_sequence) {
    gint control_index = -1, key_index = -1;
    for (guint index = 0; index < G_N_ELEMENTS(held_keys); index++) {
        if (held_keys[index].keycode == control_keycode && held_keys[index].sequence == control_sequence
            && (held_keys[index].keyval == GDK_KEY_Control_L || held_keys[index].keyval == GDK_KEY_Control_R))
            control_index = (gint)index;
        if (held_keys[index].keycode == keycode && held_keys[index].sequence == sequence
            && !held_keys[index].consumed) key_index = (gint)index;
    }
    if (held_overflow || control_index < 0 || key_index < 0 || control_index == key_index) return FALSE;
    held_keys[key_index].consumed = TRUE;
    return TRUE;
}
gboolean tl_dispatch_key(GdkEvent *event) {
    if (!event) return FALSE;
    GdkEventType type = gdk_event_get_event_type(event);
    if (type != GDK_KEY_PRESS && type != GDK_KEY_RELEASE) return FALSE;
    gint64 entered_at = tl_now();
    GdkDevice *device = gdk_event_get_device(event);
    KeyEvent key = {
        .keyval = gdk_key_event_get_keyval(event), .hardware_keycode = gdk_key_event_get_keycode(event),
        .state = gdk_event_get_modifier_state(event), .down = type == GDK_KEY_PRESS,
        .physical = device && gdk_device_get_source(device) == GDK_SOURCE_KEYBOARD
    };
    guint keyval = key.keyval, keycode = key.hardware_keycode;
    gboolean down = key.down, physical = key.physical;
    GdkModifierType state = key.state;
    AcceptanceDelivery delivery;
    const AcceptanceDelivery *delivery_at_entry = NULL;
    if (down && keyval == GDK_KEY_Tab && g_getenv("TYPELAY_TRACE")) {
        /* Freeze before callbacks, including IME inspection, can reenter GTK.
           Unfinished spans stay unfinished in this snapshot, not in live state. */
        delivery = acceptance_delivery;
        delivery.depth = g_main_depth();
        delivery_at_entry = &delivery;
    }
    if (!physical || !keycode) return FALSE;
    gint empty = -1;
    for (guint index = 0; index < G_N_ELEMENTS(held_keys); index++) {
        if (held_keys[index].keycode == keycode) {
            tl_overlay_control_cancel();
            gboolean consumed = held_keys[index].consumed;
            if (!down) { held_keys[index].keycode = 0; (void)shortcut_event(&key); }
            trace_session_key(keyval, down ? "repeat" : "release", consumed);
            return consumed;
        }
        if (!held_keys[index].keycode && empty < 0) empty = (gint)index;
    }
    gboolean candidate = keyval == GDK_KEY_Tab || keyval == GDK_KEY_ISO_Left_Tab
        || keyval == GDK_KEY_Escape || (keyval >= GDK_KEY_F1 && keyval <= GDK_KEY_F24)
        || keyval == GDK_KEY_Control_L || keyval == GDK_KEY_Control_R;
    if (!candidate) { tl_overlay_control_cancel(); return shortcut_event(&key); }
    if (!down || held_overflow) return FALSE;
    /* Saturation must never turn an untracked held press into a fresh command
       when another slot is released. Remain fail-closed for this module lifetime. */
    if (empty < 0) { held_overflow = TRUE; tl_clear_permit(); return FALSE; }
    guint64 sequence = ++held_sequence;
    held_keys[empty].keycode = keycode;
    held_keys[empty].keyval = keyval;
    held_keys[empty].sequence = sequence;
    held_keys[empty].consumed = FALSE;
    if (tl_overlay_auxiliary_key(keyval, state)) {
        if (held_keys[empty].keycode == keycode && held_keys[empty].sequence == sequence)
            held_keys[empty].consumed = TRUE;
        trace_session_key(keyval, "press", TRUE);
        return TRUE;
    }
    if (keyval == GDK_KEY_Control_L || keyval == GDK_KEY_Control_R) {
        tl_overlay_control_cancel();
        gboolean single_control = TRUE;
        for (guint index = 0; index < G_N_ELEMENTS(held_keys); index++)
            if (index != (guint)empty && held_keys[index].keycode
                && (held_keys[index].keyval == GDK_KEY_Control_L || held_keys[index].keyval == GDK_KEY_Control_R))
                single_control = FALSE;
        guint other_modifiers = state & (GDK_SHIFT_MASK | GDK_ALT_MASK | GDK_SUPER_MASK | GDK_HYPER_MASK | GDK_META_MASK);
        if (control_pair() && single_control && !other_modifiers)
            tl_overlay_control_begin(keycode, sequence);
        /* The editor's Ctrl can never also own a source ticket. */
        if (tl_focused()) ordered_begin(event);
        return FALSE; /* The original Ctrl continues to GtkText/TextView once. */
    }
    guint modifiers = state & (GDK_SHIFT_MASK | GDK_CONTROL_MASK | GDK_ALT_MASK | GDK_SUPER_MASK | GDK_HYPER_MASK | GDK_META_MASK);
    gboolean accept = keyval == tl_session_keyval(TRUE) && modifiers == (control_binding(TRUE) ? GDK_CONTROL_MASK : 0);
    gboolean exit = keyval == tl_session_keyval(FALSE) && modifiers == (control_binding(FALSE) ? GDK_CONTROL_MASK : 0);
    gboolean consumed = tl_overlay_key(accept, exit, (modifiers & GDK_CONTROL_MASK) != 0, keycode, sequence);
    tl_overlay_control_cancel();
    consumed = consumed || shortcut_event(&key)
        || ordered_acceptance(&key, sequence) || acceptance_event(&key, entered_at, delivery_at_entry);
    if (held_keys[empty].keycode == keycode && held_keys[empty].sequence == sequence)
        held_keys[empty].consumed = held_keys[empty].consumed || consumed;
    trace_session_key(keyval, "press", consumed);
    return consumed;
}
static gboolean dispatch(GtkEventControllerLegacy *controller, GdkEvent *event, gpointer data) {
    (void)data;
    GdkEventType type = gdk_event_get_event_type(event);
    /* A surface losing keyboard focus may never receive the matching release.
       Only the command-hotkey latch is retired here; session candidates retain
       their original ownership through source/popover surface changes. */
    if (type == GDK_FOCUS_CHANGE && !gdk_focus_event_get_in(event)) {
        shortcut_key = 0; shortcut_consumed = FALSE;
    }
    /* The popover is a descendant of the source. Keep its held-key ownership
       across the first Tab's focus return before considering source acceptance. */
    if (tl_dispatch_key(event)) return TRUE;
    GdkSurface *surface = gdk_event_get_surface(event);
    GtkNative *native = surface ? gtk_native_get_for_surface(surface) : NULL;
    if (native && tl_overlay_contains(GTK_WIDGET(native))) return FALSE;
    Target *target = tl_focused();
    GtkWidget *top = gtk_event_controller_get_widget(GTK_EVENT_CONTROLLER(controller));
    if (target && GTK_WIDGET(gtk_widget_get_root(target->widget)) != top) return FALSE;
    if (type == GDK_KEY_PRESS || type == GDK_KEY_RELEASE) {
        if (type == GDK_KEY_PRESS && target) {
            gboolean prefix = ordered_live(ordered, target) && !ordered->finished
                && ordered->keycode == gdk_key_event_get_keycode(event)
                && ordered->time == gdk_event_get_time(event);
            if (!prefix) tl_invalidate(target, FALSE, FALSE);
        }
    } else if (type == GDK_BUTTON_PRESS || type == GDK_SCROLL || type == GDK_TOUCH_BEGIN) {
        GtkWidget *receiver = NULL;
        double x, y, offset_x, offset_y;
        if (native && gdk_event_get_position(event, &x, &y)) {
            gtk_native_get_surface_transform(native, &offset_x, &offset_y);
            receiver = gtk_widget_pick(GTK_WIDGET(native), x + offset_x, y + offset_y, GTK_PICK_DEFAULT);
        }
        /* Root capture runs before GTK focuses a clicked sibling. Pick the
           actual recipient; an event on B does not edit the frozen source A. */
        if (target && (!receiver || (!tl_overlay_contains(receiver)
            && (receiver == target->widget || gtk_widget_is_ancestor(receiver, target->widget)))))
            tl_invalidate(target, FALSE, FALSE);
    }
    return FALSE; /* The original GtkText controller/IM receives the unchanged event. */
}
G_MODULE_EXPORT void tl_gtk4_attach(GtkWidget *client_widget) {
    if (!attach_check(g_strcmp0(g_getenv("TYPELAY_FCITX_COOPERATIVE"), "1") == 0, "coop")
        || !attach_check(!g_getenv("TYPELAY_DESKTOP"), "desktop")
        || !attach_check(client_widget && (G_OBJECT_TYPE(client_widget) == GTK_TYPE_TEXT
            || G_OBJECT_TYPE(client_widget) == GTK_TYPE_TEXT_VIEW), "type")
        || !attach_check(GDK_IS_WAYLAND_DISPLAY(gtk_widget_get_display(client_widget)), "backend")
        || !attach_check(!tl_overlay_contains(client_widget), "overlay")) return;
    GtkWidget *source = client_widget;
    if (G_OBJECT_TYPE(client_widget) == GTK_TYPE_TEXT) {
        source = gtk_widget_get_parent(client_widget);
        if (!attach_check(source && G_OBJECT_TYPE(source) == GTK_TYPE_ENTRY, "parent")
            || !attach_check(gtk_editable_get_delegate(GTK_EDITABLE(source)) == GTK_EDITABLE(client_widget), "delegate")) return;
    }
    GtkRoot *root = gtk_widget_get_root(source);
    if (!attach_check(GTK_IS_WINDOW(root), "root")) return;
    trace_attach("ready");
    if (!initialized) {
        initialized = TRUE;
        lifetime = g_uuid_string_random();
        connect_peer(NULL);
        g_timeout_add_seconds(1, connect_peer, NULL);
    }
    tl_discover(client_widget, TRUE);
    if (g_object_get_data(G_OBJECT(root), "typelay-gtk4-controller")) return;
    GtkEventController *controller = gtk_event_controller_legacy_new();
    gtk_event_controller_set_propagation_phase(controller, GTK_PHASE_CAPTURE);
    /* The popover owns a different native: its editor controller alone sees
       those keys. The shared held table still follows releases back here. */
    gtk_event_controller_set_propagation_limit(controller, GTK_LIMIT_SAME_NATIVE);
    g_signal_connect(controller, "event", G_CALLBACK(dispatch), NULL);
    g_object_set_data_full(G_OBJECT(root), "typelay-gtk4-controller", g_object_ref(controller), g_object_unref);
    gtk_widget_add_controller(GTK_WIDGET(root), controller);
}
