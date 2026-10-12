/* Real GTK3 field/IM-context cooperation experiment; synthetic text only.
 * No application text is written to the evidence log. */
#include <gtk/gtk.h>
#include <gmodule.h>
#include <stdio.h>
#include <string.h>

typedef gboolean (*Inspect)(GtkWidget *, guint64 *, guint64 *, guint64 *, gboolean *, gboolean *, gboolean *);
typedef gboolean (*Select)(GtkWidget *);
static Inspect inspect;
static Select select_candidate;
static GtkWidget *window, *entry, *other;
static gchar *directory, *captured;
static GtkEntryBuffer *captured_buffer;
static guint64 revision, captured_revision, captured_epoch;
static guint request;
static const char *result = "starting";
static gboolean writing, nested_refused, nested_done, nested_mutate;
static guint nested_depth;
static gulong reentry_handler;
static const char *replacement = "translated fixture";

typedef struct {
    gboolean valid, known, pending, composing;
    guint64 epoch, serial, delivered;
} State;

static State state(void) {
    State s = {0};
    s.valid = inspect(entry, &s.epoch, &s.serial, &s.delivered, &s.known, &s.pending, &s.composing);
    return s;
}

static gboolean settled(State s) {
    return s.valid && s.known && !s.pending && !s.composing && s.serial == s.delivered;
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
    g_free(captured);
    g_clear_object(&captured_buffer);
    GtkEntryBuffer *buffer = gtk_entry_get_buffer(GTK_ENTRY(entry));
    if (G_OBJECT_TYPE(buffer) != GTK_TYPE_ENTRY_BUFFER) return "refused-buffer";
    captured_buffer = g_object_ref(buffer);
    captured = g_strdup(gtk_entry_buffer_get_text(captured_buffer));
    captured_revision = revision;
    captured_epoch = s.epoch;
    return "captured";
}

static const char *accept(void) {
    if (writing || g_main_depth() != 1) return "refused-reentry";
    if (!captured || !gtk_widget_has_focus(entry)) return "refused-focus";
    State s = state();
    if (!settled(s)) return "refused-ime";
    if (s.epoch != captured_epoch || gtk_entry_get_buffer(GTK_ENTRY(entry)) != captured_buffer)
        return "refused-focus";
    if (revision != captured_revision || strcmp(gtk_entry_buffer_get_text(captured_buffer), captured))
        return "refused-stale";
    writing = TRUE;
    /* Real storage mutation may run application callbacks. Do not insert after
     * a callback changes the field, focus, context, or intermediate text. */
    gtk_entry_buffer_delete_text(captured_buffer, 0, -1);
    State after_delete = state();
    if (!settled(after_delete) || after_delete.epoch != captured_epoch ||
        !gtk_widget_has_focus(entry) || gtk_entry_get_buffer(GTK_ENTRY(entry)) != captured_buffer ||
        revision != captured_revision + (captured[0] ? 1u : 0u) ||
        gtk_entry_buffer_get_length(captured_buffer) != 0) {
        writing = FALSE;
        return "outcome-unknown";
    }
    gtk_entry_buffer_insert_text(captured_buffer, 0, replacement, -1);
    State after_insert = state();
    gboolean exact = settled(after_insert) && after_insert.epoch == captured_epoch &&
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
    nested_refused = !s.valid && !strcmp(accept(), "refused-reentry");
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

static void save_state(void) {
    State s = state();
    gchar *json = g_strdup_printf(
        "{\"request\":%u,\"result\":\"%s\",\"ready\":%s,\"known\":%s,"
        "\"pending\":%s,\"composing\":%s,\"settled\":%s,\"serial\":%" G_GUINT64_FORMAT ","
        "\"delivered\":%" G_GUINT64_FORMAT ",\"length\":%u,\"replacement\":%s,"
        "\"nested_refused\":%s,\"nested_depth\":%u}\n",
        request, result, gtk_widget_get_mapped(window) ? "true" : "false",
        s.valid && s.known ? "true" : "false", s.pending ? "true" : "false", s.composing ? "true" : "false",
        settled(s) ? "true" : "false", s.serial, s.delivered,
        gtk_entry_buffer_get_length(gtk_entry_get_buffer(GTK_ENTRY(entry))),
        !strcmp(gtk_entry_get_text(GTK_ENTRY(entry)), replacement) ? "true" : "false",
        nested_refused ? "true" : "false", nested_depth);
    gchar *path = g_build_filename(directory, "state.json", NULL);
    GError *error = NULL;
    if (!g_file_set_contents(path, json, -1, &error)) g_error("state write: %s", error->message);
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
    if (!directory) return 2;
    gchar *module_path = g_build_filename(directory, "lib/im-fcitx5.so", NULL);
    GModule *module = g_module_open(module_path, G_MODULE_BIND_LAZY);
    if (!module || !g_module_symbol(module, "typelay_fcitx_probe_inspect_v1", (gpointer *)&inspect) ||
        !g_module_symbol(module, "typelay_fcitx_probe_select_v1", (gpointer *)&select_candidate)) g_error("patched module: %s", g_module_error());
    g_free(module_path);
    window = gtk_window_new(GTK_WINDOW_TOPLEVEL);
    gtk_window_set_title(GTK_WINDOW(window), "TypeLay Fcitx cooperative fixture");
    GtkWidget *box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 8);
    entry = gtk_entry_new(); other = gtk_entry_new();
    gtk_container_add(GTK_CONTAINER(window), box);
    gtk_box_pack_start(GTK_BOX(box), entry, TRUE, TRUE, 0);
    gtk_box_pack_start(GTK_BOX(box), other, TRUE, TRUE, 0);
    g_signal_connect(entry, "changed", G_CALLBACK(changed), NULL);
    gtk_widget_show_all(window);
    gtk_widget_grab_focus(entry);
    g_timeout_add(20, commands, NULL);
    gtk_main();
    g_clear_object(&captured_buffer); g_free(captured); g_free(directory);
    return 0;
}
