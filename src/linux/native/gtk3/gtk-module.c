#define _GNU_SOURCE
#include "gtk-target.h"
#include "gtk-overlay.h"
#include <gdk/gdkwayland.h>
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
static struct { guint keycode; guint64 sequence; gboolean consumed; } held_keys[64];
static guint64 held_sequence, configuration_sequence;
static gboolean configuring, held_overflow;
static gboolean initialized;
static guint64 connection_generation, return_generation, shortcut_sequence;
static gint64 shortcut_started_at = -1;
static GQueue consumed_ids = G_QUEUE_INIT;
static gboolean trigger_shortcut(const char *kind, Target *target, JsonObject *approved, gint64 triggered_at, gboolean require_revisions);
static gboolean already_consumed(const char *id);

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
    /* Canonical only: no aliases, leading zeros, modifiers or unknown keys. */
    for (guint index = 1; index <= 24; index++) {
        char canonical[4];
        g_snprintf(canonical, sizeof canonical, "F%u", index);
        if (g_strcmp0(value, canonical) == 0) return GDK_KEY_F1 + index - 1;
    }
    return 0;
}
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
JsonObject *tl_session_bindings(Target *target) {
    const char *generation = tl_string(shortcut_configuration, "generation");
    if (!tl_configuration_current(generation) || !valid_pair()) return NULL;
    JsonObject *bindings = json_object_new();
    json_object_set_string_member(bindings, "generation", generation);
    json_object_set_string_member(bindings, "accept", tl_string(shortcut_configuration, "accept"));
    json_object_set_string_member(bindings, "exit", tl_string(shortcut_configuration, "exit"));
    GtkWidget *top = target && !target->dead ? gtk_widget_get_toplevel(target->widget) : NULL;
    gboolean source = initialized && target && !target->dead && GTK_IS_WINDOW(top)
        && boolean(shortcut_configuration, "sessionEnabled");
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
        && g_strcmp0(tl_string(source, "backend"), "linux-gtk3") == 0
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
void tl_clear_permit(void) {
    if (publication_timer) { g_source_remove(publication_timer); publication_timer = 0; }
    if (publication) { json_object_unref(publication); publication = NULL; }
    tl_overlay_acceptance();
}
static void clear_shortcut(void) {
    if (shortcut_authority) { json_object_unref(shortcut_authority); shortcut_authority = NULL; }
}
static void disconnect_peer(void) {
    connection_generation++;
    configuration_sequence++;
    configuring = FALSE;
    if (connection >= 0) { close(connection); connection = -1; }
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
gboolean tl_acceptance_ready(Target *target) {
    JsonObject *current = publication ? json_object_ref(publication) : NULL;
    gboolean ready = publication_failure(target, current) == NULL;
    if (current) json_object_unref(current);
    return ready;
}
static void refresh_acceptance(void);
static gboolean acceptance_expired(gpointer data) {
    (void)data;
    publication_timer = 0;
    /* Keep the independently timed Escape lease; only Tab readiness expires. */
    refresh_acceptance();
    return G_SOURCE_REMOVE;
}
static void refresh_acceptance(void) {
    if (publication_timer) { g_source_remove(publication_timer); publication_timer = 0; }
    tl_overlay_acceptance();
    gint64 now = tl_now(), until = tl_integer(tl_object(publication, "permit"), "expiresAtMs", 0);
    if (until > now && until - now <= 100)
        publication_timer = g_timeout_add((guint)(until - now), acceptance_expired, NULL);
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
    if (!g_getenv("TYPELAY_TRACE")) return;
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
    g_printerr("TypeLay GTK Tab check=%s planId=%s remainingMs=%" G_GINT64_FORMAT
        " authorityAgeMs=%" G_GINT64_FORMAT " inputRevision=%" G_GUINT64_FORMAT
        " expectedInputRevision=%" G_GINT64_FORMAT " receiptReason=%s\n", reason,
        id && g_uuid_string_is_valid(id) ? id : id ? "invalid" : "none", permit ? until - now : -1,
        observed >= 0 ? now - observed : -1, target ? target->input_revision : 0,
        tl_integer(basis, "inputRevision", -1), safe_receipt);
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
    else if (strcmp(operation, "direct") == 0) respond(id, tl_write(target, data, TRUE));
    else if (strcmp(operation, "return") == 0) {
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
        tl_clear_permit();
        if (tl_integer(data, "shortcuts", 0)) clear_shortcut();
        respond(id, NULL);
    } else if (strcmp(operation, "publish") == 0) {
        tl_clear_permit();
        JsonObject *basis = tl_object(data, "authority");
        if (target && target == tl_focused() && tl_metadata_matches(target, basis)
            && tl_configuration_current(tl_string(data, "generation"))
            && tl_session_matches(tl_object(basis, "sessionBindings"))
            && g_strcmp0(tl_string(tl_object(data, "lease"), "commandGeneration"), tl_string(data, "generation")) == 0)
            publication = tl_copy(data);
        /* The observation may revoke publication; reacquire it only afterwards. */
        if (publication) (void)tl_ime_clear(target);
        refresh_acceptance();
        JsonObject *state = publication_state(target), *reply = json_object_new();
        json_object_set_boolean_member(reply, "published", publication
            && (!tl_object(data, "permit") || json_object_get_boolean_member(state, "ready")));
        if (target) {
            json_object_set_string_member(state, "target", target->id);
            tl_send_event("publication", state);
        } else json_object_unref(state);
        respond(id, reply);
    } else { disconnect_peer(); }
}
static gboolean read_input(GIOChannel *channel, GIOCondition condition, gpointer data) {
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
        command(frame); json_object_unref(frame);
    }
    return connection >= 0 ? G_SOURCE_CONTINUE : G_SOURCE_REMOVE;
}
static gboolean connect_peer(gpointer data) {
    (void)data;
    if (connection >= 0) return G_SOURCE_CONTINUE;
    const char *runtime = g_getenv("XDG_RUNTIME_DIR");
    if (!runtime || !g_path_is_absolute(runtime)) return G_SOURCE_CONTINUE;
    char *identity = g_strconcat(g_getenv("WAYLAND_DISPLAY") ? g_getenv("WAYLAND_DISPLAY") : "", "\n",
        g_getenv("NIRI_SOCKET") ? g_getenv("NIRI_SOCKET") : "", NULL);
    char *hash = g_compute_checksum_for_string(G_CHECKSUM_SHA256, identity, -1);
    hash[16] = '\0';
    char *path = g_build_filename(runtime, "typelay", hash, "native.sock", NULL);
    g_free(hash); g_free(identity);
    struct stat info;
    struct sockaddr_un address = {.sun_family = AF_UNIX};
    if (strlen(path) >= sizeof address.sun_path || lstat(path, &info) != 0
        || !S_ISSOCK(info.st_mode) || info.st_uid != getuid() || (info.st_mode & 0077) != 0) {
        g_free(path); return G_SOURCE_CONTINUE;
    }
    strcpy(address.sun_path, path); g_free(path);
    gint socket_fd = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
    if (socket_fd < 0) return G_SOURCE_CONTINUE;
    /* Unix-domain local connect is immediate or fails; never block the application's UI. */
    if (connect(socket_fd, (struct sockaddr *)&address, sizeof address) != 0) { close(socket_fd); return G_SOURCE_CONTINUE; }
    struct ucred peer;
    socklen_t length = sizeof peer;
    if (getsockopt(socket_fd, SOL_SOCKET, SO_PEERCRED, &peer, &length) != 0 || peer.uid != getuid()) {
        close(socket_fd); return G_SOURCE_CONTINUE;
    }
    connection = socket_fd;
    if (!incoming) incoming = g_byte_array_new();
    if (!outgoing) outgoing = g_byte_array_new();
    GIOChannel *channel = g_io_channel_unix_new(connection);
    input_watch = g_io_add_watch(channel, G_IO_IN | G_IO_HUP | G_IO_ERR | G_IO_NVAL, read_input, NULL);
    g_io_channel_unref(channel);
    JsonObject *hello = json_object_new();
    json_object_set_int_member(hello, "protocol", 2);
    json_object_set_string_member(hello, "backend", "gtk3");
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
    g_printerr("TypeLay GTK3 shortcut phase=%s gate=%s reason=%s commandId=%" G_GUINT64_FORMAT
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
static gboolean shortcut_event(GdkEventKey *event) {
    if (event->send_event || !event->hardware_keycode) return FALSE;
    gboolean down = event->type == GDK_KEY_PRESS;
    if (shortcut_key && event->hardware_keycode == shortcut_key) {
        gboolean consumed = shortcut_consumed;
        if (!down) { shortcut_key = 0; shortcut_consumed = FALSE; }
        return consumed;
    }
    if (!down || shortcut_key || !shortcut_configuration
        || !json_object_has_member(shortcut_configuration, "enabled")
        || !json_object_get_boolean_member(shortcut_configuration, "enabled")) return FALSE;
    guint modifiers = event->state & (GDK_SHIFT_MASK | GDK_CONTROL_MASK | GDK_MOD1_MASK | GDK_MOD3_MASK
        | GDK_MOD4_MASK | GDK_MOD5_MASK | GDK_SUPER_MASK | GDK_HYPER_MASK | GDK_META_MASK);
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
static gboolean acceptance_event(GdkEventKey *event) {
    gboolean is_accept = event->keyval == tl_session_keyval(TRUE);
    gboolean is_exit = event->keyval == tl_session_keyval(FALSE);
    if (!is_accept && !is_exit) return FALSE;
    gboolean consumed = FALSE;
    Target *target = tl_focused();
    JsonObject *current = publication ? json_object_ref(publication) : NULL;
    gboolean clear = tl_ime_clear(target);
    guint modified = event->state & (GDK_SHIFT_MASK | GDK_CONTROL_MASK | GDK_MOD1_MASK | GDK_MOD3_MASK
        | GDK_MOD4_MASK | GDK_MOD5_MASK | GDK_SUPER_MASK | GDK_HYPER_MASK | GDK_META_MASK);
    if (!clear || !current || publication != current || !tl_session_matches(tl_object(tl_object(current, "authority"), "sessionBindings")) || modified || event->send_event) {
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
    if (failure) {
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
    json_object_unref(current);
    g_free(plan_id);
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
gboolean tl_dispatch_key(GdkEvent *event) {
    if (!event || (event->type != GDK_KEY_PRESS && event->type != GDK_KEY_RELEASE)) return FALSE;
    GdkEventKey *key = &event->key;
    guint keyval = key->keyval, keycode = key->hardware_keycode;
    gboolean down = event->type == GDK_KEY_PRESS, physical = !key->send_event;
    GdkModifierType state = key->state;
    if (!physical || !keycode) return FALSE;
    gint empty = -1;
    for (guint index = 0; index < G_N_ELEMENTS(held_keys); index++) {
        if (held_keys[index].keycode == keycode) {
            gboolean consumed = held_keys[index].consumed;
            if (!down) { held_keys[index].keycode = 0; (void)shortcut_event(key); }
            trace_session_key(keyval, down ? "repeat" : "release", consumed);
            return consumed;
        }
        if (!held_keys[index].keycode && empty < 0) empty = (gint)index;
    }
    gboolean candidate = keyval == GDK_KEY_Tab || keyval == GDK_KEY_ISO_Left_Tab
        || keyval == GDK_KEY_Escape || (keyval >= GDK_KEY_F1 && keyval <= GDK_KEY_F24);
    if (!candidate) return shortcut_event(key);
    if (!down || held_overflow) return FALSE;
    /* Saturation must never turn an untracked held press into a fresh command
       when another slot is released. Remain fail-closed for this module lifetime. */
    if (empty < 0) { held_overflow = TRUE; return FALSE; }
    guint64 sequence = ++held_sequence;
    held_keys[empty].keycode = keycode;
    held_keys[empty].sequence = sequence;
    held_keys[empty].consumed = FALSE;
    gboolean consumed = tl_overlay_key(keyval, state) || shortcut_event(key) || acceptance_event(key);
    if (held_keys[empty].keycode == keycode && held_keys[empty].sequence == sequence)
        held_keys[empty].consumed = consumed;
    trace_session_key(keyval, "press", consumed);
    return consumed;
}
static void dispatch(GdkEvent *event, gpointer data) {
    (void)data;
    /* Keyboard focus leaving a surface can deliver its release to another
       process. Only the older command-hotkey latch is retired here; session
       candidates stay fail-closed until an observed release, including popovers. */
    if (event->type == GDK_FOCUS_CHANGE && !event->focus_change.in && !event->focus_change.send_event) {
        shortcut_key = 0; shortcut_consumed = FALSE;
    }
    if (tl_dispatch_key(event)) return;
    if (event->type == GDK_KEY_PRESS || event->type == GDK_KEY_RELEASE) {
        if (event->type == GDK_KEY_PRESS) {
            Target *target = tl_focused();
            if (target) tl_invalidate(target, FALSE, FALSE);
        }
    } else if ((event->type == GDK_BUTTON_PRESS || event->type == GDK_SCROLL)
        && !tl_overlay_contains(gtk_get_event_widget(event))) {
        Target *target = tl_focused();
        GtkWidget *receiver = gtk_get_event_widget(event);
        /* A click on B arrives before GTK moves focus from A. Attribute pointer
           input to its real widget, not to the field that is about to lose focus. */
        if (target && (!receiver || receiver == target->widget
            || gtk_widget_is_ancestor(receiver, target->widget)))
            tl_invalidate(target, FALSE, FALSE);
    }
    gtk_main_do_event(event);
}
static gboolean realized(GSignalInvocationHint *hint, guint count, const GValue *values, gpointer data) {
    (void)hint; (void)data;
    if (count > 0) tl_discover(g_value_get_object(&values[0]), TRUE);
    return TRUE;
}
static void initialize_display(GdkDisplay *display) {
    if (initialized || g_getenv("TYPELAY_DESKTOP") || !display || !GDK_IS_WAYLAND_DISPLAY(display)) return;
    /* Module loading can precede the first GtkWidget class initialization. Looking
       up its signal without class-ref used to return zero and disable the module. */
    gpointer widget_class = g_type_class_ref(GTK_TYPE_WIDGET);
    guint signal_id = g_signal_lookup("realize", GTK_TYPE_WIDGET);
    if (!signal_id) { g_type_class_unref(widget_class); return; }
    g_signal_add_emission_hook(signal_id, 0, realized, NULL, NULL);
    g_type_class_unref(widget_class);
    lifetime = g_uuid_string_random();
    initialized = TRUE;
    gdk_event_handler_set(dispatch, NULL, NULL);
    connect_peer(NULL);
    g_timeout_add_seconds(1, connect_peer, NULL);
}
G_MODULE_EXPORT void gtk_module_init(gint *argc, gchar ***argv) {
    (void)argc; (void)argv;
    initialize_display(gdk_display_get_default());
}
G_MODULE_EXPORT void gtk_module_display_init(GdkDisplay *display) {
    initialize_display(display);
}
