#include <gtk/gtk.h>
#include <json-glib/json-glib.h>
#include "../../src/TypeLay.Linux/native/fcitx5/typelay-fcitx-v1.h"

/* Only the isolated test executable writes this evidence. Production has no
 * command to dump source text, inject input or change a test model. */
static GtkWidget *report_window, *report_first, *report_second, *other_window, *other_entry;
static const char *report_path;
static char *last_report;

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
    gtk_text_buffer_set_text(gtk_text_view_get_buffer(GTK_TEXT_VIEW(view)), "first line\nsecond line", -1);
    gtk_box_pack_start(GTK_BOX(box), view, TRUE, TRUE, 0);
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
        g_timeout_add(25, report_state, NULL);
    }
    gtk_main();
    g_free(last_report);
    return 0;
}
