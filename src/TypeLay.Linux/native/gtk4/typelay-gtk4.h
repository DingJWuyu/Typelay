#pragma once
#include <gtk/gtk.h>
#include <gmodule.h>

G_BEGIN_DECLS
/* Called by the actual cooperating Fcitx GTK4 context after publishing its
 * binding. Loading this library alone never establishes source authority. */
G_MODULE_EXPORT void tl_gtk4_attach(GtkWidget *client_widget);
G_END_DECLS
