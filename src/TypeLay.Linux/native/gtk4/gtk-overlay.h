#pragma once
#include "gtk-target.h"

/* Presentation is parent-relative; product state and edit authorization stay in Runtime. */
gboolean tl_overlay_contains(GtkWidget *widget);
gboolean tl_overlay_can_return(Target *target);
void tl_overlay_hide(void);
void tl_overlay_forget(Target *target);
void tl_overlay_sync(void);
void tl_overlay_acceptance(void);
/* Also called by the source controller to drain a held editor key after focus returns. */
gboolean tl_overlay_event(GdkEvent *event);
JsonObject *tl_overlay_command(const char *operation, Target *target, JsonObject *data);
