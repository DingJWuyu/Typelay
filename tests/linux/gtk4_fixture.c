#include <gtk/gtk.h>
#include <json-glib/json-glib.h>
#include "../../src/TypeLay.Linux/native/fcitx5/typelay-fcitx-v1.h"

/* Only the isolated test executable writes this evidence. Production has no
 * command to dump source text, inject input or change a test model. */
static GtkWidget *report_window, *report_first, *report_second, *other_window, *other_entry;
static GtkWidget *protected_fields[2], *report_multiline, *excluded_views[3];
static const char *report_path;
static char *last_report;
typedef struct {
    guint insertions, deletions, focus_in, focus_out;
} EntryEvidence;
static EntryEvidence first_evidence, second_evidence;
static JsonObject *first_insert_context, *first_delete_context;

static GtkWidget *entry_client(GtkWidget *widget) {
    return GTK_WIDGET(gtk_editable_get_delegate(GTK_EDITABLE(widget)));
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
    json_object_set_string_member(context, "secondText", gtk_editable_get_text(GTK_EDITABLE(report_second)));
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

static gboolean inspect_ime(GtkWidget *widget, TlFcitxStateV1 *state) {
    if (GTK_IS_ENTRY(widget)) widget = GTK_WIDGET(gtk_editable_get_delegate(GTK_EDITABLE(widget)));
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
    graphene_point_t origin = GRAPHENE_POINT_INIT(0, 0), point;
    if (gtk_widget_compute_point(widget, report_window, &origin, &point)) {
        json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, point.x);
        json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, point.y);
        json_builder_set_member_name(builder, "width");
        json_builder_add_int_value(builder, gtk_widget_get_width(widget));
        json_builder_set_member_name(builder, "height");
        json_builder_add_int_value(builder, gtk_widget_get_height(widget));
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
        && g_strcmp0(gtk_widget_get_name(widget), "typelay-candidate") == 0)
        *candidate = widget;
    else for (GtkWidget *child = gtk_widget_get_first_child(widget); child; child = gtk_widget_get_next_sibling(child))
        find_candidate(child, data);
}

static gboolean report_state(gpointer unused) {
    (void)unused;
    GtkWidget *active = other_window && gtk_window_is_active(GTK_WINDOW(other_window)) ? other_window : report_window;
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(active));
    GtkWidget *focus_field = focus;
    while (focus_field && !GTK_IS_ENTRY(focus_field) && !GTK_IS_TEXT_VIEW(focus_field) && !GTK_IS_PASSWORD_ENTRY(focus_field))
        focus_field = gtk_widget_get_parent(focus_field);
    JsonBuilder *builder = json_builder_new();
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "observedAtMs");
    json_builder_add_int_value(builder, g_get_monotonic_time() / 1000);
    json_builder_set_member_name(builder, "first");
    json_builder_add_string_value(builder, gtk_editable_get_text(GTK_EDITABLE(report_first)));
    json_builder_set_member_name(builder, "second");
    json_builder_add_string_value(builder, gtk_editable_get_text(GTK_EDITABLE(report_second)));
    if (other_entry) {
        json_builder_set_member_name(builder, "other");
        json_builder_add_string_value(builder, gtk_editable_get_text(GTK_EDITABLE(other_entry)));
    }
    json_builder_set_member_name(builder, "focus");
    json_builder_add_string_value(builder, focus_field ? gtk_widget_get_name(focus_field) : "");
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
            gtk_window_is_fullscreen(GTK_WINDOW(report_window)));
        json_builder_set_member_name(builder, "width");
        json_builder_add_int_value(builder, gtk_widget_get_width(report_window));
        json_builder_set_member_name(builder, "height");
        json_builder_add_int_value(builder, gtk_widget_get_height(report_window));
        json_builder_set_member_name(builder, "scale");
        json_builder_add_int_value(builder, gtk_widget_get_scale_factor(report_window));
        GtkWidget *candidate = NULL;
        find_candidate(report_window, &candidate);
        graphene_point_t origin = GRAPHENE_POINT_INIT(0, 0), point;
        json_builder_set_member_name(builder, "candidate");
        if (candidate && gtk_widget_get_mapped(candidate)
            && gtk_widget_compute_point(candidate, report_window, &origin, &point)) {
            json_builder_begin_object(builder);
            json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, point.x);
            json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, point.y);
            json_builder_set_member_name(builder, "width");
            json_builder_add_int_value(builder, gtk_widget_get_width(candidate));
            json_builder_set_member_name(builder, "height");
            json_builder_add_int_value(builder, gtk_widget_get_height(candidate));
            GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(candidate));
            GtkTextIter first, last;
            gtk_text_buffer_get_bounds(buffer, &first, &last);
            char *text = gtk_text_buffer_get_text(buffer, &first, &last, TRUE);
            json_builder_set_member_name(builder, "text"); json_builder_add_string_value(builder, text);
            g_free(text);
            json_builder_end_object(builder);
        } else json_builder_add_null_value(builder);
    }
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
    json_builder_set_member_name(builder, "canUndo"); json_builder_add_boolean_value(builder, gtk_text_buffer_get_can_undo(multiline));
    graphene_point_t origin = GRAPHENE_POINT_INIT(0, 0), point;
    if (gtk_widget_compute_point(report_multiline, report_window, &origin, &point)) {
        json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, point.x);
        json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, point.y);
    }
    json_builder_end_object(builder);
    json_builder_set_member_name(builder, "excludedTextViews");
    json_builder_begin_array(builder);
    for (guint i = 0; i < G_N_ELEMENTS(excluded_views); i++) {
        if (gtk_widget_compute_point(excluded_views[i], report_window, &origin, &point)) {
            json_builder_begin_object(builder);
            json_builder_set_member_name(builder, "name"); json_builder_add_string_value(builder, gtk_widget_get_name(excluded_views[i]));
            json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, point.x);
            json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, point.y);
            json_builder_end_object(builder);
        }
    }
    json_builder_end_array(builder);
    json_builder_set_member_name(builder, "protectedFields");
    json_builder_begin_array(builder);
    for (guint i = 0; i < G_N_ELEMENTS(protected_fields); i++) {
        graphene_point_t origin = GRAPHENE_POINT_INIT(0, 0), point;
        GtkWidget *field = protected_fields[i];
        if (field && gtk_widget_compute_point(field, report_window, &origin, &point)) {
            json_builder_begin_object(builder);
            json_builder_set_member_name(builder, "name");
            json_builder_add_string_value(builder, gtk_widget_get_name(field));
            json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, point.x);
            json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, point.y);
            json_builder_set_member_name(builder, "width"); json_builder_add_int_value(builder, gtk_widget_get_width(field));
            json_builder_set_member_name(builder, "height"); json_builder_add_int_value(builder, gtk_widget_get_height(field));
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
static gboolean close_fixture(GtkWindow *window, gpointer data) {
    (void)window; g_main_loop_quit(data); return FALSE;
}
int main(void) {
    gtk_init();
    GMainLoop *loop = g_main_loop_new(NULL, FALSE);
    GtkWidget *window = gtk_window_new();
    gtk_window_set_title(GTK_WINDOW(window), "TypeLay GTK4 contract fixture");
    gtk_window_set_default_size(GTK_WINDOW(window), 650, 480);
    GtkWidget *title = gtk_header_bar_new();
    gtk_header_bar_set_title_widget(GTK_HEADER_BAR(title), gtk_label_new("TypeLay input contracts"));
    gtk_header_bar_set_show_title_buttons(GTK_HEADER_BAR(title), TRUE);
    gtk_window_set_titlebar(GTK_WINDOW(window), title);
    GtkWidget *box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 16);
    gtk_widget_set_margin_start(box, 32); gtk_widget_set_margin_end(box, 32);
    gtk_widget_set_margin_top(box, 32); gtk_widget_set_margin_bottom(box, 32);
    gtk_window_set_child(GTK_WINDOW(window), box);
    GtkWidget *first = gtk_entry_new(), *second = gtk_entry_new(), *password = gtk_entry_new();
    gtk_widget_set_name(first, "fixture-first");
    gtk_widget_set_name(second, "fixture-second");
    gtk_widget_set_name(password, "fixture-password");
    const char *initial = g_getenv("TYPELAY_FIXTURE_INITIAL_TEXT");
    gtk_editable_set_text(GTK_EDITABLE(first), initial ? initial : "alpha 😀 omega");
    gtk_editable_set_text(GTK_EDITABLE(second), "second field");
    gtk_entry_set_visibility(GTK_ENTRY(password), FALSE);
    gtk_editable_set_text(GTK_EDITABLE(password), "fixture-only-private");
    gtk_box_append(GTK_BOX(box), gtk_label_new("Ordinary entries and excluded password field"));
    gtk_box_append(GTK_BOX(box), first);
    gtk_box_append(GTK_BOX(box), second);
    gtk_box_append(GTK_BOX(box), password);
    GtkWidget *password_entry = gtk_password_entry_new();
    gtk_widget_set_name(password_entry, "fixture-password-entry");
    gtk_editable_set_text(GTK_EDITABLE(password_entry), "synthetic-secret");
    gtk_box_append(GTK_BOX(box), password_entry);
    protected_fields[0] = password;
    protected_fields[1] = password_entry;
    GtkWidget *view = gtk_text_view_new();
    gtk_widget_set_name(view, "fixture-multiline");
    GtkTextBuffer *text_buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(view));
    gtk_text_buffer_set_enable_undo(text_buffer, TRUE);
    gtk_text_buffer_set_text(text_buffer, "Before 😀\nPlease review this note.\nPlease send me the final version.\nAfter untouched", -1);
    gtk_widget_set_size_request(view, -1, 100);
    report_multiline = view;
    gtk_box_append(GTK_BOX(box), view);
    const char *names[] = {"fixture-tagged", "fixture-object", "fixture-readonly"};
    for (guint i = 0; i < G_N_ELEMENTS(excluded_views); i++) {
        GtkWidget *excluded = gtk_text_view_new();
        excluded_views[i] = excluded;
        gtk_widget_set_name(excluded, names[i]);
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
        gtk_box_append(GTK_BOX(box), excluded);
    }
    g_signal_connect(window, "close-request", G_CALLBACK(close_fixture), loop);
    gtk_window_present(GTK_WINDOW(window));
    if (g_getenv("TYPELAY_FIXTURE_FULLSCREEN")) gtk_window_fullscreen(GTK_WINDOW(window));
    gtk_widget_grab_focus(first);
    gtk_editable_set_position(GTK_EDITABLE(first), -1);
    if (g_getenv("TYPELAY_FIXTURE_TWO_WINDOWS")) {
        other_window = gtk_window_new();
        gtk_window_set_title(GTK_WINDOW(other_window), "TypeLay GTK4 second window");
        gtk_window_set_default_size(GTK_WINDOW(other_window), 600, 300);
        GtkWidget *other_box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 16);
        gtk_widget_set_margin_start(other_box, 32); gtk_widget_set_margin_end(other_box, 32);
        other_entry = gtk_entry_new();
        gtk_widget_set_name(other_entry, "fixture-other-window");
        gtk_editable_set_text(GTK_EDITABLE(other_entry), "The other window is unchanged.");
        gtk_box_append(GTK_BOX(other_box), other_entry);
        gtk_window_set_child(GTK_WINDOW(other_window), other_box);
        g_signal_connect(other_window, "close-request", G_CALLBACK(close_fixture), loop);
        gtk_window_present(GTK_WINDOW(other_window));
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
    g_main_loop_run(loop);
    g_main_loop_unref(loop);
    g_free(last_report);
    if (first_insert_context) json_object_unref(first_insert_context);
    if (first_delete_context) json_object_unref(first_delete_context);
    return 0;
}
