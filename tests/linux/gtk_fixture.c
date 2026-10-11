#include <gtk/gtk.h>
#include <json-glib/json-glib.h>

/* Only the isolated test executable writes this evidence. Production has no
 * command to dump source text, inject input or change a test model. */
static GtkWidget *report_window, *report_first, *report_second;
static const char *report_path;
static char *last_report;

static gboolean report_state(gpointer unused) {
    (void)unused;
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(report_window));
    JsonBuilder *builder = json_builder_new();
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "first");
    json_builder_add_string_value(builder, gtk_entry_get_text(GTK_ENTRY(report_first)));
    json_builder_set_member_name(builder, "second");
    json_builder_add_string_value(builder, gtk_entry_get_text(GTK_ENTRY(report_second)));
    json_builder_set_member_name(builder, "focus");
    json_builder_add_string_value(builder, focus ? gtk_buildable_get_name(GTK_BUILDABLE(focus)) : "");
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
    GtkWidget *first = gtk_entry_new(), *second = gtk_entry_new(), *password = gtk_entry_new();
    gtk_buildable_set_name(GTK_BUILDABLE(first), "fixture-first");
    gtk_buildable_set_name(GTK_BUILDABLE(second), "fixture-second");
    gtk_buildable_set_name(GTK_BUILDABLE(password), "fixture-password");
    gtk_entry_set_text(GTK_ENTRY(first), "alpha 😀 omega");
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
    gtk_widget_grab_focus(first);
    gtk_editable_set_position(GTK_EDITABLE(first), -1);
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
