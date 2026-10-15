#pragma once
#include "gtk-target.h"

/* Presentation is parent-relative; product state and edit authorization stay in Runtime. */
gboolean tl_overlay_contains(GtkWidget *widget);
gboolean tl_overlay_bindings_ready(Target *target);
void tl_overlay_hide(void);
void tl_overlay_forget(Target *target);
void tl_overlay_sync(void);
void tl_overlay_acceptance(void);
gboolean tl_overlay_key(guint keyval, GdkModifierType state);
JsonObject *tl_overlay_command(const char *operation, Target *target, JsonObject *data);

/* Exact, already-proved editor family; no source/provider observation. */
gboolean tl_overlay_private_return(Target *target, JsonObject *proof, gboolean starting);
