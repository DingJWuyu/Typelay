#pragma once
#include "gtk-target.h"

/* Presentation is parent-relative; product state and edit authorization stay in Runtime. */
void tl_overlay_prepare_fonts(void);
gboolean tl_overlay_contains(GtkWidget *widget);
gboolean tl_overlay_bindings_ready(Target *target);
/* Only this target's current inline form retires source acceptance readiness. */
gboolean tl_overlay_auxiliary_active(Target *target);
/* Only the current language search adds Return to the existing held table. */
gboolean tl_overlay_auxiliary_key_candidate(guint keyval);
/* Fresh physical down only, after dispatcher ownership and before the IME.
 * Owned refusal still goes to GTK once, but never to generic source actions. */
gboolean tl_overlay_auxiliary_key(guint keyval, GdkModifierType modifiers,
    guint keycode, guint64 sequence, gboolean *owned);
gboolean tl_overlay_can_return(Target *target);
void tl_overlay_hide(void);
void tl_overlay_forget(Target *target);
void tl_overlay_sync(void);
void tl_overlay_acceptance(void);
/* Called only for a fresh physical down; the module retains pair ownership. */
gboolean tl_overlay_key(gboolean accept, gboolean exit, gboolean control, guint keycode, guint64 sequence);
void tl_overlay_control_begin(guint keycode, guint64 sequence);
void tl_overlay_control_cancel(void);
/* The module owns physical presses; claim before a return/exit can move focus. */
gboolean tl_overlay_key_claim(guint keycode, guint64 sequence, guint control_keycode, guint64 control_sequence);
/* A configuration save claims only its original single search Return. */
gboolean tl_overlay_search_key_claim(guint keycode, guint64 sequence);
/* Read-only lookup of an existing original search Return, never a new down. */
gboolean tl_overlay_search_key_held(guint keycode);
JsonObject *tl_overlay_command(const char *operation, Target *target, JsonObject *data);

gboolean tl_glossary_transfer_begin(Target *target, JsonObject *identity, const char *phase);
gboolean tl_glossary_transfer_end(gboolean valid);
JsonObject *tl_overlay_glossary_return_basis(Target *target, JsonObject *request);
gboolean tl_overlay_glossary_return(Target *target, JsonObject *request, gboolean starting, gboolean ready);
JsonObject *tl_overlay_glossary_source(Target *target);
