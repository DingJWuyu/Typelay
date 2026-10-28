/* Real GTK3 field/IM-context cooperation experiment; synthetic text only.
 * No application text is written to the evidence log. */
#include <gtk/gtk.h>
#include "typelay-fcitx-v1.h"
#include <stdio.h>
#include <string.h>
#include <signal.h>
#include <unistd.h>
#include "../support/fixture_state.h"

typedef gboolean (*Select)(GtkWidget *);
static GtkWidget *window, *entry, *other;
static gchar *directory, *captured;
static GtkEntryBuffer *captured_buffer;
static guint64 revision, captured_revision, captured_epoch, captured_serial, captured_generation;
static guint request, key_queue_ack, other_preedit;
static gboolean old_delegate_destroyed;
static const char *result = "starting";
static gboolean writing, nested_refused, nested_done, nested_mutate;
static guint nested_depth;
static gulong reentry_handler;
static const char *replacement = "translated fixture";

typedef struct {
    gboolean valid, known, pending, composing;
    guint64 epoch, input_epoch, generation, serial, delivered;
    TlFcitxStateV1 raw;
} State;
static State writing_baseline;
static pid_t server_pid;
static gboolean server_paused, pause_observed;

static GtkIMContext *bound_context(void) {
    gpointer object = g_object_get_data(G_OBJECT(entry), TL_FCITX_CONTEXT_V1);
    return object && GTK_IS_IM_CONTEXT(object) ? g_object_ref(object) : NULL;
}

static State state(void) {
    State s = {0};
    GtkIMContext *context = bound_context();
    if (!context) return s;
    const TlFcitxCapabilityV1 *cap = g_object_get_data(G_OBJECT(context), TL_FCITX_CAPABILITY_V1);
    TlFcitxStateV1 actual = {.size = sizeof(actual), .version = TL_FCITX_ABI_V1};
    if (cap && cap->size == sizeof(*cap) && cap->version == TL_FCITX_ABI_V1 && cap->inspect)
        s.valid = cap->inspect(context, entry, &actual);
    if (s.valid) {
        s.raw = actual;
        s.epoch = actual.epoch; s.input_epoch = actual.input_epoch; s.generation = actual.context_generation;
        s.serial = actual.server_serial; s.delivered = actual.applied_serial;
        s.known = !!(actual.flags & TL_FCITX_KNOWN);
        s.pending = !!(actual.flags & TL_FCITX_PENDING);
        s.composing = !!(actual.flags & TL_FCITX_COMPOSING);
        s.valid = !!(actual.flags & TL_FCITX_FOCUSED);
    }
    g_object_unref(context);
    return s;
}

static gboolean local_valid(State baseline) {
    GtkIMContext *context = bound_context();
    if (!context) return FALSE;
    const TlFcitxCapabilityV1 *cap = g_object_get_data(G_OBJECT(context), TL_FCITX_CAPABILITY_V1);
    gboolean valid = cap && cap->size == sizeof(*cap) && cap->version == TL_FCITX_ABI_V1
        && cap->validate_local && cap->validate_local(context, entry, &baseline.raw);
    g_object_unref(context);
    return valid;
}

static gboolean prepare_caret(State baseline) {
    GtkIMContext *context = bound_context();
    if (!context) return FALSE;
    const TlFcitxCapabilityV1 *cap = g_object_get_data(G_OBJECT(context), TL_FCITX_CAPABILITY_V1);
    gboolean valid = cap && cap->size == sizeof(*cap) && cap->version == TL_FCITX_ABI_V1
        && cap->prepare_caret && cap->prepare_caret(context, entry, &baseline.raw);
    g_object_unref(context);
    return valid;
}

static gboolean select_candidate(GtkWidget *widget) {
    GtkIMContext *context = bound_context();
    if (!context) return FALSE;
    Select select = (Select)g_object_get_data(G_OBJECT(context), "typelay.fcitx.select-fixture.v1");
    gboolean result = select && select(widget);
    g_object_unref(context);
    return result;
}

static gboolean settled(State s) {
    return s.valid && s.known && !s.pending && !s.composing && s.serial == s.delivered;
}

/* The original Fcitx key snooper has completed its synchronous processing
 * before this actual widget event is delivered. F24 is unused by this fixture
 * and does not select, cancel, reset, or commit the pinyin composition. */
static gboolean key_queue_marker(GtkWidget *widget, GdkEventKey *event, gpointer unused) {
    (void)unused;
    if ((widget != entry && widget != other) || event->keyval != GDK_KEY_F24) return FALSE;
    if (event->type == GDK_KEY_RELEASE) ++key_queue_ack;
    return TRUE;
}

static void other_preedit_changed(GtkEntry *widget, const gchar *text, gpointer data) {
    (void)widget; (void)data;
    other_preedit = (guint)g_utf8_strlen(text, -1);
}
static void delegate_destroyed(gpointer data, GObject *object) {
    (void)data; (void)object;
    old_delegate_destroyed = TRUE;
}

static void changed(GtkEditable *editable, gpointer unused) {
    (void)editable; (void)unused;
    ++revision;
}

static const char *capture(void) {
    if (writing || !gtk_widget_has_focus(entry)) return "refused-focus";
    State s = state();
    if (!s.valid || !s.known || s.pending || s.composing) return "refused-ime";
    if (s.serial != s.delivered) return "refused-undelivered";
    g_clear_pointer(&captured, g_free);
    g_clear_object(&captured_buffer);
    GtkEntryBuffer *buffer = gtk_entry_get_buffer(GTK_ENTRY(entry));
    if (G_OBJECT_TYPE(buffer) != GTK_TYPE_ENTRY_BUFFER) return "refused-buffer";
    captured_buffer = g_object_ref(buffer);
    captured = g_strdup(gtk_entry_buffer_get_text(captured_buffer));
    captured_revision = revision;
    captured_epoch = s.epoch;
    captured_serial = s.serial;
    captured_generation = s.generation;
    return "captured";
}

static const char *accept(void) {
    if (writing || g_main_depth() != 1) return "refused-reentry";
    if (!captured || !gtk_widget_has_focus(entry)) return "refused-focus";
    State s = state();
    if (!settled(s)) return "refused-ime";
    if (gtk_entry_get_buffer(GTK_ENTRY(entry)) != captured_buffer) return "refused-focus";
    if (s.generation != captured_generation || s.epoch != captured_epoch || s.serial != captured_serial) return "refused-input";
    if (revision != captured_revision || strcmp(gtk_entry_buffer_get_text(captured_buffer), captured))
        return "refused-stale";
    gint cursor = gtk_editable_get_position(GTK_EDITABLE(entry)), bound = 0;
    g_object_get(entry, "selection-bound", &bound, NULL);
    if (!prepare_caret(s)) return "refused-prepare";
    State prepared = state();
    gint after_bound = 0;
    g_object_get(entry, "selection-bound", &after_bound, NULL);
    if (!settled(prepared) || prepared.generation != s.generation || prepared.input_epoch != s.input_epoch
        || !gtk_widget_has_focus(entry) || gtk_entry_get_buffer(GTK_ENTRY(entry)) != captured_buffer
        || revision != captured_revision || strcmp(gtk_entry_buffer_get_text(captured_buffer), captured)
        || gtk_editable_get_position(GTK_EDITABLE(entry)) != cursor || after_bound != bound)
        return "refused-prepare";
    s = prepared;
    if (!local_valid(s)) return "refused-ime";
    writing_baseline = s;
    writing = TRUE;
    /* Real storage mutation may run application callbacks. Do not insert after
     * a callback changes the field, focus, context, or intermediate text. */
    gtk_entry_buffer_delete_text(captured_buffer, 0, -1);
    if (!local_valid(s) ||
        !gtk_widget_has_focus(entry) || gtk_entry_get_buffer(GTK_ENTRY(entry)) != captured_buffer ||
        revision != captured_revision + (captured[0] ? 1u : 0u) ||
        gtk_entry_buffer_get_length(captured_buffer) != 0) {
        writing = FALSE;
        return "outcome-unknown";
    }
    gtk_entry_buffer_insert_text(captured_buffer, 0, replacement, -1);
    gboolean inserted = local_valid(s) && revision == captured_revision + (captured[0] ? 2u : 1u)
        && !strcmp(gtk_entry_buffer_get_text(captured_buffer), replacement);
    if (inserted) gtk_editable_set_position(GTK_EDITABLE(entry), (gint)g_utf8_strlen(replacement, -1));
    gboolean exact = inserted && local_valid(s) &&
        gtk_widget_has_focus(entry) && gtk_entry_get_buffer(GTK_ENTRY(entry)) == captured_buffer &&
        revision == captured_revision + (captured[0] ? 2u : 1u) &&
        !strcmp(gtk_entry_buffer_get_text(captured_buffer), replacement);
    writing = FALSE;
    return exact ? "accepted" : "outcome-unknown";
}

static gboolean nested_call(gpointer unused) {
    (void)unused;
    nested_depth = g_main_depth();
    State s = state();
    nested_refused = !s.valid && !local_valid(writing_baseline) && !strcmp(accept(), "refused-reentry");
    nested_done = TRUE;
    return G_SOURCE_REMOVE;
}

static void during_delete(GtkEntryBuffer *buffer, guint position, guint count, gpointer unused) {
    (void)position; (void)count; (void)unused;
    nested_done = FALSE;
    g_idle_add_full(G_PRIORITY_HIGH, nested_call, NULL, NULL);
    /* Deliberately exercise processEvents-style reentry, not gtk_main(). */
    while (!nested_done) g_main_context_iteration(NULL, TRUE);
    if (nested_mutate) gtk_entry_buffer_insert_text(buffer, 0, "application edit", -1);
}

static void pause_during_delete(GtkEntryBuffer *buffer, guint position, guint count, gpointer unused) {
    (void)buffer; (void)position; (void)count; (void)unused;
    server_paused = server_pid > 1 && kill(server_pid, SIGSTOP) == 0;
    if (!server_paused) return;
    gchar *path = g_strdup_printf("/proc/%ld/status", (long)server_pid);
    gint64 deadline = g_get_monotonic_time() + 500000;
    while (!pause_observed && g_get_monotonic_time() < deadline) {
        gchar *status = NULL;
        if (g_file_get_contents(path, &status, NULL, NULL))
            pause_observed = strstr(status, "State:\tT") != NULL;
        g_free(status);
        if (!pause_observed) g_usleep(1000);
    }
    g_free(path);
}

static void save_state(void) {
    State s = state();
    gchar *json = g_strdup_printf(
        "{\"request\":%u,\"result\":\"%s\",\"ready\":%s,\"known\":%s,"
        "\"probe_valid\":%s,\"pending\":%s,\"composing\":%s,\"settled\":%s,\"serial\":%" G_GUINT64_FORMAT ","
        "\"delivered\":%" G_GUINT64_FORMAT ",\"length\":%u,\"replacement\":%s,"
        "\"nested_refused\":%s,\"nested_depth\":%u,\"epoch\":%" G_GUINT64_FORMAT ",\"unchanged_capture\":%s,\"input_epoch\":%" G_GUINT64_FORMAT ",\"generation\":%" G_GUINT64_FORMAT ",\"key_queue_ack\":%u,\"other_preedit\":%u,\"other_length\":%u,\"other_focused\":%s}\n",
        request, result, gtk_widget_get_mapped(window) ? "true" : "false",
        s.valid && s.known ? "true" : "false", s.valid ? "true" : "false", s.pending ? "true" : "false", s.composing ? "true" : "false",
        settled(s) ? "true" : "false", s.serial, s.delivered,
        gtk_entry_buffer_get_length(gtk_entry_get_buffer(GTK_ENTRY(entry))),
        !strcmp(gtk_entry_get_text(GTK_ENTRY(entry)), replacement) ? "true" : "false",
        nested_refused ? "true" : "false", nested_depth, s.epoch,
        captured && !strcmp(gtk_entry_get_text(GTK_ENTRY(entry)), captured) ? "true" : "false", s.input_epoch, s.generation, key_queue_ack, other_preedit,
        gtk_entry_buffer_get_length(gtk_entry_get_buffer(GTK_ENTRY(other))),
        gtk_widget_has_focus(other) ? "true" : "false");
    gchar *path = g_build_filename(directory, "state.json", NULL);
    int failure = fixture_publish_state(path, json);
    if (failure) g_error("state write: %s", strerror(failure));
    g_free(path); g_free(json);
}

static gboolean commands(gpointer unused) {
    (void)unused;
    gchar *path = g_build_filename(directory, "command", NULL), *text = NULL;
    if (g_file_get_contents(path, &text, NULL, NULL)) {
        guint number = 0;
        char name[64];
        if (sscanf(text, "%u %63s", &number, name) == 2 && number > request) {
            request = number;
            if (!strcmp(name, "capture")) result = capture();
            else if (!strcmp(name, "accept")) result = accept();
            else if (!strcmp(name, "detached-proof")) {
                State before = state();
                const guint preedit = other_preedit;
                gboolean clear = before.raw.flags == (TL_FCITX_KNOWN | TL_FCITX_DETACHED)
                    && local_valid(before) && prepare_caret(before);
                State after = state();
                result = clear && after.raw.flags == (TL_FCITX_KNOWN | TL_FCITX_DETACHED)
                    && local_valid(after) && other_preedit == preedit && gtk_widget_has_focus(other)
                    ? "detached-clear" : "detached-refused";
            } else if (!strcmp(name, "replace-delegate")) {
                GtkIMContext *old = bound_context();
                if (!old) g_error("missing original delegate");
                old_delegate_destroyed = FALSE;
                g_object_weak_ref(G_OBJECT(old), delegate_destroyed, NULL);
                g_object_unref(old);
                g_object_set(entry, "im-module", "gtk-im-context-simple", NULL);
                gtk_entry_reset_im_context(GTK_ENTRY(entry));
                result = old_delegate_destroyed &&
                    !g_object_get_data(G_OBJECT(entry), TL_FCITX_CONTEXT_V1)
                    ? "old-delegate-released" : "old-delegate-retained";
            }
            else if (!strcmp(name, "deferred-switch")) {
                GtkIMContext *context = bound_context();
                Select action = context ? (Select)g_object_get_data(G_OBJECT(context), "typelay.fcitx.deferred-switch-fixture.v1") : NULL;
                result = action && action(entry) ? "refused-old-pinyin-transaction" : "missing-old-transaction-gate";
                if (context) g_object_unref(context);
            }
            else if (!strcmp(name, "candidate")) {
                if (!select_candidate(entry)) g_error("real candidate context unavailable");
                /* Same DBus connection: select is enqueued before the synchronous
                 * probe. GTK cannot dispatch the resulting commit during this
                 * callback; the server/client counters must expose the gap. */
                State s = state();
                result = s.valid && s.serial != s.delivered ? "refused-undelivered" : "missing-serial-gap";
            } else if (!strcmp(name, "mutate")) {
                gtk_entry_set_text(GTK_ENTRY(entry), "new application text");
                result = "mutated";
            } else if (!strcmp(name, "clear")) {
                gtk_entry_buffer_delete_text(gtk_entry_get_buffer(GTK_ENTRY(entry)), 0, -1);
                result = "cleared";
            } else if (!strcmp(name, "focus") || !strcmp(name, "refocus")) {
                gboolean original = !strcmp(name, "refocus");
                gtk_widget_grab_focus(original ? entry : other);
                result = original ? "focused-original" : "focused-other";
            } else if (!strcmp(name, "accept-nested") || !strcmp(name, "accept-reentrant-mutation")) {
                nested_mutate = !strcmp(name, "accept-reentrant-mutation");
                nested_refused = FALSE;
                reentry_handler = g_signal_connect(gtk_entry_get_buffer(GTK_ENTRY(entry)), "deleted-text", G_CALLBACK(during_delete), NULL);
                result = accept();
                g_signal_handler_disconnect(gtk_entry_get_buffer(GTK_ENTRY(entry)), reentry_handler);
            } else if (!strcmp(name, "accept-server-paused")) {
                pause_observed = FALSE;
                GtkEntryBuffer *buffer = gtk_entry_get_buffer(GTK_ENTRY(entry));
                gulong handler = g_signal_connect(buffer, "deleted-text", G_CALLBACK(pause_during_delete), NULL);
                result = accept();
                g_signal_handler_disconnect(buffer, handler);
                if (server_paused) kill(server_pid, SIGCONT);
                server_paused = FALSE;
                if (!pause_observed) result = "pause-not-observed";
            } else if (!strcmp(name, "quit")) { result = "quit"; gtk_main_quit(); }
            else g_error("unknown fixture command");
            g_print("request=%u result=%s depth=%u nested_depth=%u\n", request, result, g_main_depth(), nested_depth);
            fflush(stdout);
        }
    }
    g_free(text); g_free(path);
    save_state();
    return G_SOURCE_CONTINUE;
}

int main(int argc, char **argv) {
    gtk_init(&argc, &argv);
    directory = g_strdup(g_getenv("TYPELAY_COOPERATIVE_DIR"));
    const char *pid = g_getenv("TYPELAY_COOPERATIVE_SERVER_PID");
    server_pid = pid ? (pid_t)g_ascii_strtoll(pid, NULL, 10) : 0;
    if (!directory || server_pid <= 1) return 2;
    window = gtk_window_new(GTK_WINDOW_TOPLEVEL);
    gtk_window_set_title(GTK_WINDOW(window), "TypeLay Fcitx cooperative fixture");
    GtkWidget *box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 8);
    entry = gtk_entry_new(); other = gtk_entry_new();
    gtk_container_add(GTK_CONTAINER(window), box);
    gtk_box_pack_start(GTK_BOX(box), entry, TRUE, TRUE, 0);
    gtk_box_pack_start(GTK_BOX(box), other, TRUE, TRUE, 0);
    g_signal_connect(entry, "changed", G_CALLBACK(changed), NULL);
    g_signal_connect(entry, "key-press-event", G_CALLBACK(key_queue_marker), NULL);
    g_signal_connect(entry, "key-release-event", G_CALLBACK(key_queue_marker), NULL);
    g_signal_connect(other, "key-press-event", G_CALLBACK(key_queue_marker), NULL);
    g_signal_connect(other, "key-release-event", G_CALLBACK(key_queue_marker), NULL);
    g_signal_connect(other, "preedit-changed", G_CALLBACK(other_preedit_changed), NULL);
    gtk_widget_show_all(window);
    gtk_widget_grab_focus(entry);
    g_timeout_add(20, commands, NULL);
    gtk_main();
    g_clear_object(&captured_buffer); g_free(captured); g_free(directory);
    return 0;
}
