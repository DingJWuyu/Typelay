#include <gtk/gtk.h>
#include <json-glib/json-glib.h>
#include "../../../src/linux/native/fcitx5/typelay-fcitx-v1.h"

/* Only the isolated test executable writes this evidence. Production has no
 * command to dump source text, inject input or change a test model. */
static GtkWidget *report_window, *report_first, *report_second, *other_window, *other_entry;
static GtkWidget *report_multiline, *excluded_views[3];
static const char *report_path;
static char *last_report;
typedef struct {
    guint insertions, deletions, focus_in, focus_out;
} EntryEvidence;
static EntryEvidence first_evidence, second_evidence;
static JsonObject *first_insert_context, *first_delete_context;

static GtkWidget *entry_client(GtkWidget *widget) {
    return widget;
}
static void focus_changed(GObject *object, GParamSpec *spec, gpointer data) {
    (void)spec;
    EntryEvidence *evidence = data;
    if (gtk_widget_has_focus(GTK_WIDGET(object))) evidence->focus_in++;
    else evidence->focus_out++;
}
static void remember_entry_change(EntryEvidence *evidence, JsonObject **observation) {
    if (evidence != &first_evidence) return;
    if (*observation) json_object_unref(*observation);
    JsonObject *context = *observation = json_object_new();
    gint start, end;
    gtk_editable_get_selection_bounds(GTK_EDITABLE(report_second), &start, &end);
    json_object_set_boolean_member(context, "secondFocused",
        gtk_widget_has_focus(entry_client(report_second)));
    json_object_set_string_member(context, "secondText", gtk_entry_get_text(GTK_ENTRY(report_second)));
    json_object_set_int_member(context, "secondStart", start);
    json_object_set_int_member(context, "secondEnd", end);
    json_object_set_int_member(context, "firstFocusIn", first_evidence.focus_in);
    json_object_set_int_member(context, "firstFocusOut", first_evidence.focus_out);
}
static void entry_inserted(GtkEntryBuffer *buffer, guint position, const char *text, guint count, gpointer data) {
    (void)buffer; (void)position; (void)text; (void)count;
    EntryEvidence *evidence = data;
    evidence->insertions++;
    remember_entry_change(evidence, &first_insert_context);
}
static void entry_deleted(GtkEntryBuffer *buffer, guint position, guint count, gpointer data) {
    (void)buffer; (void)position; (void)count;
    EntryEvidence *evidence = data;
    evidence->deletions++;
    remember_entry_change(evidence, &first_delete_context);
}
static void watch_entry(GtkWidget *widget, EntryEvidence *evidence) {
    GtkEntryBuffer *buffer = gtk_entry_get_buffer(GTK_ENTRY(widget));
    g_signal_connect_after(buffer, "inserted-text", G_CALLBACK(entry_inserted), evidence);
    g_signal_connect_after(buffer, "deleted-text", G_CALLBACK(entry_deleted), evidence);
    g_signal_connect(entry_client(widget), "notify::has-focus", G_CALLBACK(focus_changed), evidence);
}
static guint user_actions_started, user_actions_finished;
static void action_started(GtkTextBuffer *buffer, gpointer data) {
    (void)buffer; (void)data; user_actions_started++;
}
static void action_finished(GtkTextBuffer *buffer, gpointer data) {
    (void)buffer; (void)data; user_actions_finished++;
}

static gboolean inspect_ime(GtkWidget *widget, TlFcitxStateV1 *state) {
    GtkIMContext *context = widget ? g_object_get_data(G_OBJECT(widget), TL_FCITX_CONTEXT_V1) : NULL;
    if (!context || !GTK_IS_IM_CONTEXT(context)) return FALSE;
    g_object_ref(context);
    const TlFcitxCapabilityV1 *cap = g_object_get_data(G_OBJECT(context), TL_FCITX_CAPABILITY_V1);
    gboolean valid = cap && cap->size == sizeof(*cap) && cap->version == TL_FCITX_ABI_V1
        && cap->inspect && cap->inspect(context, widget, state);
    g_object_unref(context);
    return valid;
}
static gboolean ime_clear(const TlFcitxStateV1 *state) {
    return state->flags == (TL_FCITX_KNOWN | TL_FCITX_FOCUSED)
        && state->server_serial == state->applied_serial;
}
/* These are observations of the real widgets, never inputs to the provider. */
static void report_entry(JsonBuilder *builder, const char *name, GtkWidget *widget, const EntryEvidence *evidence) {
    gint start, end;
    gtk_editable_get_selection_bounds(GTK_EDITABLE(widget), &start, &end);
    json_builder_set_member_name(builder, name);
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "focused");
    json_builder_add_boolean_value(builder, gtk_widget_has_focus(entry_client(widget)));
    json_builder_set_member_name(builder, "start"); json_builder_add_int_value(builder, start);
    json_builder_set_member_name(builder, "end"); json_builder_add_int_value(builder, end);
    json_builder_set_member_name(builder, "insertions"); json_builder_add_int_value(builder, evidence->insertions);
    json_builder_set_member_name(builder, "deletions"); json_builder_add_int_value(builder, evidence->deletions);
    json_builder_set_member_name(builder, "focusIn"); json_builder_add_int_value(builder, evidence->focus_in);
    json_builder_set_member_name(builder, "focusOut"); json_builder_add_int_value(builder, evidence->focus_out);
    gint x, y;
    if (gtk_widget_translate_coordinates(widget, report_window, 0, 0, &x, &y)) {
        json_builder_set_member_name(builder, "x"); json_builder_add_int_value(builder, x);
        json_builder_set_member_name(builder, "y"); json_builder_add_int_value(builder, y);
        json_builder_set_member_name(builder, "width");
        json_builder_add_int_value(builder, gtk_widget_get_allocated_width(widget));
        json_builder_set_member_name(builder, "height");
        json_builder_add_int_value(builder, gtk_widget_get_allocated_height(widget));
    }
    TlFcitxStateV1 state = { .size = sizeof(state), .version = TL_FCITX_ABI_V1 };
    gboolean valid = inspect_ime(widget, &state);
    json_builder_set_member_name(builder, "ime");
    if (valid) {
        json_builder_begin_object(builder);
        json_builder_set_member_name(builder, "flags"); json_builder_add_int_value(builder, state.flags);
        json_builder_set_member_name(builder, "context"); json_builder_add_int_value(builder, state.context_generation);
        json_builder_set_member_name(builder, "input"); json_builder_add_int_value(builder, state.input_epoch);
        json_builder_set_member_name(builder, "server"); json_builder_add_int_value(builder, state.server_serial);
        json_builder_set_member_name(builder, "applied"); json_builder_add_int_value(builder, state.applied_serial);
        json_builder_end_object(builder);
    } else json_builder_add_null_value(builder);
    json_builder_end_object(builder);
}
static void find_candidate(GtkWidget *widget, gpointer data) {
    GtkWidget **candidate = data;
    if (GTK_IS_TEXT_VIEW(widget)
        && g_strcmp0(gtk_buildable_get_name(GTK_BUILDABLE(widget)), "typelay-candidate") == 0)
        *candidate = widget;
    else if (GTK_IS_CONTAINER(widget)) gtk_container_forall(GTK_CONTAINER(widget), find_candidate, data);
}

static gboolean report_state(gpointer unused) {
    (void)unused;
    GtkWidget *active = other_window && gtk_window_is_active(GTK_WINDOW(other_window)) ? other_window : report_window;
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(active));
    JsonBuilder *builder = json_builder_new();
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "observedAtMs");
    json_builder_add_int_value(builder, g_get_monotonic_time() / 1000);
    json_builder_set_member_name(builder, "first");
    json_builder_add_string_value(builder, gtk_entry_get_text(GTK_ENTRY(report_first)));
    json_builder_set_member_name(builder, "second");
    json_builder_add_string_value(builder, gtk_entry_get_text(GTK_ENTRY(report_second)));
    if (other_entry) {
        json_builder_set_member_name(builder, "other");
        json_builder_add_string_value(builder, gtk_entry_get_text(GTK_ENTRY(other_entry)));
    }
    json_builder_set_member_name(builder, "focus");
    json_builder_add_string_value(builder, focus ? gtk_buildable_get_name(GTK_BUILDABLE(focus)) : "");
    if (g_getenv("TYPELAY_FIXTURE_COOPERATIVE")) {
        report_entry(builder, "firstEntry", report_first, &first_evidence);
        report_entry(builder, "secondEntry", report_second, &second_evidence);
        const char *names[] = {"firstInsertContext", "firstDeleteContext"};
        JsonObject *contexts[] = {first_insert_context, first_delete_context};
        for (guint i = 0; i < G_N_ELEMENTS(contexts); i++) {
            json_builder_set_member_name(builder, names[i]);
            if (contexts[i]) {
                JsonNode *change = json_node_new(JSON_NODE_OBJECT);
                json_node_set_object(change, contexts[i]);
                json_builder_add_value(builder, change);
            } else json_builder_add_null_value(builder);
        }
        TlFcitxStateV1 state = { .size = sizeof(state), .version = TL_FCITX_ABI_V1 };
        gboolean valid = inspect_ime(report_first, &state);
        json_builder_set_member_name(builder, "cooperative");
        json_builder_add_boolean_value(builder, valid);
        json_builder_set_member_name(builder, "imeFocused");
        json_builder_add_boolean_value(builder, valid && (state.flags & TL_FCITX_FOCUSED));
        json_builder_set_member_name(builder, "imeClear");
        json_builder_add_boolean_value(builder, valid && ime_clear(&state));
        json_builder_set_member_name(builder, "imeComposing");
        json_builder_add_boolean_value(builder, valid && (state.flags & TL_FCITX_COMPOSING));
        state = (TlFcitxStateV1){ .size = sizeof(state), .version = TL_FCITX_ABI_V1 };
        valid = inspect_ime(focus, &state);
        json_builder_set_member_name(builder, "focusedImeClear");
        json_builder_add_boolean_value(builder, valid && ime_clear(&state));
        json_builder_set_member_name(builder, "focusedImeComposing");
        json_builder_add_boolean_value(builder, valid && (state.flags & TL_FCITX_COMPOSING));
        json_builder_set_member_name(builder, "fullscreen");
        json_builder_add_boolean_value(builder,
            (gdk_window_get_state(gtk_widget_get_window(report_window)) & GDK_WINDOW_STATE_FULLSCREEN) != 0);
        json_builder_set_member_name(builder, "width");
        json_builder_add_int_value(builder, gtk_widget_get_allocated_width(report_window));
        json_builder_set_member_name(builder, "height");
        json_builder_add_int_value(builder, gtk_widget_get_allocated_height(report_window));
        json_builder_set_member_name(builder, "scale");
        json_builder_add_int_value(builder, gtk_widget_get_scale_factor(report_window));
        GtkWidget *candidate = NULL;
        gtk_container_forall(GTK_CONTAINER(report_window), find_candidate, &candidate);
        gint x, y;
        json_builder_set_member_name(builder, "candidate");
        if (candidate && gtk_widget_get_mapped(candidate)
            && gtk_widget_translate_coordinates(candidate, report_window, 0, 0, &x, &y)) {
            json_builder_begin_object(builder);
            json_builder_set_member_name(builder, "x"); json_builder_add_int_value(builder, x);
            json_builder_set_member_name(builder, "y"); json_builder_add_int_value(builder, y);
            json_builder_set_member_name(builder, "width");
            json_builder_add_int_value(builder, gtk_widget_get_allocated_width(candidate));
            json_builder_set_member_name(builder, "height");
            json_builder_add_int_value(builder, gtk_widget_get_allocated_height(candidate));
            GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(candidate));
            GtkTextIter first, last;
            gtk_text_buffer_get_bounds(buffer, &first, &last);
            char *text = gtk_text_buffer_get_text(buffer, &first, &last, TRUE);
            json_builder_set_member_name(builder, "text"); json_builder_add_string_value(builder, text);
            g_free(text);
            json_builder_end_object(builder);
        } else json_builder_add_null_value(builder);
    }
    json_builder_set_member_name(builder, "userActionsStarted"); json_builder_add_int_value(builder, user_actions_started);
    json_builder_set_member_name(builder, "userActionsFinished"); json_builder_add_int_value(builder, user_actions_finished);
    json_builder_set_member_name(builder, "multiline");
    json_builder_begin_object(builder);
    GtkTextBuffer *multiline = gtk_text_view_get_buffer(GTK_TEXT_VIEW(report_multiline));
    GtkTextIter begin, end, selected_begin, selected_end;
    gtk_text_buffer_get_bounds(multiline, &begin, &end);
    char *body = gtk_text_buffer_get_text(multiline, &begin, &end, TRUE);
    json_builder_set_member_name(builder, "text"); json_builder_add_string_value(builder, body);
    g_free(body);
    gtk_text_buffer_get_selection_bounds(multiline, &selected_begin, &selected_end);
    json_builder_set_member_name(builder, "start"); json_builder_add_int_value(builder, gtk_text_iter_get_offset(&selected_begin));
    json_builder_set_member_name(builder, "end"); json_builder_add_int_value(builder, gtk_text_iter_get_offset(&selected_end));
    gint x, y;
    if (gtk_widget_translate_coordinates(report_multiline, report_window, 0, 0, &x, &y)) {
        json_builder_set_member_name(builder, "x"); json_builder_add_int_value(builder, x);
        json_builder_set_member_name(builder, "y"); json_builder_add_int_value(builder, y);
    }
    json_builder_end_object(builder);
    json_builder_set_member_name(builder, "excludedTextViews");
    json_builder_begin_array(builder);
    for (guint i = 0; i < G_N_ELEMENTS(excluded_views); i++) {
        if (gtk_widget_translate_coordinates(excluded_views[i], report_window, 0, 0, &x, &y)) {
            json_builder_begin_object(builder);
            json_builder_set_member_name(builder, "name"); json_builder_add_string_value(builder, gtk_buildable_get_name(GTK_BUILDABLE(excluded_views[i])));
            json_builder_set_member_name(builder, "x"); json_builder_add_int_value(builder, x);
            json_builder_set_member_name(builder, "y"); json_builder_add_int_value(builder, y);
            json_builder_end_object(builder);
        }
    }
    json_builder_end_array(builder);
    json_builder_end_object(builder);
    JsonNode *node = json_builder_get_root(builder);
    JsonGenerator *generator = json_generator_new();
    json_generator_set_root(generator, node);
    char *data = json_generator_to_data(generator, NULL);
    if (g_strcmp0(data, last_report) != 0) {
        g_file_set_contents(report_path, data, -1, NULL);
        g_free(last_report);
        last_report = g_strdup(data);
    }
    g_free(data);
    g_object_unref(generator);
    json_node_free(node);
    g_object_unref(builder);
    return G_SOURCE_CONTINUE;
}

/* An actual GTK client, not a production backend or a source-string assertion. */
int main(int argc, char **argv) {
    gtk_init(&argc, &argv);
    GtkWidget *window = gtk_window_new(GTK_WINDOW_TOPLEVEL);
    gtk_window_set_title(GTK_WINDOW(window), "TypeLay GTK contract fixture");
    gtk_window_set_default_size(GTK_WINDOW(window), 650, 480);
    GtkWidget *title = gtk_header_bar_new();
    gtk_header_bar_set_title(GTK_HEADER_BAR(title), "TypeLay input contracts");
    gtk_header_bar_set_show_close_button(GTK_HEADER_BAR(title), TRUE);
    gtk_window_set_titlebar(GTK_WINDOW(window), title);
    GtkWidget *box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 16);
    gtk_container_set_border_width(GTK_CONTAINER(box), 32);
    gtk_container_add(GTK_CONTAINER(window), box);
    GtkWidget *first = gtk_entry_new(), *second = gtk_search_entry_new(), *password = gtk_entry_new();
    gtk_buildable_set_name(GTK_BUILDABLE(first), "fixture-first");
    gtk_buildable_set_name(GTK_BUILDABLE(second), "fixture-second");
    gtk_buildable_set_name(GTK_BUILDABLE(password), "fixture-password");
    const char *initial = g_getenv("TYPELAY_FIXTURE_INITIAL_TEXT");
    gtk_entry_set_text(GTK_ENTRY(first), initial ? initial : "alpha 😀 omega");
    gtk_entry_set_text(GTK_ENTRY(second), "second field");
    gtk_entry_set_visibility(GTK_ENTRY(password), FALSE);
    gtk_entry_set_text(GTK_ENTRY(password), "fixture-only-private");
    gtk_box_pack_start(GTK_BOX(box), gtk_label_new("Ordinary entries and excluded password field"), FALSE, FALSE, 0);
    gtk_box_pack_start(GTK_BOX(box), first, FALSE, FALSE, 0);
    gtk_box_pack_start(GTK_BOX(box), second, FALSE, FALSE, 0);
    gtk_box_pack_start(GTK_BOX(box), password, FALSE, FALSE, 0);
    GtkWidget *view = gtk_text_view_new();
    gtk_buildable_set_name(GTK_BUILDABLE(view), "fixture-multiline");
    gtk_text_buffer_set_text(gtk_text_view_get_buffer(GTK_TEXT_VIEW(view)), "Before 😀\nPlease review this note.\nPlease send me the final version.\nAfter untouched", -1);
    gtk_widget_set_size_request(view, -1, 100);
    report_multiline = view;
    g_signal_connect(gtk_text_view_get_buffer(GTK_TEXT_VIEW(view)), "begin-user-action", G_CALLBACK(action_started), NULL);
    g_signal_connect(gtk_text_view_get_buffer(GTK_TEXT_VIEW(view)), "end-user-action", G_CALLBACK(action_finished), NULL);
    gtk_box_pack_start(GTK_BOX(box), view, FALSE, FALSE, 0);
    const char *names[] = {"fixture-tagged", "fixture-object", "fixture-readonly"};
    for (guint i = 0; i < G_N_ELEMENTS(excluded_views); i++) {
        GtkWidget *excluded = gtk_text_view_new();
        excluded_views[i] = excluded;
        gtk_buildable_set_name(GTK_BUILDABLE(excluded), names[i]);
        GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(excluded));
        gtk_text_buffer_set_text(buffer, "Excluded fixture content", -1);
        GtkTextIter first, last;
        gtk_text_buffer_get_bounds(buffer, &first, &last);
        if (i == 0) {
            GtkTextTag *tag = gtk_text_buffer_create_tag(buffer, "fixture-tag", "weight", PANGO_WEIGHT_BOLD, NULL);
            gtk_text_buffer_apply_tag(buffer, tag, &first, &last);
        } else if (i == 1) {
            GtkTextChildAnchor *anchor = gtk_text_buffer_create_child_anchor(buffer, &first);
            gtk_text_view_add_child_at_anchor(GTK_TEXT_VIEW(excluded), gtk_label_new("Embedded object"), anchor);
        } else gtk_text_view_set_editable(GTK_TEXT_VIEW(excluded), FALSE);
        gtk_widget_set_size_request(excluded, -1, 32);
        gtk_box_pack_start(GTK_BOX(box), excluded, FALSE, FALSE, 0);
    }

    g_signal_connect(window, "destroy", G_CALLBACK(gtk_main_quit), NULL);
    gtk_widget_show_all(window);
    if (g_getenv("TYPELAY_FIXTURE_FULLSCREEN")) gtk_window_fullscreen(GTK_WINDOW(window));
    gtk_widget_grab_focus(first);
    gtk_editable_set_position(GTK_EDITABLE(first), -1);
    if (g_getenv("TYPELAY_FIXTURE_TWO_WINDOWS")) {
        other_window = gtk_window_new(GTK_WINDOW_TOPLEVEL);
        gtk_window_set_title(GTK_WINDOW(other_window), "TypeLay GTK second window");
        gtk_window_set_default_size(GTK_WINDOW(other_window), 600, 300);
        GtkWidget *other_box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 16);
        gtk_container_set_border_width(GTK_CONTAINER(other_box), 32);
        other_entry = gtk_entry_new();
        gtk_buildable_set_name(GTK_BUILDABLE(other_entry), "fixture-other-window");
        gtk_entry_set_text(GTK_ENTRY(other_entry), "The other window is unchanged.");
        gtk_box_pack_start(GTK_BOX(other_box), other_entry, FALSE, FALSE, 0);
        gtk_container_add(GTK_CONTAINER(other_window), other_box);
        g_signal_connect(other_window, "destroy", G_CALLBACK(gtk_main_quit), NULL);
        gtk_widget_show_all(other_window);
    }
    report_path = g_getenv("TYPELAY_FIXTURE_STATE");
    if (report_path && g_path_is_absolute(report_path)) {
        report_window = window;
        report_first = first;
        report_second = second;
        watch_entry(first, &first_evidence);
        watch_entry(second, &second_evidence);
        g_timeout_add(25, report_state, NULL);
    }
    gtk_main();
    g_free(last_report);
    if (first_insert_context) json_object_unref(first_insert_context);
    if (first_delete_context) json_object_unref(first_delete_context);
    return 0;
}
