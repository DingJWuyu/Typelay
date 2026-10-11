#include <gtk/gtk.h>

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
    gtk_main();
    return 0;
}
