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
static guint input_watch, output_watch;
static GByteArray *incoming, *outgoing;
static char *lifetime;
static JsonObject *publication, *shortcut_configuration, *shortcut_authority;
static guint shortcut_key;
static gboolean shortcut_consumed;
static gboolean tab_down, tab_consumed, escape_down, escape_consumed;
static gboolean initialized;
static guint64 connection_generation, return_generation;
static GQueue consumed_ids = G_QUEUE_INIT;
static gboolean trigger_shortcut(const char *kind, Target *target, JsonObject *approved);

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
void tl_clear_permit(void) { if (publication) { json_object_unref(publication); publication = NULL; } }
static void clear_shortcut(void) {
    if (shortcut_authority) { json_object_unref(shortcut_authority); shortcut_authority = NULL; }
}
static void disconnect_peer(void) {
    connection_generation++;
    if (connection >= 0) {
        trace_connection("disconnected", 0);
        close(connection); connection = -1;
    }
    tl_overlay_hide();
    tl_clear_permit();
    clear_shortcut();
    if (shortcut_configuration) { json_object_unref(shortcut_configuration); shortcut_configuration = NULL; }
    shortcut_key = 0; shortcut_consumed = FALSE;
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
static void command(JsonObject *frame) {
    const char *operation = tl_string(frame, "op");
    gint64 id = tl_integer(frame, "id", 0);
    JsonObject *data = tl_object(frame, "data");
    if (!operation) { disconnect_peer(); return; }
    const char *target_id = tl_string(data, "target");
    Target *target = target_id ? tl_lookup(target_id) : tl_focused();
    if (g_str_has_prefix(operation, "overlay-")) respond(id, tl_overlay_command(operation, target, data));
    else if (strcmp(operation, "describe") == 0) respond(id, tl_describe(target));
    else if (strcmp(operation, "capture") == 0)
        respond(id, tl_capture(target, tl_object(data, "authority")));
    else if (strcmp(operation, "direct") == 0) respond(id, tl_write(target, data, TRUE));
    else if (strcmp(operation, "return") == 0) {
        return_generation++;
        JsonObject *basis = tl_object(data, "authority");
        gboolean focused = tl_return(target, basis);
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
        clear_shortcut();
        if (shortcut_configuration) json_object_unref(shortcut_configuration);
        shortcut_configuration = data ? tl_copy(data) : NULL;
        shortcut_key = 0; shortcut_consumed = FALSE;
        respond(id, NULL);
    } else if (strcmp(operation, "trigger-shortcut") == 0) {
        JsonObject *basis = tl_object(data, "authority");
        gint64 stamp = tl_integer(data, "triggeredAtMs", 0), age = tl_now() - stamp;
        gboolean allowed = target && target == tl_focused() && age >= 0 && age <= 150
            && shortcut_authority
            && g_strcmp0(tl_string(data, "generation"), tl_string(shortcut_configuration, "generation")) == 0
            && g_strcmp0(target->id, tl_string(shortcut_authority, "target")) == 0
            && (gint64)target->content_revision == tl_integer(basis, "contentRevision", -1)
            && (gint64)target->input_revision == tl_integer(basis, "inputRevision", -1)
            && (gint64)target->focus_revision == tl_integer(basis, "focusRevision", -1);
        JsonObject *reply = json_object_new();
        json_object_set_boolean_member(reply, "triggered", allowed
            && trigger_shortcut(tl_string(data, "kind"), target, basis));
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
        if (target && target == tl_focused() && tl_metadata_matches(target, basis)) publication = tl_copy(data);
        JsonObject *reply = json_object_new();
        json_object_set_boolean_member(reply, "published", publication != NULL);
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
static gboolean trigger_shortcut(const char *kind, Target *target, JsonObject *approved) {
    if (!kind || (strcmp(kind, "wake") != 0 && strcmp(kind, "direct") != 0)
        || !shortcut_configuration || !json_object_has_member(shortcut_configuration, "enabled")
        || !json_object_get_boolean_member(shortcut_configuration, "enabled")) return FALSE;
    gint64 age = tl_now() - tl_integer(approved, "observedAtMs", 0);
    if (!target || target != tl_focused() || !target->ime_known || target->composing
        || age < 0 || age > 150 || !tl_metadata_matches(target, approved)) return FALSE;
    JsonObject *description = tl_describe(target);
    JsonObject *read = description ? tl_capture(target, tl_object(description, "authority")) : NULL;
    if (description) json_object_unref(description);
    if (!read) return FALSE;
    JsonObject *message = json_object_new();
    json_object_set_string_member(message, "kind", kind);
    json_object_set_string_member(message, "generation", tl_string(shortcut_configuration, "generation"));
    json_object_set_object_member(message, "native", read);
    json_object_set_int_member(message, "acceptedAtMs", tl_now());
    tl_clear_permit(); clear_shortcut();
    tl_send_event("shortcut", message);
    return connection >= 0;
}
typedef struct {
    guint keyval, hardware_keycode;
    GdkModifierType state;
    gboolean down, physical;
} KeyEvent;
static gboolean shortcut_event(const KeyEvent *event) {
    gboolean down = event->down;
    if (shortcut_key && event->hardware_keycode == shortcut_key) {
        gboolean consumed = shortcut_consumed;
        if (!down) { shortcut_key = 0; shortcut_consumed = FALSE; }
        return consumed;
    }
    if (!down || shortcut_key || !event->physical || !shortcut_configuration || !shortcut_authority
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
    Target *target = tl_focused();
    if (!tl_ime_clear(target) || !shortcut_authority) return FALSE;
    JsonObject *approved = tl_object(shortcut_authority, "authority");
    /* Freeze the actual trigger's text and selection, not a later IPC poll. The
       metadata-only read approval is revalidated before any text is obtained. */
    if (!trigger_shortcut(kind, target, approved)) return FALSE;
    shortcut_key = event->hardware_keycode;
    shortcut_consumed = TRUE;
    return shortcut_consumed;
}
static gboolean key_event(const KeyEvent *event) {
    gboolean down = event->down;
    gboolean is_tab = event->keyval == GDK_KEY_Tab || event->keyval == GDK_KEY_ISO_Left_Tab;
    gboolean is_escape = event->keyval == GDK_KEY_Escape;
    if (!is_tab && !is_escape) return FALSE;
    gboolean *held = is_tab ? &tab_down : &escape_down, *consumed = is_tab ? &tab_consumed : &escape_consumed;
    if (!down) { gboolean result = *held && *consumed; *held = *consumed = FALSE; return result; }
    if (*held) return *consumed;
    *held = TRUE; *consumed = FALSE;
    Target *target = tl_focused();
    gboolean clear = tl_ime_clear(target);
    guint modified = event->state & (GDK_SHIFT_MASK | GDK_CONTROL_MASK | GDK_ALT_MASK | GDK_SUPER_MASK | GDK_HYPER_MASK | GDK_META_MASK);
    if (!clear || !publication || modified || !event->physical) {
        tl_clear_permit(); return FALSE;
    }
    JsonObject *basis = tl_object(publication, "authority");
    if (g_strcmp0(target->id, tl_string(basis, "target")) != 0
        || (gint64)target->input_revision != tl_integer(basis, "inputRevision", -1)
        || (gint64)target->focus_revision != tl_integer(basis, "focusRevision", -1)
        || !tl_metadata_matches(target, basis)) {
        tl_clear_permit(); return FALSE;
    }
    if (is_escape) {
        JsonObject *lease = tl_object(publication, "lease");
        JsonObject *source = tl_object(lease, "source");
        gint64 age = tl_now() - tl_integer(source, "observedAtMs", 0);
        gboolean allowed = lease && json_object_has_member(lease, "escapeAllowed")
            && json_object_get_boolean_member(lease, "escapeAllowed") && age >= 0 && age <= 150;
        tl_clear_permit();
        if (allowed) { tl_send_event("escape", json_object_new()); *consumed = TRUE; }
        return *consumed;
    }
    JsonObject *permit = tl_object(publication, "permit"), *plan = tl_object(permit, "plan");
    const char *id = tl_string(plan, "id");
    if (!permit || !plan || !id || already_consumed(id)) { tl_clear_permit(); return FALSE; }
    JsonObject *request = json_object_new();
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
        *consumed = TRUE;
    } else json_object_unref(reply);
    g_free(plan_id);
    return *consumed;
}
static gboolean dispatch(GtkEventControllerLegacy *controller, GdkEvent *event, gpointer data) {
    (void)data;
    /* The popover is a descendant of the source. Keep its held-key ownership
       across the first Tab's focus return before considering source acceptance. */
    if (tl_overlay_event(event)) return TRUE;
    GdkSurface *surface = gdk_event_get_surface(event);
    GtkNative *native = surface ? gtk_native_get_for_surface(surface) : NULL;
    if (native && tl_overlay_contains(GTK_WIDGET(native))) return FALSE;
    Target *target = tl_focused();
    GtkWidget *top = gtk_event_controller_get_widget(GTK_EVENT_CONTROLLER(controller));
    if (target && GTK_WIDGET(gtk_widget_get_root(target->widget)) != top) return FALSE;
    GdkEventType type = gdk_event_get_event_type(event);
    if (type == GDK_KEY_PRESS || type == GDK_KEY_RELEASE) {
        GdkDevice *device = gdk_event_get_device(event);
        KeyEvent key = {
            .keyval = gdk_key_event_get_keyval(event),
            .hardware_keycode = gdk_key_event_get_keycode(event),
            .state = gdk_event_get_modifier_state(event),
            .down = type == GDK_KEY_PRESS,
            .physical = device && gdk_device_get_source(device) == GDK_SOURCE_KEYBOARD
        };
        if (shortcut_event(&key) || key_event(&key)) return TRUE;
        if (key.down && target) tl_invalidate(target, FALSE, FALSE);
    } else if (type == GDK_BUTTON_PRESS || type == GDK_SCROLL || type == GDK_TOUCH_BEGIN) {
        if (target) tl_invalidate(target, FALSE, FALSE);
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
    g_signal_connect(controller, "event", G_CALLBACK(dispatch), NULL);
    gtk_widget_add_controller(GTK_WIDGET(root), controller);
    g_object_set_data(G_OBJECT(root), "typelay-gtk4-controller", controller);
}
