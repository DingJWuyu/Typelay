#include "gtk-overlay.h"
#include <string.h>
#include <math.h>
#include "../common/overlay-design.h"

void tl_overlay_prepare_fonts(void) { (void)tl_overlay_font_map(); }

/* One source-bound popover per application, never another toplevel or an input grab.
 * This is deliberately in the GTK thread: niri does not publish tiled-window origins.
 * GTK owns the parent surface, clipping, scaling and movement of its own popover. */
typedef struct {
    JsonArray *choices;
    char *saved, *label;
} LanguageMenu;


/* Freeze auxiliary values at the storage boundary. Changing GtkEditable's
 * editable/sensitive state resets its IM context after focus-out, destroying
 * the two real Clear observations that authorize Save. Buffer guards also
 * cover delayed paste, drop, accessibility edits and native undo. */
typedef struct { GtkEntryBuffer parent_instance; gboolean locked; } TlGtk4GlossaryBuffer;
typedef GtkEntryBufferClass TlGtk4GlossaryBufferClass;
G_DEFINE_TYPE(TlGtk4GlossaryBuffer, tl_gtk4_glossary_buffer, GTK_TYPE_ENTRY_BUFFER)
static guint glossary_buffer_insert(GtkEntryBuffer *buffer, guint position, const gchar *chars, guint count) {
    if (((TlGtk4GlossaryBuffer *)buffer)->locked) return 0;
    return GTK_ENTRY_BUFFER_CLASS(tl_gtk4_glossary_buffer_parent_class)->insert_text(buffer, position, chars, count);
}
static guint glossary_buffer_delete(GtkEntryBuffer *buffer, guint position, guint count) {
    if (((TlGtk4GlossaryBuffer *)buffer)->locked) return 0;
    return GTK_ENTRY_BUFFER_CLASS(tl_gtk4_glossary_buffer_parent_class)->delete_text(buffer, position, count);
}
static void tl_gtk4_glossary_buffer_class_init(TlGtk4GlossaryBufferClass *klass) {
    klass->insert_text = glossary_buffer_insert; klass->delete_text = glossary_buffer_delete;
}
static void tl_gtk4_glossary_buffer_init(TlGtk4GlossaryBuffer *buffer) { buffer->locked = FALSE; }

/* Auxiliary inputs belong to the overlay family, but are never source targets
 * or translation editors. Their identity and IME proofs live only for this form. */
typedef struct {
    guint refs, selected_direction;
    GtkWidget *box, *terms[2], *clients[2], *scope, *save, *cancel, *feedback;
    GtkWidget *scope_popup, *scope_choices;
    GPtrArray *scope_options;
    TlGtk4GlossaryBuffer *buffers[2]; /* owned by the corresponding entry */
    GtkIMContext *contexts[2]; /* weak provider-owned bindings */
    gulong handlers[2];
    JsonObject *token, *source_authority, *source_view;
    JsonArray *directions;
    char *id, *target, *generation, *origin_state;
    Target *origin;
    GtkWidget *origin_top, *origin_client;
    gpointer origin_buffer;
    gboolean source_origin, return_started, returned;
    guint64 returned_focus;
    gint64 revision, settings_revision;
    gboolean clear[2], composing[2], probing, pending, finished, closing;
    gint reported_ime;
} GlossaryForm;

static struct {
    Target *target;
    GtkWidget *popover, *top, *card, *header, *language, *status, *scroll, *text;
    GtkWidget *body, *skeleton, *skeleton_short, *dot;
    GtkWidget *more, *more_popup;
    JsonObject *more_view;
    GtkWidget *footer, *hint, *actions, *expand, *menu, *source, *destination, *resume, *exit, *apply_languages;
    GtkWidget *style_label, *style_inherit, *style_natural, *style_formal, *alternative, *action_feedback, *glossary_action;
    GlossaryForm *glossary;
    JsonObject *glossary_draft_token;
    GtkTextBuffer *glossary_draft_buffer;
    gboolean pending_glossary, language_focus_transfer;
    LanguageMenu source_languages, target_languages;
    JsonObject *menu_token;
    char *menu_target, *menu_generation;
    gint64 menu_revision, menu_settings_revision;
    GtkCssProvider *css;
    GdkDisplay *display;
    GtkEventController *language_pointer;
    JsonObject *view, *authority;
    GdkFrameClock *layout_clock;
    GtkWidget *layout_popover;
    JsonObject *layout_view;
    gint64 layout_revision;
    gulong focus_handler, active_handler, layout_handler;
    gboolean setting_text, setting_languages, composing, focused, expanded, pending_edit, pending_resume;
    gboolean english, reduce_motion;
    gint text_size;
    char *generation;
    gint edit_offset;
    gint64 revision, expires;
    guint expiry_timer;
} overlay;

/* Lives outside overlay: synchronous destruction must not erase failure. */
typedef struct {
    GtkWidget *top, *destination;
    guint emissions;
    gboolean failed, destination_seen;
} GlossaryHandoff;
static GlossaryHandoff *glossary_handoff;
static gboolean glossary_can_return(Target *target);
static void hide_overlay(const char *reason);
static void language_configuration_close(gboolean notify);
static void begin_language_configuration(GtkWidget *trigger, JsonObject *view);

/* Only a local editor command, never a source write permit. Borrowed identities
 * are compared while the current overlay owns them, never dereferenced later. */
typedef struct {
    Target *target;
    GtkWidget *editor;
    GtkTextBuffer *buffer;
    gpointer context;
    JsonObject *view, *token;
    guint keycode;
    guint64 sequence, serial;
    gint64 revision, expires;
} EditorControl;
static EditorControl editor_control;
static guint64 editor_control_serial;

void tl_overlay_control_cancel(void) {
    memset(&editor_control, 0, sizeof editor_control);
}

static gboolean boolean(JsonObject *object, const char *key) {
    JsonNode *node = object ? json_object_get_member(object, key) : NULL;
    return node && JSON_NODE_HOLDS_VALUE(node) && json_node_get_value_type(node) == G_TYPE_BOOLEAN
        && json_node_get_boolean(node);
}
static const char *text_or_empty(JsonObject *object, const char *key) {
    const char *text = tl_string(object, key);
    return text ? text : "";
}
/* Unknown or incomplete action state cannot open a new product entry. The host
 * owns release and capability decisions; this adapter only validates the wire. */
static JsonObject *translation_actions(JsonObject *view) {
    JsonObject *state = tl_object(tl_object(view, "viewContext"), "translationActions");
    const char *flags[] = {"styleReleased", "canChangeStyle", "canInheritStyle",
        "alternativeReleased", "canRequestAlternative"};
    const char *nullable[] = {"applicationStyle", "styleUnavailableReason", "inheritUnavailableReason",
        "alternativeUnavailableReason", "feedback"};
    for (guint index = 0; index < G_N_ELEMENTS(flags); index++) {
        JsonNode *node = state ? json_object_get_member(state, flags[index]) : NULL;
        if (!node || !JSON_NODE_HOLDS_VALUE(node) || json_node_get_value_type(node) != G_TYPE_BOOLEAN) return NULL;
    }
    for (guint index = 0; index < G_N_ELEMENTS(nullable); index++) {
        JsonNode *node = json_object_get_member(state, nullable[index]);
        if (!node || (!JSON_NODE_HOLDS_NULL(node)
            && (!JSON_NODE_HOLDS_VALUE(node) || json_node_get_value_type(node) != G_TYPE_STRING))) return NULL;
    }
    /* Legacy action states predate glossary. All three fields may be absent,
     * but a partial or malformed new contract must never open the entry. */
    if (json_object_has_member(state, "glossaryReleased") || json_object_has_member(state, "canOpenGlossary")
        || json_object_has_member(state, "glossaryUnavailableReason")) {
        const char *glossary_flags[] = {"glossaryReleased", "canOpenGlossary"};
        for (guint index = 0; index < G_N_ELEMENTS(glossary_flags); index++) {
            JsonNode *node = json_object_get_member(state, glossary_flags[index]);
            if (!node || !JSON_NODE_HOLDS_VALUE(node) || json_node_get_value_type(node) != G_TYPE_BOOLEAN) return NULL;
        }
        JsonNode *reason = json_object_get_member(state, "glossaryUnavailableReason");
        if (!reason || (!JSON_NODE_HOLDS_NULL(reason)
            && (!JSON_NODE_HOLDS_VALUE(reason) || json_node_get_value_type(reason) != G_TYPE_STRING))) return NULL;
    }
    JsonNode *revision = json_object_get_member(state, "settingsRevision");
    if (!revision || !JSON_NODE_HOLDS_VALUE(revision) || json_node_get_value_type(revision) != G_TYPE_INT64
        || json_node_get_int(revision) < 0) return NULL;
    const char *style = tl_string(state, "applicationStyle");
    return !style || strcmp(style, "Natural") == 0 || strcmp(style, "Formal") == 0 ? state : NULL;
}
static void clear_translation_menu(void) {
    if (overlay.menu_token) json_object_unref(overlay.menu_token);
    g_free(overlay.menu_target); g_free(overlay.menu_generation);
    overlay.menu_token = NULL;
    overlay.menu_target = overlay.menu_generation = NULL;
    overlay.menu_revision = overlay.menu_settings_revision = 0;
}
static void close_more(gboolean awaiting_glossary) {
    GtkWidget *popup = overlay.more_popup;
    JsonObject *view = overlay.more_view;
    /* Retire this interaction before GTK destroy callbacks. A retained old
     * button can never dispatch against a later opening's view or token. */
    overlay.more_popup = NULL;
    overlay.more_view = NULL;
    overlay.style_label = overlay.style_inherit = overlay.style_natural = overlay.style_formal = NULL;
    overlay.alternative = overlay.glossary_action = overlay.action_feedback = NULL;
    if (!awaiting_glossary) {
        overlay.pending_glossary = FALSE;
        clear_translation_menu();
    }
    if (popup) {
        gtk_popover_popdown(GTK_POPOVER(popup));
        if (gtk_widget_get_parent(popup)) gtk_widget_unparent(popup);
        g_object_unref(popup);
    }
    if (view) json_object_unref(view);
}
static void freeze_translation_menu(JsonObject *view) {
    overlay.pending_glossary = FALSE;
    clear_translation_menu();
    JsonObject *state = translation_actions(view);
    JsonObject *token = tl_object(tl_object(view, "viewContext"), "token");
    if (!state || !token || !overlay.target || !tl_view_context_matches(overlay.target, view)) return;
    overlay.menu_token = tl_copy(token);
    overlay.menu_target = g_strdup(overlay.target->id);
    overlay.menu_generation = g_strdup(overlay.generation);
    overlay.menu_revision = overlay.revision;
    overlay.menu_settings_revision = tl_integer(state, "settingsRevision", -1);
}
static gboolean translation_menu_current(void) {
    JsonObject *state = translation_actions(overlay.view);
    JsonObject *token = tl_object(tl_object(overlay.view, "viewContext"), "token");
    return overlay.menu_token && token && state && overlay.target && !overlay.target->dead
        && g_strcmp0(overlay.target->id, overlay.menu_target) == 0
        && g_strcmp0(overlay.generation, overlay.menu_generation) == 0
        && overlay.revision == overlay.menu_revision
        && tl_integer(state, "settingsRevision", -1) == overlay.menu_settings_revision
        && json_object_equal(token, overlay.menu_token)
        && tl_configuration_current(overlay.menu_generation)
        && tl_view_context_matches(overlay.target, overlay.view) && tl_now() <= overlay.expires;
}
static gboolean translation_render_current(JsonObject *view, JsonObject *context) {
    return overlay.view == view && tl_object(view, "viewContext") == context;
}
/* Only the new menu projection may change without retiring an editor key ticket.
 * A duplicate render or any old view-field change keeps the original full path. */
static gboolean translation_actions_only(JsonObject *previous, JsonObject *next) {
    JsonObject *old_context = tl_object(previous, "viewContext"), *new_context = tl_object(next, "viewContext");
    if (!old_context || !new_context || json_object_equal(previous, next)) return FALSE;
    JsonObject *old_view = tl_copy(previous), *new_view = tl_copy(next);
    JsonObject *old_without = tl_copy(old_context), *new_without = tl_copy(new_context);
    json_object_remove_member(old_without, "translationActions");
    json_object_remove_member(new_without, "translationActions");
    json_object_set_object_member(old_view, "viewContext", old_without);
    json_object_set_object_member(new_view, "viewContext", new_without);
    gboolean equal = json_object_equal(old_view, new_view);
    json_object_unref(old_view); json_object_unref(new_view);
    return equal;
}
static void more_button_label(GtkWidget *button, const char *label) {
    GtkWidget *child = g_object_get_data(G_OBJECT(button), "typelay-more-label");
    const char *current = child ? gtk_label_get_text(GTK_LABEL(child)) : gtk_button_get_label(GTK_BUTTON(button));
    if (g_strcmp0(current, label) == 0) return;
    if (child) gtk_label_set_text(GTK_LABEL(child), label);
    else gtk_button_set_label(GTK_BUTTON(button), label);
    gtk_accessible_update_property(GTK_ACCESSIBLE(button), GTK_ACCESSIBLE_PROPERTY_LABEL, label, -1);
}
static gboolean more_render_current(JsonObject *view, JsonObject *context, GtkWidget *popup) {
    return translation_render_current(view, context) && overlay.more_popup == popup && overlay.more_view == view;
}
static gboolean render_translation_actions(JsonObject *view) {
    if (!overlay.more_popup) return overlay.view == view;
    GtkWidget *popup = g_object_ref(overlay.more_popup);
    JsonObject *state = translation_actions(view), *context = tl_object(view, "viewContext");
    if (state) json_object_ref(state);
    if (context) json_object_ref(context);
    const char *style = tl_string(state, "applicationStyle"), *feedback = text_or_empty(state, "feedback");
    gboolean released = boolean(state, "styleReleased"), current = translation_menu_current();
    gboolean change = current && released && boolean(state, "canChangeStyle");
    GtkWidget *widgets[] = {overlay.style_label, overlay.style_inherit, overlay.style_natural,
        overlay.style_formal, overlay.alternative, overlay.glossary_action, overlay.action_feedback};
    const char *labels[] = {NULL, state && !style ? "✓ 继承" : "继承", "自然", "正式", "换种说法", "加入术语表", feedback};
    gboolean visible[] = {TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, *feedback != '\0'};
    gboolean enabled[] = {TRUE, current && boolean(state, "canInheritStyle"), change, change,
        current && boolean(state, "alternativeReleased") && boolean(state, "canRequestAlternative"),
        current && boolean(state, "canOpenGlossary"), TRUE};
    const char *reasons[] = {NULL, tl_string(state, "inheritUnavailableReason"),
        tl_string(state, "styleUnavailableReason"), tl_string(state, "styleUnavailableReason"),
        tl_string(state, "alternativeUnavailableReason"), tl_string(state, "glossaryUnavailableReason"), NULL};
    /* Every GTK setter can dispatch callbacks. Keep widgets and JSON alive and
     * stop immediately if a callback replaces this rendered view. */
    json_object_ref(view);
    for (guint index = 0; index < G_N_ELEMENTS(widgets); index++) g_object_ref(widgets[index]);
    for (guint index = 0; index < G_N_ELEMENTS(widgets) && more_render_current(view, context, popup); index++) {
        GtkWidget *widget = widgets[index];
        if (labels[index]) {
            const char *label = tl_overlay_ui(labels[index], overlay.english);
            if (GTK_IS_LABEL(widget)) gtk_label_set_text(GTK_LABEL(widget), label);
            else more_button_label(widget, label);
        }
        if (!more_render_current(view, context, popup)) break;
        gtk_widget_set_sensitive(widget, enabled[index]);
        if (!more_render_current(view, context, popup)) break;
        gtk_widget_set_tooltip_text(widget, enabled[index] ? NULL : reasons[index] ? reasons[index]
            : overlay.english ? "Not available yet" : "暂不可用");
        if (!more_render_current(view, context, popup)) break;
        gtk_widget_set_visible(widget, visible[index]);
    }
    gboolean retained = more_render_current(view, context, popup);
    if (retained) {
        GtkWidget *styles[] = {widgets[2], widgets[3]};
        const char *names[] = {"Natural", "Formal"};
        for (guint index = 0; index < G_N_ELEMENTS(styles) && more_render_current(view, context, popup); index++) {
            const char *effective = tl_string(state, "effectiveStyle");
            if (g_strcmp0(effective, names[index]) == 0) gtk_widget_add_css_class(styles[index], "selected");
            else gtk_widget_remove_css_class(styles[index], "selected");
        }
        retained = more_render_current(view, context, popup);
    }
    for (guint index = 0; index < G_N_ELEMENTS(widgets); index++) g_object_unref(widgets[index]);
    json_object_unref(view);
    if (context) json_object_unref(context);
    if (state) json_object_unref(state);
    g_object_unref(popup);
    return retained;
}
static gboolean language_text(const char *text, glong maximum) {
    if (!text || !*text || !g_utf8_validate(text, -1, NULL)) return FALSE;
    glong length = 0;
    for (const char *cursor = text; *cursor; cursor = g_utf8_next_char(cursor)) {
        length += g_utf8_get_char(cursor) > 0xffff ? 2 : 1;
        if (length > maximum) return FALSE;
    }
    return TRUE;
}
/* Keep only the bounded wire contract. Extra JSON members are not model changes. */
static JsonArray *language_choices(JsonObject *view, const char *side) {
    JsonObject *choices = tl_object(view, "languageChoices");
    JsonNode *node = choices ? json_object_get_member(choices, side) : NULL;
    if (!node || !JSON_NODE_HOLDS_ARRAY(node)) return NULL;
    JsonArray *input = json_node_get_array(node);
    guint count = json_array_get_length(input);
    if (!count || count > 32) return NULL;
    JsonArray *result = json_array_new();
    for (guint index = 0; index < count; index++) {
        JsonNode *item = json_array_get_element(input, index);
        JsonObject *choice = item && JSON_NODE_HOLDS_OBJECT(item) ? json_node_get_object(item) : NULL;
        const char *code = tl_string(choice, "code"), *label = tl_string(choice, "label");
        if (!language_text(code, 16) || !language_text(label, 80)) goto invalid;
        for (guint previous = 0; previous < index; previous++)
            if (g_strcmp0(code, tl_string(json_array_get_object_element(result, previous), "code")) == 0)
                goto invalid;
        JsonObject *entry = json_object_new();
        json_object_set_string_member(entry, "code", code);
        json_object_set_string_member(entry, "label", label);
        json_array_add_object_element(result, entry);
    }
    return result;
invalid:
    json_array_unref(result);
    return NULL;
}
static gint language_index(JsonArray *choices, const char *code) {
    if (choices && code) for (guint index = 0; index < json_array_get_length(choices); index++)
        if (g_strcmp0(code, tl_string(json_array_get_object_element(choices, index), "code")) == 0)
            return (gint)index;
    return -1;
}
static char *language_title(JsonObject *view, const char *key, const char *code, JsonArray *choices) {
    const char *label = tl_string(view, key);
    if (language_text(label, 80)) return g_strdup(label);
    gint index = language_index(choices, code);
    if (index >= 0) return g_strdup(tl_string(json_array_get_object_element(choices, (guint)index), "label"));
    return g_strdup_printf("%s（当前不可用）", code);
}
static void clear_language_menu(LanguageMenu *menu) {
    if (menu->choices) json_array_unref(menu->choices);
    g_free(menu->saved); g_free(menu->label);
    memset(menu, 0, sizeof *menu);
}
/* The stock GtkDropDown uses an autohide popup whose native grab deactivates
 * this source window on Wayland. Keep language navigation in a nonmodal child
 * instead of exempting a menu from our active-window safety boundary. */
static GtkListView *language_list(GtkWidget *button) {
    GtkPopover *popup = gtk_menu_button_get_popover(GTK_MENU_BUTTON(button));
    return GTK_LIST_VIEW(gtk_scrolled_window_get_child(GTK_SCROLLED_WINDOW(gtk_popover_get_child(popup))));
}
static GtkSingleSelection *language_selection(GtkWidget *button) {
    return GTK_SINGLE_SELECTION(gtk_list_view_get_model(language_list(button)));
}
static const char *selected_language(GtkWidget *widget, LanguageMenu *menu) {
    guint index = gtk_single_selection_get_selected(language_selection(widget));
    return menu->choices && index < json_array_get_length(menu->choices)
        ? tl_string(json_array_get_object_element(menu->choices, index), "code") : NULL;
}
static void select_language(GtkWidget *widget, LanguageMenu *menu, const char *code) {
    gint index = language_index(menu->choices, code);
    guint selected = index >= 0 ? (guint)index
        : menu->choices ? json_array_get_length(menu->choices) : 0;
    gtk_single_selection_set_selected(language_selection(widget), selected);
}
static gboolean language_menu_current(GtkWidget *widget, LanguageMenu *menu, JsonObject *view) {
    return overlay.view == view && overlay.popover
        && ((widget == overlay.source && menu == &overlay.source_languages)
            || (widget == overlay.destination && menu == &overlay.target_languages));
}
static void refresh_language_apply(void) {
    if (overlay.apply_languages)
        gtk_widget_set_sensitive(overlay.apply_languages, !overlay.setting_languages
            && selected_language(overlay.source, &overlay.source_languages)
            && selected_language(overlay.destination, &overlay.target_languages));
}
static gboolean active(void);
static gboolean language_focus_current(void);
static gboolean language_handoff_current(JsonObject *view) {
    return overlay.view == view && overlay.popover && overlay.menu && overlay.language
        && !overlay.glossary && active() && tl_now() <= overlay.expires
        && gtk_widget_get_mapped(overlay.language)
        && tl_configuration_current(overlay.generation)
        && tl_view_context_matches(overlay.target, view)
        && tl_metadata_matches(overlay.target, overlay.authority);
}
static void focus_language_trigger(JsonObject *view) {
    if (overlay.language_focus_transfer || !language_handoff_current(view) || !language_focus_current()) return;
    json_object_ref(view);
    GtkWidget *trigger = g_object_ref(overlay.language);
    /* The source's ordinary Tab must skip a preview. Only an existing internal
     * language interaction temporarily makes this return destination focusable. */
    overlay.language_focus_transfer = TRUE;
    gtk_widget_set_focusable(trigger, TRUE);
    if (overlay.language == trigger && language_handoff_current(view) && language_focus_current())
        gtk_widget_grab_focus(trigger);
    if (overlay.language == trigger) overlay.language_focus_transfer = FALSE;
    if (overlay.language != trigger || !overlay.top
        || gtk_window_get_focus(GTK_WINDOW(overlay.top)) != trigger)
        gtk_widget_set_focusable(trigger, FALSE);
    g_object_unref(trigger);
    json_object_unref(view);
}
static void close_language_menu(JsonObject *view) {
    GtkWidget *menu = overlay.menu;
    /* Only a live internal language interaction hands focus to the trigger.
     * Outside destinations and expired views never borrow this transition. */
    if (language_handoff_current(view) && gtk_widget_get_mapped(menu) && language_focus_current())
        focus_language_trigger(view);
    if (overlay.view == view && overlay.menu == menu && menu) gtk_widget_set_visible(menu, FALSE);
}
static gboolean language_menu_key(GtkEventControllerKey *keys, guint keyval, guint keycode,
    GdkModifierType modifiers, gpointer data) {
    (void)keys; (void)keycode; (void)data;
    modifiers &= GDK_SHIFT_MASK | GDK_CONTROL_MASK | GDK_ALT_MASK
        | GDK_SUPER_MASK | GDK_HYPER_MASK | GDK_META_MASK;
    if (keyval != GDK_KEY_Escape || modifiers || !overlay.menu
        || !gtk_widget_get_mapped(overlay.menu) || !language_focus_current()
        || !language_handoff_current(overlay.view)) return FALSE;
    close_language_menu(overlay.view);
    return TRUE;
}
static void language_selection_changed(GObject *selection, GParamSpec *spec, gpointer data) {
    (void)spec;
    GtkWidget *button = data;
    if (!overlay.popover || (button != overlay.source && button != overlay.destination)) return;
    GObject *item = gtk_single_selection_get_selected_item(GTK_SINGLE_SELECTION(selection));
    /* GtkMenuButton.set_label rebuilds its hit-test children on every call.
     * Keep the public custom label stable while the pointer stays on this button. */
    GtkWidget *label = gtk_menu_button_get_child(GTK_MENU_BUTTON(button));
    gtk_label_set_text(GTK_LABEL(label), item ? gtk_string_object_get_string(GTK_STRING_OBJECT(item)) : "");
    refresh_language_apply();
}
static void language_item_setup(GtkSignalListItemFactory *factory, GtkListItem *item, gpointer data) {
    (void)factory; (void)data;
    GtkWidget *label = gtk_label_new("");
    gtk_label_set_xalign(GTK_LABEL(label), 0);
    gtk_widget_set_margin_start(label, 8); gtk_widget_set_margin_end(label, 8);
    gtk_widget_set_margin_top(label, 6); gtk_widget_set_margin_bottom(label, 6);
    gtk_list_item_set_child(item, label);
}
static void language_item_bind(GtkSignalListItemFactory *factory, GtkListItem *item, gpointer data) {
    (void)factory; (void)data;
    gtk_label_set_text(GTK_LABEL(gtk_list_item_get_child(item)),
        gtk_string_object_get_string(GTK_STRING_OBJECT(gtk_list_item_get_item(item))));
}
static void language_popup_mapped(GtkWidget *popup, gpointer data) {
    GtkWidget *button = data;
    if ((button != overlay.source && button != overlay.destination)
        || !language_handoff_current(overlay.view) || !gtk_widget_get_mapped(popup)
        || !gtk_menu_button_get_active(GTK_MENU_BUTTON(button))
        || GTK_WIDGET(gtk_menu_button_get_popover(GTK_MENU_BUTTON(button))) != popup) return;
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(overlay.top));
    if (!focus || !gtk_widget_get_mapped(focus)
        || (focus != button && !gtk_widget_is_ancestor(focus, button))) return;
    g_object_ref(button);
    g_object_ref(popup);
    JsonObject *view = json_object_ref(overlay.view);
    GtkListView *list = g_object_ref(language_list(button));
    guint selected = gtk_single_selection_get_selected(language_selection(button));
    if (selected != GTK_INVALID_LIST_POSITION) {
        /* GTK 4.22 scroll_to updates the focus tracker, but only grabs an item
         * when focus is already inside the list. Enter this user-opened popup
         * explicitly; otherwise Home/Down still navigate from its button. */
        gtk_list_view_scroll_to(list, selected, GTK_LIST_SCROLL_FOCUS, NULL);
        if ((button == overlay.source || button == overlay.destination)
            && language_handoff_current(view) && gtk_widget_get_mapped(popup)
            && gtk_menu_button_get_active(GTK_MENU_BUTTON(button))
            && language_list(button) == list) {
            focus = gtk_window_get_focus(GTK_WINDOW(overlay.top));
            if (focus && gtk_widget_get_mapped(focus)
                && (focus == button || gtk_widget_is_ancestor(focus, button)))
                gtk_widget_grab_focus(GTK_WIDGET(list));
        }
    }
    g_object_unref(list);
    json_object_unref(view);
    g_object_unref(popup);
    g_object_unref(button);
}
static void close_language_popup(GtkWidget *widget) {
    GtkWidget *button = g_object_ref(widget);
    if (active() && (button == overlay.source || button == overlay.destination)
        && gtk_menu_button_get_active(GTK_MENU_BUTTON(button))) {
        /* Hand focus back within the same family before hiding the child. */
        gtk_widget_grab_focus(button);
        if (button == overlay.source || button == overlay.destination)
            gtk_menu_button_popdown(GTK_MENU_BUTTON(button));
    }
    g_object_unref(button);
}
static void language_row_activated(GtkListView *list, guint position, gpointer data) {
    GtkWidget *button = g_object_ref(data);
    if (active() && (button == overlay.source || button == overlay.destination)
        && list == language_list(button) && gtk_menu_button_get_active(GTK_MENU_BUTTON(button))) {
        GtkSingleSelection *selection = language_selection(button);
        if (position < g_list_model_get_n_items(G_LIST_MODEL(selection))) {
            /* activate's position identifies the chosen row; keyboard activation
             * need not have changed GtkSingleSelection beforehand. */
            gtk_single_selection_set_selected(selection, position);
            close_language_popup(button);
        }
    }
    g_object_unref(button);
}
static gboolean language_popup_key(GtkEventControllerKey *keys, guint keyval, guint keycode,
    GdkModifierType modifiers, gpointer data) {
    (void)keys; (void)keycode;
    modifiers &= GDK_SHIFT_MASK | GDK_CONTROL_MASK | GDK_ALT_MASK
        | GDK_SUPER_MASK | GDK_HYPER_MASK | GDK_META_MASK;
    if (keyval != GDK_KEY_Escape || modifiers) return FALSE;
    close_language_popup(data);
    return TRUE;
}
static GtkWidget *language_button(void) {
    GtkWidget *button = gtk_menu_button_new(), *popup = gtk_popover_new();
    GtkWidget *label = gtk_label_new("");
    gtk_widget_set_hexpand(label, TRUE);
    gtk_menu_button_set_child(GTK_MENU_BUTTON(button), label);
    gtk_menu_button_set_always_show_arrow(GTK_MENU_BUTTON(button), TRUE);
    gtk_accessible_update_relation(GTK_ACCESSIBLE(button), GTK_ACCESSIBLE_RELATION_LABELLED_BY, label, NULL, -1);
    gtk_popover_set_autohide(GTK_POPOVER(popup), FALSE);
    g_object_set_data(G_OBJECT(popup), "typelay-overlay", GINT_TO_POINTER(1));
    GtkSingleSelection *selection = gtk_single_selection_new(G_LIST_MODEL(gtk_string_list_new(NULL)));
    GtkListItemFactory *factory = gtk_signal_list_item_factory_new();
    g_signal_connect(factory, "setup", G_CALLBACK(language_item_setup), NULL);
    g_signal_connect(factory, "bind", G_CALLBACK(language_item_bind), NULL);
    GtkWidget *list = gtk_list_view_new(GTK_SELECTION_MODEL(selection), factory);
    gtk_list_view_set_single_click_activate(GTK_LIST_VIEW(list), TRUE);
    GtkWidget *scroll = gtk_scrolled_window_new();
    gtk_widget_set_focusable(scroll, FALSE);
    gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(scroll), GTK_POLICY_NEVER, GTK_POLICY_AUTOMATIC);
    gtk_scrolled_window_set_max_content_height(GTK_SCROLLED_WINDOW(scroll), 240);
    gtk_scrolled_window_set_propagate_natural_height(GTK_SCROLLED_WINDOW(scroll), TRUE);
    gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(scroll), list);
    gtk_popover_set_child(GTK_POPOVER(popup), scroll);
    gtk_menu_button_set_popover(GTK_MENU_BUTTON(button), popup);
    g_signal_connect_object(selection, "notify::selected", G_CALLBACK(language_selection_changed), button, 0);
    g_signal_connect_object(list, "activate", G_CALLBACK(language_row_activated), button, 0);
    g_signal_connect_object(popup, "map", G_CALLBACK(language_popup_mapped), button, G_CONNECT_AFTER);
    GtkEventController *keys = gtk_event_controller_key_new();
    gtk_event_controller_set_propagation_phase(keys, GTK_PHASE_CAPTURE);
    g_signal_connect_object(keys, "key-pressed", G_CALLBACK(language_popup_key), button, 0);
    gtk_widget_add_controller(popup, keys);
    return button;
}
static gboolean sync_language(GtkWidget *widget, LanguageMenu *menu, JsonArray *choices,
    const char *saved, const char *label, gboolean reset, JsonObject *view) {
    gboolean saved_changed = g_strcmp0(menu->saved, saved) != 0;
    gboolean choices_changed = (!menu->choices != !choices)
        || (choices && menu->choices && !json_array_equal(menu->choices, choices));
    gboolean missing = language_index(choices, saved) < 0;
    gboolean was_missing = language_index(menu->choices, menu->saved) < 0;
    gboolean rebuild = !menu->saved || choices_changed || missing != was_missing
        || (missing && (saved_changed || g_strcmp0(menu->label, label) != 0));
    char *selection = g_strdup(selected_language(widget, menu));
    clear_language_menu(menu);
    menu->choices = choices ? json_array_ref(choices) : NULL;
    menu->saved = g_strdup(saved); menu->label = g_strdup(label);
    if (rebuild) {
        GtkStringList *model = gtk_string_list_new(NULL);
        if (choices) for (guint index = 0; index < json_array_get_length(choices); index++)
            gtk_string_list_append(model, tl_string(json_array_get_object_element(choices, index), "label"));
        if (missing) gtk_string_list_append(model, label);
        gtk_single_selection_set_model(language_selection(widget), G_LIST_MODEL(model));
        g_object_unref(model);
    }
    if (!language_menu_current(widget, menu, view)) goto done;
    /* Renewals must not discard an unapplied choice while the menu is open. */
    if (reset || saved_changed || rebuild)
        select_language(widget, menu, !reset && !saved_changed && language_index(choices, selection) >= 0 ? selection : saved);
done:
    g_free(selection);
    return language_menu_current(widget, menu, view);
}
static gboolean render_languages(JsonObject *view) {
    JsonArray *source_choices = language_choices(view, "source"), *target_choices = language_choices(view, "target");
    const char *source = text_or_empty(view, "sourceLanguage"), *target = text_or_empty(view, "targetLanguage");
    char *source_label = language_title(view, "sourceLanguageLabel", source, source_choices);
    char *target_label = language_title(view, "targetLanguageLabel", target, target_choices);
    char *source_code = g_ascii_strup(source, -1), *target_code = g_ascii_strup(target, -1);
    char *title = g_strdup_printf("%s → %s", strcmp(source, "auto") == 0 ? "Auto" : source_code, target_code);
    g_free(source_code); g_free(target_code);
    char *description = g_strdup_printf("%s → %s", source_label, target_label);
    gboolean valid = source_choices && target_choices;
    gboolean reset = !gtk_widget_get_visible(overlay.menu) || !valid;
    JsonObject *rendered = json_object_ref(overlay.view);
    GtkWidget *source_widget = g_object_ref(overlay.source), *target_widget = g_object_ref(overlay.destination);
    /* A missing choice catalogue disables both selectors. Move an existing
     * owned interaction before model replacement or sensitivity clears focus;
     * never recover focus from NULL or an outside destination. */
    if (!valid && language_handoff_current(rendered) && language_focus_current())
        close_language_menu(rendered);
    gboolean current = overlay.view == rendered;
    if (current) overlay.setting_languages = TRUE;
    current = current && sync_language(source_widget, &overlay.source_languages, source_choices, source, source_label, reset, rendered)
        && sync_language(target_widget, &overlay.target_languages, target_choices, target, target_label, reset, rendered);
    if (current) { gtk_widget_set_sensitive(source_widget, valid); current = overlay.view == rendered; }
    if (current) { gtk_widget_set_sensitive(target_widget, valid); current = overlay.view == rendered; }
    if (current) { overlay.setting_languages = FALSE; refresh_language_apply(); current = overlay.view == rendered; }
    if (current) { gtk_button_set_label(GTK_BUTTON(overlay.language), title); current = overlay.view == rendered; }
    if (current) { gtk_widget_set_tooltip_text(overlay.language, description); current = overlay.view == rendered; }
    g_object_unref(source_widget); g_object_unref(target_widget); json_object_unref(rendered);
    if (source_choices) json_array_unref(source_choices);
    if (target_choices) json_array_unref(target_choices);
    g_free(source_label); g_free(target_label); g_free(title); g_free(description);
    return current;
}
void tl_overlay_acceptance(void) {
    if (!overlay.hint || !overlay.view) return;
    JsonObject *presentation = tl_object(overlay.view, "presentation");
    JsonObject *shortcuts = tl_object(overlay.view, "sessionShortcuts");
    const char *hint = text_or_empty(presentation, "tabHint");
    if (!boolean(presentation, "editorOwnsText")) {
        if (tl_acceptance_ready(overlay.target)) hint = text_or_empty(shortcuts, "acceptHint");
        else if (g_strcmp0(hint, tl_string(shortcuts, "acceptHint")) == 0)
            hint = text_or_empty(shortcuts, "passThroughHint");
    }
    /* Notify handlers may hide/reconfigure the overlay. Neither the borrowed
       JSON strings nor the second widget may be used across the first setter. */
    const char *accept_key = text_or_empty(shortcuts, "acceptKey");
    char *hint_text = g_strdup(hint), *exit_text = g_strdup(text_or_empty(shortcuts, "exitHint"));
    if (*accept_key && g_strcmp0(hint, tl_string(shortcuts, "acceptHint")) == 0) {
        g_free(hint_text); hint_text = g_strdup_printf("%s  %s", accept_key, overlay.english ? "Accept" : "接受");
    } else if (*accept_key && g_strcmp0(hint, tl_string(shortcuts, "returnHint")) == 0) {
        g_free(hint_text); hint_text = g_strdup_printf("%s  %s", accept_key, overlay.english ? "Return" : "返回");
    } else if (*accept_key && g_strcmp0(hint, tl_string(shortcuts, "passThroughHint")) == 0) {
        g_free(hint_text); hint_text = g_strdup("");
    }
    if (*text_or_empty(shortcuts, "exitKey")) {
        g_free(exit_text); exit_text = g_strdup("×");
    }
    char *generation = g_strdup(overlay.generation);
    GtkWidget *label = g_object_ref(overlay.hint), *exit_button = g_object_ref(overlay.exit);
    JsonObject *view = json_object_ref(overlay.view);
    gtk_label_set_text(GTK_LABEL(label), hint_text);
    if (overlay.view == view && overlay.hint == label && overlay.exit == exit_button
        && g_strcmp0(overlay.generation, generation) == 0)
        gtk_button_set_label(GTK_BUTTON(exit_button), exit_text);
    json_object_unref(view);
    g_object_unref(label); g_object_unref(exit_button);
    g_free(hint_text); g_free(exit_text); g_free(generation);
}
gboolean tl_overlay_bindings_ready(Target *target) {
    return !overlay.glossary && target && !target->dead && overlay.target == target && overlay.popover && overlay.text
        && tl_configuration_current(overlay.generation) && tl_view_context_matches(target, overlay.view)
        && tl_controller_attached(overlay.text, "typelay-session-controller");
}
gboolean tl_overlay_contains(GtkWidget *widget) {
    for (GtkWidget *parent = widget; parent; parent = gtk_widget_get_parent(parent))
        if (g_object_get_data(G_OBJECT(parent), "typelay-overlay")) return TRUE;
    return FALSE;
}
static gboolean active(void) {
    return overlay.target && !overlay.target->dead && overlay.top
        && gtk_window_is_active(GTK_WINDOW(overlay.top));
}
static gboolean more_current(void) {
    return overlay.more_popup && overlay.more_view && overlay.view == overlay.more_view
        && active() && overlay.popover && gtk_widget_get_mapped(overlay.popover)
        && !overlay.glossary && tl_now() <= overlay.expires
        && tl_configuration_current(overlay.generation)
        && tl_view_context_matches(overlay.target, overlay.view);
}
static void close_language_popups_outside(GtkWidget *widget, gboolean trigger_click) {
    if (widget) g_object_ref(widget);
    for (guint index = 0; index < 2; index++) {
        GtkWidget *button = index == 0 ? overlay.source : overlay.destination;
        if (!button || !gtk_menu_button_get_active(GTK_MENU_BUTTON(button))) continue;
        GtkWidget *popup = GTK_WIDGET(gtk_menu_button_get_popover(GTK_MENU_BUTTON(button)));
        if (widget && (widget == button || gtk_widget_is_ancestor(widget, button)
            || widget == popup || gtk_widget_is_ancestor(widget, popup))) continue;
        /* A real click on our trigger may close its owned language menu.
         * Other pointer/focus destinations must never be stolen. */
        if (trigger_click && widget && overlay.language && (widget == overlay.language
            || gtk_widget_is_ancestor(widget, overlay.language))
            && language_handoff_current(overlay.view) && language_focus_current())
            focus_language_trigger(overlay.view);
        if (!overlay.popover || (button != overlay.source && button != overlay.destination)) break;
        gtk_menu_button_popdown(GTK_MENU_BUTTON(button));
        if (!overlay.popover) break;
    }
    if (widget) g_object_unref(widget);
}
static gboolean language_pointer_event(GtkEventControllerLegacy *controller, GdkEvent *event, gpointer data) {
    (void)controller; (void)data;
    GdkEventType type = gdk_event_get_event_type(event);
    if (type != GDK_BUTTON_PRESS && type != GDK_TOUCH_BEGIN) return FALSE;
    GdkSurface *surface = gdk_event_get_surface(event);
    GtkNative *native = surface ? gtk_native_get_for_surface(surface) : NULL;
    double x, y, offset_x, offset_y;
    if (!native || !gdk_event_get_position(event, &x, &y)) return FALSE;
    gtk_native_get_surface_transform(native, &offset_x, &offset_y);
    GtkWidget *receiver = gtk_widget_pick(GTK_WIDGET(native), x + offset_x, y + offset_y, GTK_PICK_DEFAULT);
    g_object_ref(controller);
    if (receiver) g_object_ref(receiver);
    if (overlay.more_popup && (!receiver || (receiver != overlay.more
        && !gtk_widget_is_ancestor(receiver, overlay.more)
        && receiver != overlay.more_popup && !gtk_widget_is_ancestor(receiver, overlay.more_popup))))
        close_more(FALSE);
    close_language_popups_outside(receiver, TRUE);
    if (receiver) g_object_unref(receiver);
    g_object_unref(controller);
    return FALSE; /* Preserve the original click and its destination. */
}

gboolean tl_overlay_can_return(Target *target) {
    if (glossary_can_return(target)) return TRUE;
    const char *state = tl_string(overlay.view, "state");
    gboolean editor_owner = boolean(tl_object(overlay.view, "presentation"), "editorOwnsText")
        && (g_strcmp0(state, "EditingTranslation") == 0 || g_strcmp0(state, "ReturningToSource") == 0);
    if (overlay.target != target || !active() || !overlay.popover || !overlay.text
        || !gtk_widget_get_visible(overlay.popover) || tl_now() > overlay.expires
        || !editor_owner) return FALSE;
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(overlay.top));
    return focus && (focus == overlay.text || gtk_widget_is_ancestor(focus, overlay.text));
}
static gboolean paused(void) {
    return g_strcmp0(tl_string(overlay.view, "state"), "Paused") == 0;
}
static gboolean editor_control_current(const EditorControl *ticket) {
    return !overlay.glossary && ticket->keycode && editor_control.serial == ticket->serial
        && overlay.target == ticket->target && overlay.text == ticket->editor
        && overlay.view == ticket->view && overlay.revision == ticket->revision
        && tl_object(tl_object(overlay.view, "viewContext"), "token") == ticket->token
        && active() && !paused() && !overlay.composing && overlay.focused
        && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == overlay.text
        && gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text)) == ticket->buffer
        && g_object_get_data(G_OBJECT(overlay.text), TL_FCITX_CONTEXT_V1) == ticket->context
        && gtk_text_view_get_editable(GTK_TEXT_VIEW(overlay.text))
        && tl_overlay_bindings_ready(overlay.target)
        && tl_now() <= ticket->expires;
}
void tl_overlay_control_begin(guint keycode, guint64 sequence) {
    tl_overlay_control_cancel();
    if (overlay.glossary || !keycode || !sequence || editor_control_serial == G_MAXUINT64 || !active()
        || !overlay.text || !overlay.view || paused() || overlay.composing || !overlay.focused
        || gtk_window_get_focus(GTK_WINDOW(overlay.top)) != overlay.text
        || !gtk_text_view_get_editable(GTK_TEXT_VIEW(overlay.text))
        || !tl_overlay_bindings_ready(overlay.target) || tl_now() > overlay.expires) return;
    gpointer context = g_object_get_data(G_OBJECT(overlay.text), TL_FCITX_CONTEXT_V1);
    JsonObject *token = tl_object(tl_object(overlay.view, "viewContext"), "token");
    if (!context || !token) return;
    editor_control = (EditorControl){.target = overlay.target, .editor = overlay.text,
        .buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text)), .context = context,
        .view = overlay.view, .token = token, .revision = overlay.revision, .expires = overlay.expires,
        .keycode = keycode, .sequence = sequence, .serial = ++editor_control_serial};
}
static gboolean pause_current(Target *target, JsonObject *authority) {
    if (!target || !authority) return FALSE;
    /* IME inspection may dispatch invalidation. Hold the metadata record and
       recheck the source generation afterwards, without a text capture. */
    json_object_ref(authority);
    gboolean valid = tl_ime_clear(target) && tl_metadata_matches(target, authority)
        && tl_focused() == target
        && (gint64)target->content_revision == tl_integer(authority, "contentRevision", -1)
        && (gint64)target->input_revision == tl_integer(authority, "inputRevision", -1)
        && (gint64)target->focus_revision == tl_integer(authority, "focusRevision", -1);
    JsonObject *geometry = valid ? tl_geometry(target) : NULL;
    valid = valid && geometry != NULL;
    if (geometry) json_object_unref(geometry);
    json_object_unref(authority);
    return valid;
}
static void emit(const char *operation, JsonNode *value) {
    if (!overlay.target) { if (value) json_node_free(value); return; }
    JsonObject *event = json_object_new();
    json_object_set_string_member(event, "target", overlay.target->id);
    json_object_set_string_member(event, "operation", operation);
    json_object_set_int_member(event, "revision", overlay.revision);
    if (overlay.generation) json_object_set_string_member(event, "generation", overlay.generation);
    if (value) json_object_set_member(event, "value", value);
    else json_object_set_null_member(event, "value");
    tl_send_event("ui", event);
}
static void emit_boolean(const char *operation, gboolean value) {
    JsonNode *node = json_node_new(JSON_NODE_VALUE);
    json_node_set_boolean(node, value);
    emit(operation, node);
}
static void emit_string(const char *operation, const char *value) {
    JsonNode *node = json_node_new(JSON_NODE_VALUE);
    json_node_set_string(node, value);
    emit(operation, node);
}

static GlossaryForm *glossary_ref(GlossaryForm *form) {
    if (form) form->refs++;
    return form;
}
static void glossary_unref(GlossaryForm *form) {
    if (!form || --form->refs) return;
    for (guint index = 0; index < 2; index++) if (form->contexts[index]) {
        if (form->handlers[index]) g_signal_handler_disconnect(form->contexts[index], form->handlers[index]);
        g_object_remove_weak_pointer(G_OBJECT(form->contexts[index]), (gpointer *)&form->contexts[index]);
    }
    if (form->scope_popup) {
        if (gtk_widget_get_parent(form->scope_popup)) gtk_widget_unparent(form->scope_popup);
        g_object_unref(form->scope_popup);
    }
    if (form->scope_options) g_ptr_array_unref(form->scope_options);
    g_object_unref(form->box);
    json_object_unref(form->token); json_array_unref(form->directions);
    if (form->source_authority) json_object_unref(form->source_authority);
    if (form->source_view) json_object_unref(form->source_view);
    g_free(form->id); g_free(form->target); g_free(form->generation); g_free(form->origin_state); g_free(form);
}
static gboolean glossary_scope_option(GlossaryForm *form, GtkWidget *widget, guint *selected) {
    if (!form || !widget || !form->scope_options || !form->scope_popup
        || !gtk_widget_get_visible(form->scope_popup)
        || gtk_widget_get_parent(widget) != form->scope_choices) return FALSE;
    for (guint index = 0; index < form->scope_options->len; index++)
        if (widget == g_ptr_array_index(form->scope_options, index)) {
            if (selected) *selected = index;
            return TRUE;
        }
    return FALSE;
}
static gboolean glossary_contains(GlossaryForm *form, GtkWidget *widget) {
    /* Exact focus roles only. A box, label, scrollbar or unrelated descendant
     * never inherits auxiliary authority from the overlay-family marker. */
    return form && widget && (widget == form->clients[0] || widget == form->clients[1]
        || widget == form->scope || widget == form->save || widget == form->cancel
        || glossary_scope_option(form, widget, NULL));
}
static gboolean glossary_source_basis(GlossaryForm *form) {
    Target *target = overlay.target;
    JsonObject *basis = form ? form->source_authority : NULL;
    return form && form->source_origin && target && target == form->origin && !target->dead
        && overlay.top == form->origin_top && target->widget && target->buffer
        && target->buffer == form->origin_buffer && overlay.text && overlay.glossary_draft_buffer
        && gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text)) == overlay.glossary_draft_buffer
        && tl_source_glossary_supported(target)
        && target->client && target->client == form->origin_client
        && !target->mutating && !target->quarantined && target->im_context && target->im_observed
        && g_object_get_data(G_OBJECT(target->client), TL_FCITX_CONTEXT_V1) == target->im_context
        && (gint64)target->im_state.context_generation == tl_integer(tl_object(basis, "contextStamp"), "context", -1)
        && (gint64)target->content_revision == tl_integer(basis, "contentRevision", -1)
        && (gint64)target->input_revision == tl_integer(basis, "inputRevision", -1);
}
static gboolean glossary_source_view_matches(JsonObject *original, JsonObject *view) {
    JsonObject *old_context = tl_object(original, "viewContext"), *next_context = tl_object(view, "viewContext");
    if (!original || !view || !old_context || !next_context) return FALSE;
    JsonObject *copy = tl_copy(view), *context = tl_copy(next_context);
    json_object_set_object_member(copy, "viewContext", context);
    /* The actual auxiliary ACK withdraws these two booleans. Never discard a
     * whole projection: settings, model actions and unknown fields stay exact. */
    JsonObject *before[] = {original, old_context}, *after[] = {copy, context};
    JsonObject *current[] = {overlay.view, tl_object(overlay.view, "viewContext")};
    const char *keys[] = {"editable", "canEdit"};
    for (guint index = 0; index < G_N_ELEMENTS(keys); index++) {
        JsonNode *node = json_object_get_member(after[index], keys[index]);
        if (!boolean(current[index], keys[index]) && boolean(after[index], keys[index])) {
            json_object_unref(copy); return FALSE;
        }
        if (boolean(before[index], keys[index]) && node && JSON_NODE_HOLDS_VALUE(node)
            && json_node_get_value_type(node) == G_TYPE_BOOLEAN && !json_node_get_boolean(node))
            json_object_set_boolean_member(after[index], keys[index], TRUE);
    }
    JsonObject *old_actions = tl_object(old_context, "translationActions");
    JsonObject *actions = tl_object(context, "translationActions");
    if (g_strcmp0(tl_string(translation_actions(overlay.view), "glossaryUnavailableReason"), "内容或设置已变化，请重新打开表单") == 0
        && g_strcmp0(tl_string(actions, "glossaryUnavailableReason"), "请先完成当前术语表单") == 0) {
        json_object_unref(copy); return FALSE;
    }
    if (g_strcmp0(tl_string(old_actions, "glossaryUnavailableReason"), "请先完成当前术语表单") == 0
        && g_strcmp0(tl_string(actions, "glossaryUnavailableReason"), "内容或设置已变化，请重新打开表单") == 0) {
        /* QualifyGlossaryEntry checks the withdrawn ViewCommand ACK first. */
        actions = tl_copy(actions);
        json_object_set_string_member(actions, "glossaryUnavailableReason", "请先完成当前术语表单");
        json_object_set_object_member(context, "translationActions", actions);
    }
    gboolean same = json_object_equal(original, copy);
    json_object_unref(copy);
    return same;
}
static gboolean glossary_source_render(GlossaryForm *form, Target *target, JsonObject *data) {
    if (!form || !form->source_origin || overlay.target != target || !glossary_source_basis(form)
        || form->revision != tl_integer(data, "revision", -1)) return FALSE;
    JsonObject *value = tl_object(data, "view"), *basis = tl_object(data, "authority");
    if (!value || !basis
        || g_strcmp0(tl_string(basis, "metadataKey"), tl_string(form->source_authority, "metadataKey")) != 0
        || tl_integer(basis, "inputRevision", -1) != tl_integer(form->source_authority, "inputRevision", -2)
        || tl_integer(basis, "contentRevision", -1) != tl_integer(form->source_authority, "contentRevision", -2)
        || tl_integer(tl_object(basis, "contextStamp"), "context", -1)
            != tl_integer(tl_object(form->source_authority, "contextStamp"), "context", -2)) return FALSE;
    return glossary_source_view_matches(form->source_view, value);
}
static gboolean glossary_identity_current(GlossaryForm *form) {
    JsonObject *token = tl_object(tl_object(overlay.view, "viewContext"), "token");
    return form && overlay.target && !overlay.target->dead
        && g_strcmp0(form->target, overlay.target->id) == 0
        && g_strcmp0(form->generation, overlay.generation) == 0 && form->revision == overlay.revision
        && token && json_object_equal(form->token, token)
        && (gint64)overlay.target->content_revision == tl_integer(overlay.authority, "contentRevision", -1)
        && (gint64)overlay.target->input_revision == tl_integer(overlay.authority, "inputRevision", -1)
        && g_strcmp0(tl_string(overlay.view, "state"), form->origin_state) == 0
        && (!form->source_origin || glossary_source_basis(form))
        && tl_configuration_current(form->generation) && tl_view_context_matches(overlay.target, overlay.view)
        && active() && tl_now() <= overlay.expires;
}
static gboolean glossary_current(GlossaryForm *form) {
    return overlay.glossary == form && form && !form->closing && glossary_identity_current(form);
}
static void glossary_close(gboolean restore, gboolean notify);
gboolean tl_overlay_auxiliary_active(Target *target) {
    return overlay.target == target && glossary_current(overlay.glossary);
}
static void glossary_emit(GlossaryForm *form, const char *operation, JsonNode *value) {
    JsonObject *event = json_object_new();
    json_object_set_string_member(event, "target", form->target);
    json_object_set_string_member(event, "generation", form->generation);
    json_object_set_int_member(event, "revision", form->revision);
    json_object_set_string_member(event, "operation", operation);
    if (value) json_object_set_member(event, "value", value);
    else json_object_set_null_member(event, "value");
    tl_send_event("ui", event);
}
static void glossary_report_unknown(GlossaryForm *form) {
    gint ime = form->composing[0] || form->composing[1] ? 2 : 0;
    if (!glossary_current(form) || form->reported_ime == ime
        || !glossary_contains(form, gtk_window_get_focus(GTK_WINDOW(overlay.top)))) return;
    form->reported_ime = ime;
    JsonObject *value = json_object_new();
    json_object_set_string_member(value, "formId", form->id);
    json_object_set_object_member(value, "token", tl_copy(form->token));
    json_object_set_string_member(value, "ime", ime == 2 ? "Composing" : "Unknown");
    JsonNode *node = json_node_new(JSON_NODE_OBJECT); json_node_take_object(node, value);
    glossary_emit(form, "sessionGlossaryFocus", node);
}
static void glossary_ime_changed(GtkIMContext *context, guint causes, gpointer data) {
    GlossaryForm *form = data;
    for (guint index = 0; index < 2; index++) if (form->contexts[index] == context) {
        /* A pure focus transition preserves only an existing explicit Clear.
         * The notification precedes focus state; never guess its direction.
         * Any input, binding change or unspecified invalidation revokes it. */
        if (causes != TL_FCITX_CHANGE_FOCUS) form->clear[index] = FALSE;
    }
    tl_overlay_control_cancel();
    glossary_report_unknown(form);
}
static void glossary_bind_context(GlossaryForm *form, guint index) {
    GtkIMContext *context = g_object_get_data(G_OBJECT(form->clients[index]), TL_FCITX_CONTEXT_V1);
    if (context == form->contexts[index]) return;
    if (form->contexts[index]) {
        if (form->handlers[index]) g_signal_handler_disconnect(form->contexts[index], form->handlers[index]);
        g_object_remove_weak_pointer(G_OBJECT(form->contexts[index]), (gpointer *)&form->contexts[index]);
    }
    form->contexts[index] = context; form->handlers[index] = 0; form->clear[index] = FALSE;
    if (context) {
        g_object_add_weak_pointer(G_OBJECT(context), (gpointer *)&form->contexts[index]);
        guint signal = g_signal_lookup(TL_FCITX_CHANGED_V1, G_OBJECT_TYPE(context));
        GSignalQuery query = {0};
        if (signal) g_signal_query(signal, &query);
        if (query.return_type == G_TYPE_NONE && query.n_params == 1 && query.param_types[0] == G_TYPE_UINT)
            form->handlers[index] = g_signal_connect(context, TL_FCITX_CHANGED_V1,
                G_CALLBACK(glossary_ime_changed), form);
    }
}
static gboolean glossary_terms_clear(GlossaryForm *form) {
    return form->clear[0] && form->clear[1] && !form->composing[0] && !form->composing[1]
        && form->contexts[0] && form->contexts[1] && form->handlers[0] && form->handlers[1]
        && g_object_get_data(G_OBJECT(form->clients[0]), TL_FCITX_CONTEXT_V1) == form->contexts[0]
        && g_object_get_data(G_OBJECT(form->clients[1]), TL_FCITX_CONTEXT_V1) == form->contexts[1];
}
static void glossary_sync(void) {
    if (glossary_handoff) return;
    GlossaryForm *form = glossary_ref(overlay.glossary);
    if (!form || form->probing || form->closing) { glossary_unref(form); return; }
    form->probing = TRUE;
    GtkWidget *focus = overlay.top ? gtk_window_get_focus(GTK_WINDOW(overlay.top)) : NULL;
    if (glossary_current(form) && gtk_widget_get_visible(form->scope_popup)
        && focus != form->scope && glossary_contains(form, focus)
        && !glossary_scope_option(form, focus, NULL)) {
        /* The ungrabbed scope popup closes only after a real known control
         * already owns focus; dismissal must not invent a focus return. */
        gtk_popover_popdown(GTK_POPOVER(form->scope_popup));
        focus = overlay.top ? gtk_window_get_focus(GTK_WINDOW(overlay.top)) : NULL;
    }
    gint ime = -1;
    if (glossary_current(form) && glossary_contains(form, focus)) {
        ime = 0;
        for (guint index = 0; index < 2; index++) {
            glossary_bind_context(form, index);
            if (focus == form->clients[index]) {
                /* Empty preedit and button focus never establish Clear. */
                gboolean clear = tl_widget_ime_clear(form->clients[index]);
                form->clear[index] = clear && glossary_current(form) && !form->composing[index]
                    && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == form->clients[index]
                    && form->contexts[index] && form->handlers[index]
                    && g_object_get_data(G_OBJECT(form->clients[index]), TL_FCITX_CONTEXT_V1) == form->contexts[index];
                break;
            }
        }
        /* Shared focus and Escape use the same two-field aggregate as Save;
         * probing one focused entry cannot conceal an unknown peer entry. */
        ime = form->composing[0] || form->composing[1] ? 2 : glossary_terms_clear(form) ? 1 : 0;
    }
    if (!glossary_current(form) || !overlay.top
        || !glossary_contains(form, gtk_window_get_focus(GTK_WINDOW(overlay.top)))) ime = -1;
    if (overlay.glossary == form && form->reported_ime != ime) {
        form->reported_ime = ime;
        JsonNode *node = NULL;
        if (ime >= 0) {
            JsonObject *value = json_object_new();
            json_object_set_string_member(value, "formId", form->id);
            json_object_set_object_member(value, "token", tl_copy(form->token));
            json_object_set_string_member(value, "ime", ime == 1 ? "Clear" : ime == 2 ? "Composing" : "Unknown");
            node = json_node_new(JSON_NODE_OBJECT); json_node_take_object(node, value);
        }
        glossary_emit(form, "sessionGlossaryFocus", node);
    }
    form->probing = FALSE;
    glossary_unref(form);
}
static void glossary_focus_observed(GObject *object, GParamSpec *property, gpointer data) {
    (void)property;
    GlossaryHandoff *guard = data;
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(object));
    /* GTK4 may expose the old focus being cleared before its destination.
     * Only that one NULL followed by the exact destination is permitted. */
    if (GTK_WIDGET(object) != guard->top || guard->destination_seen || guard->emissions >= 2
        || (focus && focus != guard->destination)
        || (!focus && guard->emissions != 0)) guard->failed = TRUE;
    if (focus == guard->destination) guard->destination_seen = TRUE;
    guard->emissions++;
}
static JsonObject *glossary_receipt(GlossaryForm *form) {
    JsonObject *value = json_object_new();
    json_object_set_string_member(value, "formId", form->id);
    json_object_set_object_member(value, "token", tl_copy(form->token));
    return value;
}
static gboolean glossary_transfer(GlossaryForm *form, gboolean returning) {
    if (glossary_handoff || !glossary_current(form) || !glossary_source_basis(form)) return FALSE;
    GtkWidget *from = gtk_window_get_focus(GTK_WINDOW(overlay.top));
    if (returning ? !glossary_contains(form, from) : from != form->origin->client) return FALSE;
    GtkWidget *to = returning ? form->origin->client : form->clients[0];
    GlossaryHandoff guard = {.top = overlay.top, .destination = to};
    g_object_ref(guard.top);
    gulong hook = g_signal_connect(guard.top, "notify::focus-widget", G_CALLBACK(glossary_focus_observed), &guard);
    JsonObject *identity = glossary_receipt(form);
    gboolean begun = tl_glossary_transfer_begin(form->origin, identity, returning ? "Returning" : "Opening");
    json_object_unref(identity);
    if (!begun) { g_signal_handler_disconnect(guard.top, hook); g_object_unref(guard.top); return FALSE; }
    glossary_handoff = &guard;
    gboolean moved = TRUE;
    if (returning) moved = tl_return(form->origin, form->source_authority);
    else gtk_widget_grab_focus(to);
    glossary_handoff = NULL;
    g_signal_handler_disconnect(guard.top, hook);
    gboolean current = moved && !guard.failed && guard.destination_seen && guard.emissions > 0 && guard.emissions <= 2 && glossary_current(form)
        && glossary_source_basis(form) && overlay.top == guard.top && active()
        && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == to;
    g_object_unref(guard.top);
    return tl_glossary_transfer_end(current);
}

static gboolean glossary_return_request(GlossaryForm *form, Target *target, JsonObject *request) {
    JsonObject *token = tl_object(request, "token");
    return glossary_current(form) && form->source_origin && overlay.target == target && form->pending
        && g_strcmp0(form->id, tl_string(request, "formId")) == 0
        && g_strcmp0(form->generation, tl_string(request, "generation")) == 0
        && form->revision == tl_integer(request, "revision", -1)
        && token && json_object_equal(form->token, token);
}
JsonObject *tl_overlay_glossary_return_basis(Target *target, JsonObject *request) {
    GlossaryForm *form = overlay.glossary;
    return glossary_return_request(form, target, request) && !form->return_started
        ? tl_copy(form->source_authority) : NULL;
}
gboolean tl_overlay_glossary_return(Target *target, JsonObject *request, gboolean starting, gboolean ready) {
    GlossaryForm *form = glossary_ref(overlay.glossary);
    gboolean valid = glossary_return_request(form, target, request) && glossary_source_basis(form);
    if (valid && starting) {
        valid = !form->return_started && glossary_contains(form, gtk_window_get_focus(GTK_WINDOW(overlay.top)));
        if (valid) glossary_sync();
        valid = valid && glossary_return_request(form, target, request) && glossary_terms_clear(form)
            && form->reported_ime == 1;
        if (valid) {
            form->return_started = TRUE;
            valid = glossary_transfer(form, TRUE);
            if (!valid && overlay.glossary == form) glossary_close(FALSE, TRUE);
        }
    }
    valid = valid && glossary_return_request(form, target, request) && form->return_started
        && glossary_source_basis(form) && tl_focused() == target
        && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == target->client;
    if (valid && ready) { form->returned = TRUE; form->returned_focus = target->focus_revision; }
    if (starting) tl_overlay_sync();
    glossary_unref(form);
    return valid;
}
JsonObject *tl_overlay_glossary_source(Target *target) {
    GlossaryForm *form = overlay.glossary;
    return glossary_current(form) && form->source_origin && form->returned && overlay.target == target
        && glossary_source_basis(form) && tl_focused() == target && target->ime_known && !target->composing
        && form->returned_focus == target->focus_revision ? glossary_receipt(form) : NULL;
}
static gboolean glossary_can_return(Target *target) {
    GlossaryForm *form = overlay.glossary;
    return glossary_current(form) && form->source_origin && form->return_started
        && target == form->origin && glossary_source_basis(form)
        && glossary_contains(form, gtk_window_get_focus(GTK_WINDOW(overlay.top)));
}
static void glossary_close(gboolean restore, gboolean notify) {
    GlossaryForm *form = glossary_ref(overlay.glossary);
    if (!form || form->closing) { glossary_unref(form); return; }
    gboolean return_editor = restore && glossary_current(form) && overlay.text
        && glossary_contains(form, gtk_window_get_focus(GTK_WINDOW(overlay.top)));
    GtkWidget *editor = return_editor ? g_object_ref(form->source_origin ? form->origin->widget : overlay.text) : NULL;
    if (notify) {
        JsonNode *node = json_node_new(JSON_NODE_VALUE); json_node_set_string(node, form->id);
        glossary_emit(form, "sessionGlossaryClose", node);
    }
    if (form->reported_ime >= 0) glossary_emit(form, "sessionGlossaryFocus", NULL);
    return_editor = return_editor && glossary_current(form)
        && (form->source_origin ? form->origin->widget == editor : overlay.text == editor)
        && glossary_contains(form, gtk_window_get_focus(GTK_WINDOW(overlay.top)));
    /* Transfer the overlay owner before grab_focus: a synchronous focus callback
     * can hide/memset overlay. This stack must still release that owner once. */
    gboolean owned = overlay.glossary == form;
    if (owned) overlay.glossary = NULL;
    form->closing = TRUE;
    if (!restore && owned && form->source_origin && overlay.top) {
        GtkWindow *top = g_object_ref(GTK_WINDOW(overlay.top));
        /* Removing a focused child lets GTK navigate to another field and
         * select its text. Release only our exact auxiliary focus before
         * unparenting; invalidation must not choose a source focus or selection. */
        if (glossary_contains(form, gtk_window_get_focus(top)))
            gtk_window_set_focus(top, NULL);
        g_object_unref(top);
    }
    if (return_editor) {
        /* The host can retire editor readiness while aux owns focus. Return to
         * its original read-only widget; fresh binding/IME ACK enables editing. */
        if (form->source_origin) {
            if (glossary_identity_current(form) && glossary_source_basis(form)
                && glossary_contains(form, gtk_window_get_focus(GTK_WINDOW(overlay.top)))) {
                if (GTK_IS_ENTRY(editor)) gtk_entry_grab_focus_without_selecting(GTK_ENTRY(editor));
                else if (GTK_IS_TEXT_VIEW(editor)) gtk_widget_grab_focus(editor);
            }
        } else {
            gtk_widget_set_focusable(editor, TRUE);
            if (glossary_identity_current(form) && overlay.text == editor
                && glossary_contains(form, gtk_window_get_focus(GTK_WINDOW(overlay.top)))) gtk_widget_grab_focus(editor);
        }
    }
    if (owned) {
        if (gtk_widget_get_parent(form->box)) gtk_box_remove(GTK_BOX(gtk_widget_get_parent(form->box)), form->box);
        glossary_unref(form);
    }
    if (editor) g_object_unref(editor);
    glossary_unref(form);
}
static gboolean close_more_escape(void);
gboolean tl_overlay_auxiliary_key(guint keyval, GdkModifierType modifiers) {
    /* Match the dispatcher's effective modifiers; lock states are not chords. */
    modifiers &= GDK_SHIFT_MASK | GDK_CONTROL_MASK | GDK_ALT_MASK
        | GDK_SUPER_MASK | GDK_HYPER_MASK | GDK_META_MASK;
    if (keyval != GDK_KEY_Escape || modifiers) return FALSE;
    if (close_more_escape()) return TRUE;
    GlossaryForm *form = glossary_ref(overlay.glossary);
    gboolean consumed = FALSE;
    if (!glossary_current(form) || form->reported_ime != 1) goto done;
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(overlay.top));
    if (!glossary_contains(form, focus)) goto done;
    /* The dispatcher has registered this real press, before GTK/Fcitx sees it.
     * Existing aggregate Clear plus a new real observation authorizes only
     * this form's cancel. Unknown/composing goes to the original IME once. */
    glossary_sync();
    /* A real probe can dispatch callbacks. Retire this original press if its
     * form or focused control changed; it must not borrow a replacement ticket. */
    if (!glossary_current(form) || !overlay.top
        || gtk_window_get_focus(GTK_WINDOW(overlay.top)) != focus) consumed = TRUE;
    else if (form->reported_ime == 1) {
        glossary_close(TRUE, TRUE);
        consumed = TRUE;
    }
done:
    glossary_unref(form);
    return consumed;
}
static void glossary_term_changed(GtkEditable *editable, gpointer data) {
    GlossaryForm *form = data;
    for (guint index = 0; index < 2; index++) if (GTK_EDITABLE(form->terms[index]) == editable)
        form->clear[index] = FALSE;
    tl_overlay_control_cancel();
    glossary_report_unknown(form);
}
static void glossary_preedit(GtkWidget *widget, const gchar *text, gpointer data) {
    GlossaryForm *form = data;
    for (guint index = 0; index < 2; index++) if (form->clients[index] == widget) {
        form->composing[index] = text && *text;
        form->clear[index] = FALSE;
    }
    tl_overlay_control_cancel();
    /* The provider callback stack is not an inspection boundary. Report its
     * conservative state now; the existing overlay timer obtains real Clear. */
    if (glossary_current(form) && glossary_contains(form, gtk_window_get_focus(GTK_WINDOW(overlay.top)))) {
        JsonObject *value = json_object_new();
        json_object_set_string_member(value, "formId", form->id);
        json_object_set_object_member(value, "token", tl_copy(form->token));
        form->reported_ime = form->composing[0] || form->composing[1] ? 2 : 0;
        json_object_set_string_member(value, "ime", form->reported_ime == 2 ? "Composing" : "Unknown");
        JsonNode *node = json_node_new(JSON_NODE_OBJECT); json_node_take_object(node, value);
        glossary_emit(form, "sessionGlossaryFocus", node);
    }
}
static void glossary_reject_drop(GlossaryForm *form, guint input) {
    GListModel *controllers = gtk_widget_observe_controllers(form->clients[input]);
    for (guint index = 0; glossary_current(form) && index < g_list_model_get_n_items(controllers); index++) {
        GObject *controller = g_list_model_get_item(controllers, index);
        if (GTK_IS_DROP_TARGET(controller)) gtk_drop_target_reject(GTK_DROP_TARGET(controller));
        g_object_unref(controller);
    }
    g_object_unref(controllers);
}
static void glossary_freeze_inputs(GlossaryForm *form, gboolean frozen) {
    if (!glossary_current(form)) return;
    for (guint index = 0; index < 2; index++) form->buffers[index]->locked = frozen;
    for (guint index = 0; index < 2 && glossary_current(form); index++) {
        gtk_widget_set_can_focus(form->terms[index], !frozen);
        if (glossary_current(form)) gtk_widget_set_can_target(form->terms[index], !frozen);
        /* Reject an already accepted drop too: targetability only prevents new
         * pointer/drop selection and must not leave a queued MOVE successful. */
        if (frozen && glossary_current(form)) glossary_reject_drop(form, index);
    }
    if (glossary_current(form)) gtk_widget_set_sensitive(form->scope, !frozen);
}
static void glossary_scope_clicked(GtkButton *button, gpointer data) {
    GlossaryForm *form = glossary_ref(data);
    if (!glossary_current(form) || form->pending || form->finished
        || gtk_window_get_focus(GTK_WINDOW(overlay.top)) != GTK_WIDGET(button)) goto done;
    if (GTK_WIDGET(button) == form->scope) {
        if (gtk_widget_get_visible(form->scope_popup)) gtk_popover_popdown(GTK_POPOVER(form->scope_popup));
        else {
            gtk_popover_popup(GTK_POPOVER(form->scope_popup));
            if (glossary_current(form)) gtk_widget_grab_focus(g_ptr_array_index(form->scope_options, form->selected_direction));
        }
    } else {
        guint selected;
        if (!glossary_scope_option(form, GTK_WIDGET(button), &selected)) goto done;
        form->selected_direction = selected;
        char *label = g_strdup_printf("%s ▾", gtk_button_get_label(button));
        gtk_button_set_label(GTK_BUTTON(form->scope), label); g_free(label);
        if (glossary_current(form)) gtk_widget_grab_focus(form->scope);
        /* Restore actual scope focus before dismissing this owned popup. */
        if (glossary_current(form) && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == form->scope)
            gtk_popover_popdown(GTK_POPOVER(form->scope_popup));
    }
    if (glossary_current(form)) glossary_sync();
done:
    glossary_unref(form);
}
static void glossary_clicked(GtkButton *button, gpointer data) {
    GlossaryForm *form = glossary_ref(data);
    if (!glossary_current(form)) goto done;
    if (GTK_WIDGET(button) == form->cancel) { glossary_close(TRUE, TRUE); goto done; }
    if (GTK_WIDGET(button) != form->save || form->pending || form->finished) goto done;
    glossary_sync();
    if (!glossary_current(form) || !glossary_contains(form, gtk_window_get_focus(GTK_WINDOW(overlay.top)))) goto done;
    if (!glossary_terms_clear(form)) {
        gtk_label_set_text(GTK_LABEL(form->feedback), "请先完成两个词条的输入法组合，再保存");
        goto done;
    }
    /* Keep the real Save focus while freezing auxiliary values without any
     * IM-resetting entry property setter. Genuine invalidations still abort. */
    gtk_widget_grab_focus(form->save);
    if (!glossary_current(form) || gtk_window_get_focus(GTK_WINDOW(overlay.top)) != form->save
        || !glossary_terms_clear(form)) goto done;
    const char *source = gtk_editable_get_text(GTK_EDITABLE(form->terms[0])), *target = gtk_editable_get_text(GTK_EDITABLE(form->terms[1]));
    if (!source || !*source || !target || !*target) {
        gtk_label_set_text(GTK_LABEL(form->feedback), "请明确填写源词和译词"); goto done;
    }
    if (strlen(source) > 128 || strlen(target) > 128
        || !g_utf8_validate(source, -1, NULL) || !g_utf8_validate(target, -1, NULL)) {
        gtk_label_set_text(GTK_LABEL(form->feedback), "每个词条最多 128 个 UTF-8 字节，请缩短后保存");
        goto done;
    }
    guint selected = form->selected_direction;
    if (selected > json_array_get_length(form->directions)) goto done;
    JsonObject *rule = json_object_new();
    json_object_set_string_member(rule, "sourceTerm", source);
    json_object_set_string_member(rule, "targetTerm", target);
    JsonObject *direction = selected ? json_array_get_object_element(form->directions, selected - 1) : NULL;
    if (direction) {
        json_object_set_string_member(rule, "sourceLanguage", tl_string(direction, "source"));
        json_object_set_string_member(rule, "targetLanguage", tl_string(direction, "target"));
    } else {
        json_object_set_null_member(rule, "sourceLanguage"); json_object_set_null_member(rule, "targetLanguage");
    }
    JsonObject *value = json_object_new();
    json_object_set_string_member(value, "formId", form->id);
    json_object_set_object_member(value, "token", tl_copy(form->token));
    json_object_set_int_member(value, "settingsRevision", form->settings_revision);
    json_object_set_object_member(value, "rule", rule);
    JsonNode *node = json_node_new(JSON_NODE_OBJECT); json_node_take_object(node, value);
    form->pending = TRUE;
    glossary_freeze_inputs(form, TRUE);
    glossary_sync();
    if (glossary_current(form) && glossary_terms_clear(form)
        && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == form->save) {
        gtk_label_set_text(GTK_LABEL(form->feedback), "正在保存…");
        if (glossary_current(form) && glossary_terms_clear(form)) glossary_emit(form, "sessionGlossaryAdd", node);
        else { form->pending = FALSE; json_node_free(node); glossary_freeze_inputs(form, FALSE); }
    } else {
        form->pending = FALSE; json_node_free(node);
        glossary_freeze_inputs(form, FALSE);
        if (glossary_current(form)) gtk_label_set_text(GTK_LABEL(form->feedback), "输入状态已变化，请确认两个词条后再保存");
    }
done:
    glossary_unref(form);
}
static gboolean glossary_key(guint keyval, GdkModifierType modifiers) {
    modifiers &= GDK_SHIFT_MASK | GDK_CONTROL_MASK | GDK_ALT_MASK
        | GDK_SUPER_MASK | GDK_HYPER_MASK | GDK_META_MASK;
    GlossaryForm *form = glossary_ref(overlay.glossary);
    gboolean consumed = FALSE;
    if (!glossary_current(form) || !glossary_contains(form, gtk_window_get_focus(GTK_WINDOW(overlay.top)))) goto done;
    /* This runs after the real entry/IM handler. Fn keys never become editor
     * commands, and unconsumed Tab only moves among auxiliary controls. */
    GtkWidget *focused = gtk_window_get_focus(GTK_WINDOW(overlay.top));
    guint option = 0;
    gboolean in_options = glossary_scope_option(form, focused, &option);
    if (in_options && (keyval == GDK_KEY_Up || keyval == GDK_KEY_Down)
        && !modifiers) {
        guint count = form->scope_options->len;
        guint next = (option + (keyval == GDK_KEY_Up ? count - 1 : 1)) % count;
        gtk_widget_grab_focus(g_ptr_array_index(form->scope_options, next));
        consumed = TRUE;
    } else if (keyval == GDK_KEY_Tab || keyval == GDK_KEY_ISO_Left_Tab) {
        if (modifiers & ~((guint)GDK_SHIFT_MASK)) goto done;
        /* Capture an actual Clear while this entry still owns focus, rather
         * than relying on the periodic probe having run before a fast Tab. */
        for (guint input = 0; input < 2; input++) if (focused == form->clients[input]) {
            consumed = TRUE;
            glossary_sync();
            if (!glossary_current(form) || gtk_window_get_focus(GTK_WINDOW(overlay.top)) != focused
                || !form->clear[input] || form->composing[input]) goto done;
            break;
        }
        GtkWidget *items[] = {form->terms[0], form->terms[1], form->scope, form->save, form->cancel};
        GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(overlay.top));
        guint index = 0;
        for (; index < G_N_ELEMENTS(items); index++)
            if (focus == items[index] || (index < 2 && focus == form->clients[index])) break;
        if (in_options) index = 2;
        gboolean backwards = keyval == GDK_KEY_ISO_Left_Tab || (modifiers & GDK_SHIFT_MASK);
        guint count = (guint)G_N_ELEMENTS(items);
        guint next = index < count ? index : 0;
        for (guint step = 0; step < count; step++) {
            next = (next + (backwards ? count - 1 : 1)) % count;
            if ((form->pending || form->finished) && next < 3) continue;
            if (!gtk_widget_is_sensitive(items[next])) continue;
            gtk_widget_grab_focus(items[next]);
            if (in_options && glossary_current(form)
                && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == items[next])
                gtk_popover_popdown(GTK_POPOVER(form->scope_popup));
            break;
        }
        consumed = TRUE;
    } else if (keyval == GDK_KEY_Escape) {
        /* GtkPopover otherwise handles even Unknown/modified Escape by hiding
         * the whole overlay. The child IM already had its original event. */
        consumed = TRUE;
        /* Only the original dispatcher may close on pre-IME Clear. A raw
         * Escape delivered to IME must never close in this fallback, even if
         * it has just made the two-field aggregate Clear. */
        glossary_sync();
    }
done:
    glossary_unref(form);
    return consumed;
}
static gboolean glossary_dispatch(GtkEventControllerLegacy *controller, GdkEvent *event, gpointer data) {
    (void)data;
    /* Releases only retire their original physical latch, even if focus moved.
     * A stale form can never initiate a command against the new focused source. */
    if (gdk_event_get_event_type(event) == GDK_KEY_RELEASE) return tl_dispatch_key(event);
    GlossaryForm *form = overlay.glossary;
    if (!glossary_current(form) || gtk_event_controller_get_widget(GTK_EVENT_CONTROLLER(controller)) != form->box
        || !glossary_contains(form, gtk_window_get_focus(GTK_WINDOW(overlay.top)))) return FALSE;
    /* Like Tab, a pointer departure must observe the focused term before GTK
     * moves focus to Save/scope/another term. The timer may not have sampled
     * the last IM completion yet. This does not consume or replay the event;
     * Unknown/composition and the other term's invalidation remain rejected. */
    GdkEventType type = gdk_event_get_event_type(event);
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(overlay.top));
    if ((type == GDK_BUTTON_PRESS || type == GDK_TOUCH_BEGIN)
        && (focus == form->clients[0] || focus == form->clients[1])) glossary_sync();
    return tl_dispatch_key(event);
}
static gboolean glossary_key_event(GtkEventControllerKey *controller, guint keyval, guint keycode,
    GdkModifierType state, gpointer data) {
    (void)keycode; (void)data;
    GtkWidget *owner = gtk_event_controller_get_widget(GTK_EVENT_CONTROLLER(controller));
    if (!overlay.glossary || (owner != overlay.glossary->box && owner != overlay.glossary->scope_popup)) return FALSE;
    return glossary_key(keyval, state);
}
static gboolean glossary_directions_valid(JsonArray *directions) {
    if (!directions) return FALSE;
    for (guint index = 0; index < json_array_get_length(directions); index++) {
        JsonNode *node = json_array_get_element(directions, index);
        JsonObject *direction = node && JSON_NODE_HOLDS_OBJECT(node) ? json_node_get_object(node) : NULL;
        const char *source = tl_string(direction, "source"), *target = tl_string(direction, "target");
        if (!source || !*source || !target || !*target || strcmp(source, target) == 0
            || g_ascii_strcasecmp(source, "auto") == 0 || g_ascii_strcasecmp(target, "auto") == 0
            || !*text_or_empty(direction, "sourceName") || !*text_or_empty(direction, "targetName")) return FALSE;
    }
    return TRUE;
}
static gboolean glossary_editor_ready(void) {
    if (!overlay.text || !overlay.view || !active() || overlay.composing
        || gtk_window_get_focus(GTK_WINDOW(overlay.top)) != overlay.text) return FALSE;
    GtkWidget *editor = g_object_ref(overlay.text);
    JsonObject *view = json_object_ref(overlay.view);
    /* Moving focus must not cause an unfinished translation preedit to commit.
     * This proof only permits leaving that editor, never saving auxiliary text. */
    gboolean clear = tl_widget_ime_clear(editor);
    gboolean current = clear && overlay.text == editor && overlay.view == view
        && active() && !overlay.composing && translation_menu_current()
        && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == editor;
    json_object_unref(view); g_object_unref(editor);
    return current;
}
/* Fixed, bounded rejection evidence on the existing opt-in trace. No input,
 * provider query, metadata recomputation, source text or opaque identity. */
static void trace_glossary_action(const char *stage, JsonObject *basis, gint clear, gint metadata) {
    static guint count;
    if (!g_getenv("TYPELAY_TRACE") || count >= 32) return;
    count++;
    Target *target = overlay.target;
    gboolean live = target && !target->dead;
    GtkWidget *focus = overlay.top ? gtk_window_get_focus(GTK_WINDOW(overlay.top)) : NULL;
    const char *focus_kind = !focus ? "none" : live && focus == target->client ? "source"
        : focus == overlay.text ? "editor" : "other";
    g_printerr("TypeLay GTK4 glossary-action stage=%s atMs=%" G_GINT64_FORMAT
        " expiresAtMs=%" G_GINT64_FORMAT " revision=%" G_GINT64_FORMAT
        " sourceLive=%d focus=%s popupMapped=%d pending=%d form=%d clear=%d metadata=%d"
        " context=%" G_GINT64_FORMAT " basisContext=%" G_GINT64_FORMAT
        " input=%" G_GINT64_FORMAT " basisInput=%" G_GINT64_FORMAT
        " content=%" G_GINT64_FORMAT " basisContent=%" G_GINT64_FORMAT "\n",
        stage, tl_now(), overlay.expires, overlay.revision, live, focus_kind,
        overlay.more_popup && gtk_widget_get_mapped(overlay.more_popup),
        overlay.pending_glossary, overlay.glossary != NULL, clear, metadata,
        live ? (gint64)target->im_state.context_generation : -1,
        tl_integer(tl_object(basis, "contextStamp"), "context", -1),
        live ? (gint64)target->input_revision : -1, tl_integer(basis, "inputRevision", -1),
        live ? (gint64)target->content_revision : -1, tl_integer(basis, "contentRevision", -1));
}
static gboolean glossary_source_ready(void) {
    Target *target = overlay.target;
    const char *state = tl_string(overlay.view, "state");
    if (!target || target->dead || !target->widget || !target->buffer || !active()
        || (g_strcmp0(state, "ValidTranslation") != 0 && g_strcmp0(state, "EditedTranslationPending") != 0)
        || !tl_source_glossary_supported(target)
        || gtk_window_get_focus(GTK_WINDOW(overlay.top)) != target->client || tl_focused() != target
        || !translation_menu_current()
        || (overlay.pending_glossary && (!overlay.text || !overlay.glossary_draft_buffer
            || gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text)) != overlay.glossary_draft_buffer))) {
        trace_glossary_action("reject-source-entry", overlay.authority, -1, -1);
        return FALSE;
    }
    JsonObject *view = json_object_ref(overlay.view), *basis = tl_copy(overlay.authority);
    gboolean clear = tl_ime_clear(target);
    gint metadata = -1; /* Unknown if the original short-circuit never reaches it. */
    gboolean current = clear && overlay.target == target && overlay.view == view && active()
        && tl_focused() == target && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == target->client
        && (metadata = tl_metadata_matches(target, basis))
        && (gint64)target->im_state.context_generation == tl_integer(tl_object(basis, "contextStamp"), "context", -1)
        && (gint64)target->input_revision == tl_integer(basis, "inputRevision", -1)
        && (gint64)target->content_revision == tl_integer(basis, "contentRevision", -1)
        && overlay.view == view && translation_menu_current() && active() && tl_focused() == target
        && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == target->client;
    if (!current) trace_glossary_action("reject-source-proof", basis, clear, metadata);
    json_object_unref(view); json_object_unref(basis);
    return current;
}
static gboolean glossary_origin_ready(void) {
    return g_strcmp0(tl_string(overlay.view, "state"), "EditingTranslation") == 0
        ? glossary_editor_ready() : glossary_source_ready();
}
static gboolean close_more_escape(void) {
    if (!more_current() || !gtk_widget_get_mapped(overlay.more_popup)) return FALSE;
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(overlay.top));
    Target *target = overlay.target;
    GtkWidget *popup = g_object_ref(overlay.more_popup);
    JsonObject *view = json_object_ref(overlay.view);
    gboolean clear = focus == overlay.text ? !overlay.composing && tl_widget_ime_clear(focus)
        : target && !target->dead && focus == target->client && tl_focused() == target && tl_ime_clear(target);
    /* The IM observation can dispatch callbacks. A retired opening consumes
     * its original press without closing or dispatching against a replacement.
     * Only an unchanged opening's unknown/preedit goes to the original IME. */
    gboolean same_interaction = more_current() && overlay.more_popup == popup
        && overlay.view == view && overlay.target == target
        && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == focus;
    if (same_interaction && clear) close_more(FALSE);
    json_object_unref(view); g_object_unref(popup);
    return !same_interaction || clear;
}
static void glossary_reject_open(Target *target, JsonObject *data, const char *id) {
    if (!target || !id || !*id || !tl_string(data, "generation")) return;
    JsonObject *event = json_object_new();
    json_object_set_string_member(event, "target", target->id);
    json_object_set_string_member(event, "generation", tl_string(data, "generation"));
    json_object_set_int_member(event, "revision", tl_integer(data, "revision", -1));
    json_object_set_string_member(event, "operation", "sessionGlossaryClose");
    json_object_set_string_member(event, "value", id);
    tl_send_event("ui", event);
}
static void glossary_open(Target *target, JsonObject *data) {
    JsonObject *entry = tl_object(data, "entry"), *token = tl_object(entry, "token");
    JsonNode *directions_node = entry ? json_object_get_member(entry, "directions") : NULL;
    JsonArray *directions = directions_node && JSON_NODE_HOLDS_ARRAY(directions_node) ? json_node_get_array(directions_node) : NULL;
    const char *id = tl_string(entry, "formId");
    if (overlay.glossary && g_strcmp0(overlay.glossary->id, id) == 0) return;
    gboolean source_origin = boolean(entry, "sourceOrigin");
    gboolean editor_ready = overlay.pending_glossary && glossary_origin_ready();
    if (!editor_ready || !overlay.pending_glossary || overlay.glossary || !target || overlay.target != target
        || !translation_menu_current() || !active()
        || overlay.revision != tl_integer(data, "revision", -1)
        || g_strcmp0(overlay.generation, tl_string(data, "generation")) != 0
        || !id || !*id || !token || !json_object_equal(token, overlay.menu_token)
        || tl_integer(entry, "settingsRevision", -1) != overlay.menu_settings_revision
        || !glossary_directions_valid(directions)
        || source_origin != (g_strcmp0(tl_string(overlay.view, "state"), "EditingTranslation") != 0)) {
        if (overlay.target == target && overlay.revision == tl_integer(data, "revision", -1)
            && g_strcmp0(overlay.generation, tl_string(data, "generation")) == 0)
            overlay.pending_glossary = FALSE;
        glossary_reject_open(target, data, id);
        return;
    }
    overlay.pending_glossary = FALSE;
    tl_overlay_control_cancel();
    GlossaryForm *form = g_new0(GlossaryForm, 1);
    form->refs = 1; form->reported_ime = -2;
    form->id = g_strdup(id); form->target = g_strdup(target->id); form->generation = g_strdup(overlay.generation);
    form->revision = overlay.revision; form->settings_revision = tl_integer(entry, "settingsRevision", -1);
    form->token = tl_copy(token); form->directions = json_array_ref(directions);
    form->source_origin = source_origin;
    form->origin_state = g_strdup(tl_string(overlay.view, "state"));
    form->origin = target; form->origin_top = overlay.top; form->origin_buffer = target->buffer;
    form->origin_client = target->client;
    if (source_origin) {
        form->source_authority = tl_copy(overlay.authority);
        form->source_view = tl_copy(overlay.view);
    }
    form->box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 6); g_object_ref_sink(form->box);
    /* Mark before creating/parenting editable children: discovery never sees
     * these controls as external source fields. */
    g_object_set_data(G_OBJECT(form->box), "typelay-overlay", GINT_TO_POINTER(1));
    gtk_widget_set_name(form->box, "typelay-glossary-form");
    gtk_widget_set_margin_start(form->box, 12); gtk_widget_set_margin_end(form->box, 12);
    gtk_widget_set_margin_top(form->box, 8); gtk_widget_set_margin_bottom(form->box, 12);
    const char *names[] = {"源词", "译词"};
    for (guint index = 0; index < 2; index++) {
        GtkWidget *label = gtk_label_new(names[index]); gtk_label_set_xalign(GTK_LABEL(label), 0);
        gtk_box_append(GTK_BOX(form->box), label);
        form->buffers[index] = g_object_new(tl_gtk4_glossary_buffer_get_type(), NULL);
        form->terms[index] = gtk_entry_new_with_buffer(GTK_ENTRY_BUFFER(form->buffers[index]));
        g_object_unref(form->buffers[index]);
        /* Parent before realization so the production target scanner excludes it. */
        gtk_box_append(GTK_BOX(form->box), form->terms[index]);
        form->clients[index] = GTK_WIDGET(gtk_editable_get_delegate(GTK_EDITABLE(form->terms[index])));
        gtk_widget_set_name(form->terms[index], index == 0 ? "typelay-glossary-source" : "typelay-glossary-target");
        gtk_entry_set_placeholder_text(GTK_ENTRY(form->terms[index]), names[index]);
        g_signal_connect(form->terms[index], "changed", G_CALLBACK(glossary_term_changed), form);
        g_signal_connect(form->clients[index], "preedit-changed", G_CALLBACK(glossary_preedit), form);
    }
    form->scope = gtk_button_new_with_label("全局 ▾");
    gtk_widget_set_name(form->scope, "typelay-glossary-scope");
    gtk_box_append(GTK_BOX(form->box), form->scope);
    form->scope_popup = gtk_popover_new();
    gtk_widget_set_parent(form->scope_popup, form->scope);
    g_object_ref_sink(form->scope_popup);
    gtk_popover_set_autohide(GTK_POPOVER(form->scope_popup), FALSE);
    g_object_set_data(G_OBJECT(form->scope_popup), "typelay-overlay", GINT_TO_POINTER(1));
    form->scope_choices = gtk_box_new(GTK_ORIENTATION_VERTICAL, 0);
    form->scope_options = g_ptr_array_new_with_free_func(g_object_unref);
    GtkWidget *scope_scroll = gtk_scrolled_window_new();
    gtk_widget_set_focusable(scope_scroll, FALSE);
    gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(scope_scroll), GTK_POLICY_NEVER, GTK_POLICY_AUTOMATIC);
    gtk_scrolled_window_set_max_content_height(GTK_SCROLLED_WINDOW(scope_scroll), 240);
    gtk_scrolled_window_set_propagate_natural_height(GTK_SCROLLED_WINDOW(scope_scroll), TRUE);
    gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(scope_scroll), form->scope_choices);
    gtk_popover_set_child(GTK_POPOVER(form->scope_popup), scope_scroll);
    for (guint index = 0; index <= json_array_get_length(directions); index++) {
        JsonObject *direction = index ? json_array_get_object_element(directions, index - 1) : NULL;
        char *label = direction ? g_strdup_printf("%s → %s", tl_string(direction, "sourceName"), tl_string(direction, "targetName")) : g_strdup("全局");
        GtkWidget *choice = gtk_button_new_with_label(label); g_free(label);
        gtk_box_append(GTK_BOX(form->scope_choices), choice);
        g_ptr_array_add(form->scope_options, g_object_ref(choice));
        g_signal_connect(choice, "clicked", G_CALLBACK(glossary_scope_clicked), form);
    }
    g_signal_connect(form->scope, "clicked", G_CALLBACK(glossary_scope_clicked), form);
    GtkEventController *scope_keys = gtk_event_controller_key_new();
    gtk_event_controller_set_propagation_phase(scope_keys, GTK_PHASE_CAPTURE);
    g_signal_connect(scope_keys, "key-pressed", G_CALLBACK(glossary_key_event), NULL);
    gtk_widget_add_controller(form->scope_popup, scope_keys);
    form->feedback = gtk_label_new("仅保存明确填写的两个词，不从译文自动学习");
    gtk_label_set_xalign(GTK_LABEL(form->feedback), 0);
    gtk_label_set_wrap(GTK_LABEL(form->feedback), TRUE);
    gtk_box_append(GTK_BOX(form->box), form->feedback);
    GtkWidget *buttons = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
    form->save = gtk_button_new_with_label("保存"); form->cancel = gtk_button_new_with_label("取消");
    gtk_widget_set_name(form->save, "typelay-glossary-save"); gtk_widget_set_name(form->cancel, "typelay-glossary-cancel");
    g_signal_connect(form->save, "clicked", G_CALLBACK(glossary_clicked), form);
    g_signal_connect(form->cancel, "clicked", G_CALLBACK(glossary_clicked), form);
    gtk_box_append(GTK_BOX(buttons), form->save); gtk_box_append(GTK_BOX(buttons), form->cancel);
    gtk_box_append(GTK_BOX(form->box), buttons);
    GtkEventController *physical = gtk_event_controller_legacy_new();
    gtk_event_controller_set_propagation_phase(physical, GTK_PHASE_CAPTURE);
    gtk_event_controller_set_propagation_limit(physical, GTK_LIMIT_NONE);
    g_signal_connect(physical, "event", G_CALLBACK(glossary_dispatch), NULL);
    gtk_widget_add_controller(form->box, physical);
    GtkEventController *keys = gtk_event_controller_key_new();
    gtk_event_controller_set_propagation_phase(keys, GTK_PHASE_BUBBLE);
    g_signal_connect(keys, "key-pressed", G_CALLBACK(glossary_key_event), NULL);
    gtk_widget_add_controller(form->box, keys);
    /* Widget setters can dispatch callbacks while the form is still detached.
     * Recheck the initiating editor before attaching or taking its focus. */
    if (overlay.glossary || !overlay.top || !translation_menu_current() || !active()
        || overlay.target != target || overlay.revision != form->revision
        || g_strcmp0(overlay.generation, form->generation) != 0
        || gtk_window_get_focus(GTK_WINDOW(overlay.top)) != (source_origin ? target->client : overlay.text)
        || (source_origin && !glossary_source_ready())) {
        glossary_reject_open(target, data, id);
        glossary_unref(form);
        return;
    }
    overlay.glossary = form;
    if (overlay.glossary_draft_token) json_object_unref(overlay.glossary_draft_token);
    overlay.glossary_draft_token = tl_copy(form->token);
    glossary_ref(form);
    gtk_box_append(GTK_BOX(overlay.card), form->box);
    if (glossary_current(form)) gtk_widget_set_visible(overlay.menu, FALSE);
    if (glossary_current(form)) {
        if (source_origin) {
            if (!glossary_transfer(form, FALSE)) glossary_close(FALSE, TRUE);
        } else gtk_widget_grab_focus(form->terms[0]);
    }
    if (glossary_current(form) && overlay.target == target) {
        /* The guarded source handoff suppresses focus notifications. Publish
         * the actual auxiliary focus before a following renew reads its cache. */
        if (source_origin) tl_overlay_sync();
        else glossary_sync();
    }
    glossary_unref(form);
}
static void glossary_result(Target *target, JsonObject *data) {
    GlossaryForm *form = glossary_ref(overlay.glossary);
    if (!glossary_current(form) || overlay.target != target || !form->pending
        || g_strcmp0(form->id, tl_string(data, "formId")) != 0
        || form->revision != tl_integer(data, "revision", -1)
        || g_strcmp0(form->generation, tl_string(data, "generation")) != 0) goto done;
    JsonObject *result = tl_object(data, "result");
    JsonNode *saved = result ? json_object_get_member(result, "saved") : NULL;
    const char *code = tl_string(result, "code");
    gboolean known = saved && JSON_NODE_HOLDS_VALUE(saved) && json_node_get_value_type(saved) == G_TYPE_BOOLEAN;
    gboolean success = known && json_node_get_boolean(saved);
    gboolean unknown = !known || g_strcmp0(code, "glossary-unconfirmed") == 0
        || (code && strstr(code, "unknown"));
    form->pending = FALSE; form->finished = success || unknown;
    const char *message = success ? (g_strcmp0(code, "glossary-saved-notification-failed") == 0
        ? "术语已保存，通知更新失败" : "术语已保存，将用于下一次机器翻译")
        : unknown ? "保存结果未知，请检查术语表后再操作"
        : g_strcmp0(code, "glossary-conflict") == 0 ? "相同范围的源词已有不同译法，请修改后保存"
        : g_strcmp0(code, "glossary-stale") == 0 ? "保存上下文已变化，请关闭后重新加入"
        : "术语未保存，请检查词条和作用范围后重试";
    const char *provided = tl_string(result, "message");
    gtk_label_set_text(GTK_LABEL(form->feedback), provided && *provided ? provided : message);
    if (glossary_current(form) && !form->finished) glossary_freeze_inputs(form, FALSE);
    /* Keep the focused Save control in the family. The finished/pending guard
     * blocks duplicate saves without destroying keyboard navigation. */
    if (glossary_current(form) && success) gtk_button_set_label(GTK_BUTTON(form->cancel), "关闭");
done:
    glossary_unref(form);
}

void tl_overlay_sync(void) {
    if (glossary_handoff) return;
    glossary_sync();
    GtkWidget *focus = overlay.top ? gtk_window_get_focus(GTK_WINDOW(overlay.top)) : NULL;
    gboolean focused = active() && overlay.popover && gtk_widget_get_visible(overlay.popover)
        && (overlay.glossary ? focus == overlay.text || glossary_contains(overlay.glossary, focus)
            : tl_overlay_contains(focus));
    if (focused != overlay.focused) {
        tl_overlay_control_cancel();
        overlay.focused = focused;
        emit_boolean("focus", focused);
    }
}
static void focus_changed(GObject *object, GParamSpec *property, gpointer data) {
    if (glossary_handoff) return;
    (void)object; (void)property; (void)data;
    GtkWidget *focus = overlay.top ? gtk_window_get_focus(GTK_WINDOW(overlay.top)) : NULL;
    if (overlay.language && !overlay.language_focus_transfer && focus != overlay.language)
        gtk_widget_set_focusable(overlay.language, FALSE);
    /* notify may expose a transient NULL during an internal focus transfer.
     * It is not proof of an outside destination; the original safety gates
     * still revoke focus/authority without actively closing a child here. */
    if (focus) close_language_popups_outside(focus, FALSE);
    tl_overlay_control_cancel();
    if (overlay.top && gtk_window_get_focus(GTK_WINDOW(overlay.top)) != overlay.text) overlay.pending_glossary = FALSE;
    tl_overlay_sync();
    if (overlay.glossary && overlay.glossary->source_origin
        && (overlay.glossary->return_started || (focus && !glossary_contains(overlay.glossary, focus)))) {
        hide_overlay("glossary-focus-departure");
        return;
    }
    if (active() && tl_focused() != overlay.target && (paused() || tl_focused())) {
        hide_overlay("source-focus-changed");
    }
}
static void active_changed(GObject *object, GParamSpec *property, gpointer data) {
    (void)object; (void)property; (void)data;
    tl_overlay_control_cancel();
    if (!active()) {
        hide_overlay("window-inactive");
    }
}
static gboolean expired(gpointer data) {
    (void)data;
    gint64 now = tl_now();
    const char *reason = !overlay.popover ? "timer-missing-popover"
        : now > overlay.expires ? "timer-expired"
        : paused() && !pause_current(overlay.target, overlay.authority) ? "timer-paused-authority" : NULL;
    if (reason) {
        overlay.expiry_timer = 0;
        hide_overlay(reason);
        return G_SOURCE_REMOVE;
    }
    glossary_sync();
    return G_SOURCE_CONTINUE;
}
/* A geometry request belongs only to the opening/view which produced it.
 * Detach before unref so reentrant GTK callbacks cannot erase a newer request. */
static void clear_layout_request(void) {
    GtkWidget *popover = overlay.layout_popover;
    JsonObject *view = overlay.layout_view;
    overlay.layout_popover = NULL; overlay.layout_view = NULL; overlay.layout_revision = 0;
    if (view) json_object_unref(view);
    if (popover) g_object_unref(popover);
}
static void hide_overlay(const char *reason) {
    close_more(FALSE);
    clear_layout_request();
    /* Branch-local evidence only: no new focus/IME probe and no user text. */
    static unsigned trace_count;
    if (g_getenv("TYPELAY_TRACE") && overlay.popover && trace_count < 96) {
        trace_count++;
        g_printerr("TypeLay GTK4 overlay-hide reason=%s atMs=%" G_GINT64_FORMAT
            " expiresAtMs=%" G_GINT64_FORMAT " revision=%" G_GINT64_FORMAT
            " focused=%d glossary=%d\n", reason, tl_now(), overlay.expires,
            overlay.revision, overlay.focused, overlay.glossary != NULL);
    }
    tl_overlay_control_cancel();
    glossary_close(FALSE, TRUE);
    /* Clear state before destroy: source destruction and GTK callbacks are reentrant. */
    if (overlay.focused) emit_boolean("focus", FALSE);
    if (overlay.popover) emit_boolean("visible", FALSE);
    if (overlay.composing) emit_boolean("composition", FALSE);
    GtkWidget *popover = overlay.popover, *top = overlay.top;
    GtkWidget *language = overlay.language ? g_object_ref(overlay.language) : NULL;
    gulong focus_handler = overlay.focus_handler, active_handler = overlay.active_handler;
    GtkCssProvider *css = overlay.css;
    GdkDisplay *display = overlay.display;
    GtkEventController *language_pointer = overlay.language_pointer;
    JsonObject *view = overlay.view, *authority = overlay.authority;
    char *generation = overlay.generation;
    if (overlay.expiry_timer) g_source_remove(overlay.expiry_timer);
    clear_language_menu(&overlay.source_languages);
    clear_language_menu(&overlay.target_languages);
    clear_translation_menu();
    if (overlay.glossary_draft_token) json_object_unref(overlay.glossary_draft_token);
    if (overlay.glossary_draft_buffer) g_object_unref(overlay.glossary_draft_buffer);
    if (overlay.layout_handler) g_signal_handler_disconnect(overlay.layout_clock, overlay.layout_handler);
    g_clear_object(&overlay.layout_clock);
    memset(&overlay, 0, sizeof overlay);
    if (top) {
        if (language_pointer) gtk_widget_remove_controller(top, language_pointer);
        if (focus_handler) g_signal_handler_disconnect(top, focus_handler);
        if (active_handler) g_signal_handler_disconnect(top, active_handler);
        g_object_remove_weak_pointer(G_OBJECT(top), (gpointer *)&overlay.top);
    }
    if (language_pointer) g_object_unref(language_pointer);
    if (language) {
        gtk_widget_set_focusable(language, FALSE);
        g_object_unref(language);
    }
    if (popover) {
        gtk_popover_popdown(GTK_POPOVER(popover));
        if (gtk_widget_get_parent(popover)) gtk_widget_unparent(popover);
        g_object_unref(popover);
    }
    if (css && display) gtk_style_context_remove_provider_for_display(display, GTK_STYLE_PROVIDER(css));
    if (css) g_object_unref(css);
    if (display) g_object_unref(display);
    if (view) json_object_unref(view);
    if (authority) json_object_unref(authority);
    g_free(generation);
}
void tl_overlay_hide(void) { language_configuration_close(TRUE); hide_overlay("external-revoke"); }
void tl_overlay_forget(Target *target) {
    if (overlay.target == target) hide_overlay("target-forgotten");
}
static char *candidate(void) {
    GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text));
    GtkTextIter start, end;
    gtk_text_buffer_get_bounds(buffer, &start, &end);
    return gtk_text_buffer_get_text(buffer, &start, &end, TRUE);
}
static gint measured_body_height(const char *text, gint width) {
    PangoLayout *layout = gtk_widget_create_pango_layout(overlay.text, text);
    pango_layout_set_width(layout, MAX(1, width - 30) * PANGO_SCALE);
    pango_layout_set_wrap(layout, PANGO_WRAP_WORD_CHAR);
    gint height;
    pango_layout_get_pixel_size(layout, NULL, &height);
    g_object_unref(layout);
    gint collapsed = (overlay.text_size * 155 * 7 + 99) / 100 + 4;
    return CLAMP(height + 16, 36, overlay.expanded ? 360 : collapsed);
}
static void resize_owned_draft(GtkTextBuffer *buffer) {
    /* Ordinary draft changes do not ask the host for another presentation.
     * Resize only this live native buffer, never synthesize a render/token. */
    if (!active() || !overlay.text || !overlay.card || !overlay.scroll || !overlay.expand
        || overlay.setting_text || !tl_configuration_current(overlay.generation)
        || !tl_view_context_matches(overlay.target, overlay.view)
        || !boolean(tl_object(overlay.view, "presentation"), "editorOwnsText")
        || buffer != gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text))
        || !gtk_text_view_get_editable(GTK_TEXT_VIEW(overlay.text))) return;
    GtkWidget *editor = g_object_ref(overlay.text), *scroll = g_object_ref(overlay.scroll);
    GtkWidget *card = g_object_ref(overlay.card), *expand = g_object_ref(overlay.expand);
    JsonObject *view = json_object_ref(overlay.view);
    gint width = 0;
    gtk_widget_get_size_request(card, &width, NULL);
    char *text = candidate();
    gint height = measured_body_height(text, MAX(1, width));
    g_free(text);
    if (overlay.view == view && overlay.text == editor && overlay.scroll == scroll
        && overlay.card == card && overlay.expand == expand && active()) {
        gint collapsed = (overlay.text_size * 155 * 7 + 99) / 100 + 4;
        gtk_widget_set_visible(expand, overlay.expanded || height >= collapsed);
        if (overlay.view == view && overlay.text == editor && overlay.scroll == scroll
            && overlay.card == card && active()) {
            gtk_widget_set_size_request(scroll, -1, height);
        }
    }
    json_object_unref(view);
    g_object_unref(expand); g_object_unref(card); g_object_unref(scroll); g_object_unref(editor);
}
static void draft_changed(GtkTextBuffer *buffer, gpointer data) {
    (void)buffer; (void)data;
    tl_overlay_control_cancel();
    if (!overlay.setting_text && overlay.text
        && buffer == gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text))
        && tl_configuration_current(overlay.generation) && tl_view_context_matches(overlay.target, overlay.view)
        && gtk_text_view_get_editable(GTK_TEXT_VIEW(overlay.text))) {
        char *text = candidate();
        emit_string("draft", text);
        g_free(text);
        resize_owned_draft(buffer);
    }
}
static void limit_draft(GtkTextBuffer *buffer, GtkTextIter *location, gchar *text, gint length, gpointer data) {
    (void)location; (void)data;
    if (overlay.setting_text) return;
    char *current = candidate();
    glong existing = 0, inserted = 0;
    gunichar2 *a = g_utf8_to_utf16(current, -1, NULL, &existing, NULL);
    gunichar2 *b = g_utf8_to_utf16(text, length, NULL, &inserted, NULL);
    if (!a || !b || existing + inserted > TL_MAX_TEXT) {
        g_signal_stop_emission_by_name(buffer, "insert-text");
        gtk_label_set_text(GTK_LABEL(overlay.hint), "译文超过 8192 字符限制，此次插入未生效");
    }
    g_free(a); g_free(b); g_free(current);
}
static void preedit(GtkWidget *widget, const gchar *text, gpointer data) {
    (void)data;
    tl_overlay_control_cancel();
    if (widget != overlay.text || !tl_configuration_current(overlay.generation)) return;
    overlay.composing = text && *text;
    emit_boolean("composition", overlay.composing);
}
static void begin_edit(GtkGestureClick *gesture, gint presses, gdouble x, gdouble y, gpointer data) {
    (void)presses; (void)data;
    GtkWidget *widget = gtk_event_controller_get_widget(GTK_EVENT_CONTROLLER(gesture));
    if (widget != overlay.text || gtk_text_view_get_editable(GTK_TEXT_VIEW(widget))) return;
    /* The preview click requests Runtime authorization before GTK may focus or edit. */
    gtk_gesture_set_state(GTK_GESTURE(gesture), GTK_EVENT_SEQUENCE_CLAIMED);
    if (paused() || !boolean(overlay.view, "editable") || overlay.pending_edit
        || !tl_configuration_current(overlay.generation)
        || !tl_view_context_matches(overlay.target, overlay.view)
        || !boolean(tl_object(overlay.view, "viewContext"), "canEdit")) return;
    gint buffer_x, buffer_y;
    GtkTextIter position;
    gtk_text_view_window_to_buffer_coords(GTK_TEXT_VIEW(widget), GTK_TEXT_WINDOW_WIDGET,
        (gint)x, (gint)y, &buffer_x, &buffer_y);
    gtk_text_view_get_iter_at_location(GTK_TEXT_VIEW(widget), &position, buffer_x, buffer_y);
    overlay.edit_offset = gtk_text_iter_get_offset(&position);
    overlay.pending_edit = TRUE;
    emit("beginEdit", NULL);
}
static gboolean key_event(GtkEventControllerLegacy *controller, GdkEvent *event, gpointer data) {
    (void)controller; (void)data;
    return tl_dispatch_key(event);
}
static void translation_action_clicked(const char *operation, JsonObject *view) {
    if (!more_current() || !gtk_widget_get_mapped(overlay.more_popup) || !translation_menu_current()) {
        if (strcmp(operation, "glossary") == 0)
            trace_glossary_action("reject-translation-current", overlay.authority, -1, -1);
        return;
    }
    GtkWidget *popup = g_object_ref(overlay.more_popup);
    JsonObject *token = json_object_ref(overlay.menu_token);
    JsonObject *context = json_object_ref(tl_object(view, "viewContext"));
    gboolean glossary = strcmp(operation, "glossary") == 0;
    if (glossary && (!glossary_origin_ready() || !more_render_current(view, context, popup)
        || overlay.menu_token != token || !gtk_widget_get_mapped(popup)
        || !translation_menu_current())) {
        trace_glossary_action("reject-origin-or-opening", overlay.authority, -1, -1);
        goto done;
    }
    JsonObject *state = translation_actions(view);
    gboolean alternative = strcmp(operation, "alternative") == 0;
    gboolean inherit = strcmp(operation, "style-inherit") == 0;
    if (glossary ? !boolean(state, "canOpenGlossary")
        || overlay.pending_glossary || overlay.glossary
        : alternative ? !boolean(state, "alternativeReleased") || !boolean(state, "canRequestAlternative")
        : (!inherit && !boolean(state, "styleReleased"))
            || !boolean(state, inherit ? "canInheritStyle" : "canChangeStyle")) {
        if (glossary) trace_glossary_action("reject-qualified-state", overlay.authority, -1, -1);
        goto done;
    }
    /* The bridge resolves this exact transport record to its original complete
     * session token. Never emit() here: it reads the latest overlay identity. */
    JsonObject *event = json_object_new();
    json_object_set_string_member(event, "target", overlay.menu_target);
    json_object_set_string_member(event, "operation", glossary ? "sessionGlossaryBegin" : alternative ? "alternative" : "style");
    json_object_set_string_member(event, "generation", overlay.menu_generation);
    json_object_set_int_member(event, "revision", overlay.menu_revision);
    if (alternative || glossary) json_object_set_null_member(event, "value");
    else {
        JsonObject *value = json_object_new();
        json_object_set_int_member(value, "settingsRevision", overlay.menu_settings_revision);
        if (inherit) json_object_set_null_member(value, "style");
        else json_object_set_string_member(value, "style", strcmp(operation, "style-natural") == 0 ? "Natural" : "Formal");
        json_object_set_object_member(event, "value", value);
    }
    if (glossary) {
        overlay.pending_glossary = TRUE;
        /* The host withdraws acceptance and renders before replying with the
         * form. Preserve this original buffer even during that first repaint. */
        if (g_strcmp0(tl_string(view, "state"), "EditingTranslation") != 0) {
            if (overlay.glossary_draft_token) json_object_unref(overlay.glossary_draft_token);
            overlay.glossary_draft_token = tl_copy(token);
            if (overlay.glossary_draft_buffer) g_object_unref(overlay.glossary_draft_buffer);
            overlay.glossary_draft_buffer = g_object_ref(gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text)));
        }
    }

    close_more(TRUE);
    if (translation_render_current(view, context) && overlay.menu_token == token && active() && translation_menu_current()) {
        tl_send_event("ui", event);
        /* Post-send state only, not the frozen authorization snapshot. */
        if (glossary) trace_glossary_action("sent", overlay.authority, -1, -1);
    } else {
        if (glossary) trace_glossary_action("reject-postclose", overlay.authority, -1, -1);
        json_object_unref(event);
    }
    if (!glossary && overlay.menu_token == token && !overlay.more_popup) clear_translation_menu();
done:
    json_object_unref(context); json_object_unref(token); g_object_unref(popup);
}
/* Observe only an attempted resume, not every paused-view renewal. */
static void trace_resume(GtkWidget *widget, gpointer data) {
    static unsigned count;
    const char *stage = data;
    if (!g_getenv("TYPELAY_TRACE") || count >= 8) return;
    if (strcmp(stage, "activate") == 0)
        g_object_set_data(G_OBJECT(widget), "typelay-resume-activated", GINT_TO_POINTER(1));
    if (strcmp(stage, "unmap") == 0
        && !g_object_get_data(G_OBJECT(widget), "typelay-resume-activated")) return;
    count++;
    g_printerr("TypeLay GTK4 resume stage=%s atMs=%" G_GINT64_FORMAT
        " current=%d mapped=%d revision=%" G_GINT64_FORMAT "\n",
        stage, tl_now(), widget == overlay.resume, gtk_widget_get_mapped(widget), overlay.revision);
}
/* Display may remain open during preedit. An action requires one real
 * current observation and cannot borrow a replacement opening after reentry. */
static gboolean more_action_ready(GtkWidget *popup, JsonObject *view, JsonObject *token, gboolean trace) {
    if (!more_current() || overlay.more_popup != popup || overlay.view != view
        || overlay.menu_token != token || !translation_menu_current()) {
        if (trace) trace_glossary_action("reject-more-current", overlay.authority, -1, -1);
        return FALSE;
    }
    Target *target = overlay.target;
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(overlay.top));
    if (!focus) {
        if (trace) trace_glossary_action("reject-more-focus", overlay.authority, -1, -1);
        return FALSE;
    }
    g_object_ref(focus);
    gboolean clear = focus == overlay.text ? !overlay.composing && tl_widget_ime_clear(focus)
        : target && !target->dead && focus == target->client && tl_focused() == target && tl_ime_clear(target);
    gboolean current = clear && more_current() && overlay.more_popup == popup && overlay.view == view
        && overlay.menu_token == token && overlay.target == target && translation_menu_current()
        && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == focus
        && (focus != overlay.text || !overlay.composing);
    if (trace && !current) trace_glossary_action("reject-more-proof", overlay.authority, clear, -1);
    g_object_unref(focus);
    return current;
}
static void toggle_more(JsonObject *view);
static void button_clicked(GtkButton *button, gpointer data) {
    const char *operation = data;
    gboolean glossary_action = strcmp(operation, "glossary") == 0;
    if (glossary_action) trace_glossary_action("clicked", overlay.authority, -1, -1);
    if (strcmp(operation, "resume") == 0) trace_resume(GTK_WIDGET(button), "clicked");
    gboolean from_more = overlay.more_popup && gtk_widget_is_ancestor(GTK_WIDGET(button), overlay.more_popup);
    if (!overlay.popover || (!from_more && !gtk_widget_is_ancestor(GTK_WIDGET(button), overlay.popover))) {
        if (strcmp(operation, "resume") == 0) trace_resume(GTK_WIDGET(button), "reject-owner");
        if (glossary_action) trace_glossary_action("reject-owner", overlay.authority, -1, -1);
        return;
    }
    if (strcmp(operation, "resume") == 0) {
        Target *target = overlay.target;
        gint64 revision = overlay.revision;
        if (GTK_WIDGET(button) != overlay.resume || !paused() || overlay.pending_resume) {
            trace_resume(GTK_WIDGET(button), "reject-state"); return;
        }
        const char *rejected = !active() ? "reject-active"
            : tl_now() > overlay.expires ? "reject-expired"
            : !pause_current(target, overlay.authority) ? "reject-authority" : NULL;
        if (rejected) {
            trace_resume(GTK_WIDGET(button), (gpointer)rejected);
            hide_overlay(rejected); return;
        }
        if (overlay.target != target || overlay.revision != revision || !paused() || tl_now() > overlay.expires) {
            trace_resume(GTK_WIDGET(button), "reject-reentry"); return;
        }
        trace_resume(GTK_WIDGET(button), "emit");
        overlay.pending_resume = TRUE;
        gtk_widget_set_sensitive(overlay.resume, FALSE);
        emit("resume", NULL);
        return;
    }
    if (paused() || overlay.glossary || !tl_configuration_current(overlay.generation)) {
        if (glossary_action) trace_glossary_action("reject-view", overlay.authority, -1, -1);
        return;
    }
    if (from_more && (!more_current() || !gtk_widget_get_mapped(GTK_WIDGET(button))
        || !gtk_widget_is_sensitive(GTK_WIDGET(button)))) {
        if (glossary_action) trace_glossary_action("reject-control", overlay.authority, -1, -1);
        return;
    }
    /* Source view/configuration changes replace this identity. The menu-only
       projection preserves it without changing the original token or revision. */
    JsonObject *clicked_view = json_object_ref(overlay.view);
    GtkWidget *clicked_popup = from_more ? g_object_ref(overlay.more_popup) : NULL;
    JsonObject *clicked_token = from_more && overlay.menu_token ? json_object_ref(overlay.menu_token) : NULL;
    Target *clicked_target = overlay.target;
    gint64 clicked_revision = overlay.revision;
    GtkWidget *clicked_focus = from_more ? gtk_window_get_focus(GTK_WINDOW(overlay.top)) : NULL;
    if (clicked_focus) g_object_ref(clicked_focus);
    if (from_more && !more_action_ready(clicked_popup, clicked_view, clicked_token, glossary_action)) goto done;
    if (strcmp(operation, "more") == 0) {
        if (GTK_WIDGET(button) == overlay.more) toggle_more(clicked_view);
    } else if (strcmp(operation, "menu") == 0) {
        begin_language_configuration(GTK_WIDGET(button), clicked_view);
    } else if (strcmp(operation, "style-inherit") == 0 || strcmp(operation, "style-natural") == 0
        || strcmp(operation, "style-formal") == 0 || strcmp(operation, "alternative") == 0
        || strcmp(operation, "glossary") == 0) {
        translation_action_clicked(operation, clicked_view);
    } else if (strcmp(operation, "expand") == 0) {
        gboolean expanded = overlay.expanded = !overlay.expanded;
        gtk_button_set_label(GTK_BUTTON(overlay.expand), expanded ? "收起" : "展开");
        if (overlay.view == clicked_view && tl_configuration_current(overlay.generation))
            emit_boolean("expand", expanded);
    } else if (strcmp(operation, "copy") == 0) {
        char *text = candidate();
        gdk_clipboard_set_text(gtk_widget_get_clipboard(overlay.text), text);
        g_free(text);
        if (overlay.view == clicked_view && overlay.more_popup == clicked_popup
            && overlay.menu_token == clicked_token) close_more(FALSE);
    } else if (strcmp(operation, "languages") == 0) {
        const char *source = selected_language(overlay.source, &overlay.source_languages);
        const char *target = selected_language(overlay.destination, &overlay.target_languages);
        if (overlay.setting_languages || !overlay.source_languages.choices || !overlay.target_languages.choices || !source || !target) {
            goto done;
        }
        JsonObject *value = json_object_new();
        json_object_set_string_member(value, "source", source);
        json_object_set_string_member(value, "target", target);
        JsonNode *node = json_node_new(JSON_NODE_OBJECT);
        json_node_take_object(node, value);
        emit(operation, node);
        if (overlay.view == clicked_view) close_language_menu(clicked_view);
    } else {
        /* Preserve only this opening's ticket across popdown callbacks. A new
         * opening replaces it, even if its view/token values happen to match. */
        if (from_more) close_more(TRUE);
        else gtk_widget_set_visible(overlay.menu, FALSE);
        if (overlay.view == clicked_view && overlay.target == clicked_target
            && overlay.revision == clicked_revision && tl_configuration_current(overlay.generation)
            && (!from_more || (!overlay.more_popup && overlay.menu_token == clicked_token
                && translation_menu_current() && active()
                && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == clicked_focus
                && (clicked_focus != overlay.text || !overlay.composing)))) emit(operation, NULL);
        if (from_more && !overlay.more_popup && overlay.menu_token == clicked_token) clear_translation_menu();
    }
done:
    if (clicked_focus) g_object_unref(clicked_focus);
    if (clicked_token) json_object_unref(clicked_token);
    if (clicked_popup) g_object_unref(clicked_popup);
    json_object_unref(clicked_view);
}
static GtkWidget *button(GtkWidget *box, const char *label, const char *operation) {
    GtkWidget *widget = gtk_button_new_with_label(label);
    gtk_button_set_has_frame(GTK_BUTTON(widget), FALSE);
    gtk_widget_set_focusable(widget, FALSE);
    gtk_widget_set_focus_on_click(widget, FALSE);
    g_signal_connect(widget, "clicked", G_CALLBACK(button_clicked), (gpointer)operation);
    gtk_box_append(GTK_BOX(box), widget);
    return widget;
}
static void collect_ui_widgets(GtkWidget *widget, gpointer data) {
    g_ptr_array_add((GPtrArray *)data, g_object_ref(widget));
    for (GtkWidget *child = gtk_widget_get_first_child(widget); child; child = gtk_widget_get_next_sibling(child))
        collect_ui_widgets(child, data);
}
static gboolean localize_widgets(GtkWidget *card, JsonObject *view) {
    GPtrArray *widgets = g_ptr_array_new_with_free_func(g_object_unref);
    collect_ui_widgets(card, widgets);
    for (guint i = 0; i < widgets->len && overlay.card == card && overlay.view == view; i++) {
        GtkWidget *widget = g_ptr_array_index(widgets, i);
        const char *label = GTK_IS_BUTTON(widget) ? gtk_button_get_label(GTK_BUTTON(widget))
            : GTK_IS_LABEL(widget) ? gtk_label_get_text(GTK_LABEL(widget)) : NULL;
        if (widget == overlay.more) {
            const char *name = tl_overlay_ui("更多", overlay.english);
            gtk_widget_set_tooltip_text(widget, name);
            gtk_accessible_update_property(GTK_ACCESSIBLE(widget), GTK_ACCESSIBLE_PROPERTY_LABEL, name, -1);
        }
        if (!label) continue;
        const char *translated = tl_overlay_ui(label, overlay.english);
        if (strcmp(label, translated) == 0) continue;
        char *copy = g_strdup(translated);
        if (GTK_IS_BUTTON(widget)) gtk_button_set_label(GTK_BUTTON(widget), copy);
        else gtk_label_set_text(GTK_LABEL(widget), copy);
        g_free(copy);
    }
    gboolean current = overlay.card == card && overlay.view == view;
    g_ptr_array_unref(widgets);
    return current;
}
typedef struct { const char *paths; GdkRGBA color; GdkPixbuf *pixels; int size; gboolean own_color; } MoreIcon;
static void free_more_icon(gpointer data) {
    MoreIcon *icon = data;
    g_clear_object(&icon->pixels);
    g_free(icon);
}
static void draw_more_icon(GtkDrawingArea *area, cairo_t *cr, int width, int height, gpointer data) {
    (void)width; (void)height;
    GtkWidget *widget = GTK_WIDGET(area);
    MoreIcon *icon = data;
    GtkWidget *button = gtk_widget_get_ancestor(widget, GTK_TYPE_BUTTON);
    GtkWidget *owner = icon->own_color ? widget : (button ? button : widget);
    GdkRGBA color;
#if GTK_CHECK_VERSION(4, 10, 0)
    gtk_widget_get_color(owner, &color);
#else
    gtk_style_context_get_color(gtk_widget_get_style_context(owner), &color);
#endif
    if (!icon->pixels || !gdk_rgba_equal(&icon->color, &color)) {
        /* Exact prototype paths, embedded and bounded. Cache each current
         * foreground instead of parsing SVG on every natural paint frame. */
        char *svg = g_strdup_printf("<svg xmlns='http://www.w3.org/2000/svg' width='%d' height='%d' viewBox='0 0 24 24' "
            "fill='none' stroke='#%02x%02x%02x' stroke-width='1.6' stroke-linecap='round' stroke-linejoin='round'>%s</svg>",
            icon->size, icon->size, (guint)(color.red * 255 + .5),
            (guint)(color.green * 255 + .5), (guint)(color.blue * 255 + .5), icon->paths);
        GdkPixbufLoader *loader = gdk_pixbuf_loader_new_with_type("svg", NULL);
        if (loader && gdk_pixbuf_loader_write(loader, (const guchar *)svg, strlen(svg), NULL)
            && gdk_pixbuf_loader_close(loader, NULL)) {
            g_set_object(&icon->pixels, gdk_pixbuf_loader_get_pixbuf(loader));
            icon->color = color;
        }
        g_clear_object(&loader); g_free(svg);
    }
    if (icon->pixels) { gdk_cairo_set_source_pixbuf(cr, icon->pixels, 0, 0); cairo_paint(cr); }
}
static GtkWidget *more_icon(const char *operation) {
    static const struct { const char *operation, *paths; } icons[] = {
        {"regenerate", "<path d='M20 5v5h-5M4 19v-5h5'/><path d='M19.2 10A7.5 7.5 0 0 0 6 7.2M4.8 14A7.5 7.5 0 0 0 18 16.8'/>"},
        {"alternative", "<path d='M12 3.5c.6 4.4 2.1 6 6.5 6.5-4.4.6-5.9 2.1-6.5 6.5-.6-4.4-2.1-5.9-6.5-6.5 4.4-.5 5.9-2.1 6.5-6.5Z'/><path d='M18.5 15.5c.2 1.6.8 2.2 2.4 2.4-1.6.2-2.2.8-2.4 2.4-.2-1.6-.8-2.2-2.4-2.4 1.6-.2 2.2-.8 2.4-2.4Z'/>"},
        {"copy", "<rect x='8' y='8' width='12' height='12' rx='2.5'/><path d='M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2'/>"},
        {"glossary", "<path d='M5 4.5h10.5A2.5 2.5 0 0 1 18 7v12.5H7.5A2.5 2.5 0 0 1 5 17Z'/><path d='M5 17a2.5 2.5 0 0 1 2.5-2.5H18M9 8.5h5'/>"},
        {"escape", "<path d='M3 3l18 18M10.6 5.2A9.6 9.6 0 0 1 12 5c5 0 8.5 4.5 9.5 7a13 13 0 0 1-2.9 4M6.4 6.5C4.4 7.8 3.1 9.8 2.5 12c1 2.5 4.5 7 9.5 7a9.4 9.4 0 0 0 4.6-1.2M9.9 9.9a3 3 0 0 0 4.2 4.2'/>"},
        {"exclude", "<circle cx='12' cy='12' r='8.5'/><path d='m6 6 12 12'/>"},
        {"lock", "<rect x='5' y='10.5' width='14' height='9.5' rx='2.5'/><path d='M8.5 10.5V8a3.5 3.5 0 0 1 7 0v2.5'/>"},
        {"style", "<path d='M4 19h4L19 8l-4-4L4 15Z'/><path d='m13 6 4 4'/>"},
        {"language-swap", "<path d='M4 8h15m-4-4 4 4-4 4M20 16H5m4-4-4 4 4 4'/>"},
        {"language-check", "<path d='m5 12.5 4.5 4.5L19 7.5'/>"},
        {"language-search", "<circle cx='11' cy='11' r='6'/><path d='m20 20-4.5-4.5'/>"}
    };
    for (guint index = 0; index < G_N_ELEMENTS(icons); index++) if (strcmp(operation, icons[index].operation) == 0) {
        GtkWidget *widget = gtk_drawing_area_new();
        gtk_widget_set_name(widget, "typelay-menu-icon");
        const char *icon_class = strcmp(operation, "language-swap") == 0 ? "language-swap-icon"
            : strcmp(operation, "language-check") == 0 ? "language-check-icon"
            : strcmp(operation, "language-search") == 0 ? "language-search-icon" : NULL;
        int size = icon_class ? (strcmp(operation, "language-swap") == 0 ? 15 : 14) : 16;
        gtk_widget_set_size_request(widget, size, size);
        gtk_widget_set_valign(widget, GTK_ALIGN_CENTER);
        if (icon_class) {
            gtk_widget_add_css_class(widget, icon_class);
            gtk_widget_set_halign(widget, GTK_ALIGN_CENTER);
        }
        MoreIcon *icon = g_new0(MoreIcon, 1);
        icon->paths = icons[index].paths;
        icon->size = size;
        icon->own_color = icon_class != NULL;
        gtk_drawing_area_set_draw_func(GTK_DRAWING_AREA(widget), draw_more_icon, icon, free_more_icon);
        gtk_widget_set_can_target(widget, FALSE);
        return widget;
    }
    return NULL;
}
/* A configuration opening owns only its parent and numerical anchor. The source
 * view, target, token, editor and key ticket were retired before this is sent. */
typedef struct {
    guint refs, timeout;
    GtkWidget *top, *popup, *search, *client, *list, *feedback, *swap;
    GtkCssProvider *css;
    GdkDisplay *display;
    GtkIMContext *context; /* weak existing provider binding, never our own IM */
    gulong ime_handler;
    GdkRectangle anchor;
    JsonArray *choices;
    char *id, *source, *target;
    gint64 expires, settings_revision;
    gboolean pending, saving, opening, retired, english, swap_allowed, clear, composing, probing;
} LanguageConfiguration;
static LanguageConfiguration *language_configuration;
static LanguageConfiguration *language_configuration_ref(LanguageConfiguration *configuration) {
    if (configuration) configuration->refs++;
    return configuration;
}
static void language_configuration_unref(gpointer data) {
    LanguageConfiguration *configuration = data;
    if (!configuration || --configuration->refs) return;
    if (configuration->choices) json_array_unref(configuration->choices);
    g_clear_object(&configuration->top);
    g_free(configuration->id); g_free(configuration->source); g_free(configuration->target);
    g_free(configuration);
}
static void language_configuration_closure_unref(gpointer data, GClosure *closure) {
    (void)closure; language_configuration_unref(data);
}
static void language_configuration_signal(gpointer instance, const char *signal, GCallback callback,
        LanguageConfiguration *configuration) {
    g_signal_connect_data(instance, signal, callback, language_configuration_ref(configuration),
        language_configuration_closure_unref, 0);
}
static gboolean language_configuration_current(LanguageConfiguration *configuration) {
    return configuration && language_configuration == configuration && !configuration->retired
        && !overlay.popover && configuration->top && gtk_widget_get_mapped(configuration->top)
        && gtk_window_is_active(GTK_WINDOW(configuration->top));
}
static gboolean language_configuration_contains(LanguageConfiguration *configuration, GtkWidget *widget) {
    return configuration && configuration->popup && widget
        && (widget == configuration->popup || gtk_widget_is_ancestor(widget, configuration->popup));
}
/* Branch-local, bounded failure evidence on the existing opt-in trace channel.
 * Never expose the opening id, source, search text or target identity. */
static void language_configuration_trace(LanguageConfiguration *configuration, const char *stage) {
    static guint count;
    if (!g_getenv("TYPELAY_TRACE") || !configuration || count >= 32) return;
    count++;
    GtkWidget *focus = configuration->top ? gtk_window_get_focus(GTK_WINDOW(configuration->top)) : NULL;
    const char *focus_kind = !focus ? "none" : focus == configuration->client ? "search"
        : language_configuration_contains(configuration, focus) ? "inside" : "outside";
    g_printerr("TypeLay GTK4 language-opening stage=%s atMs=%" G_GINT64_FORMAT
        " deadlineMs=%" G_GINT64_FORMAT " same=%d retired=%d pending=%d opening=%d saving=%d"
        " topMapped=%d topActive=%d popupMapped=%d popupVisible=%d focus=%s\n",
        stage, tl_now(), configuration->expires, language_configuration == configuration,
        configuration->retired, configuration->pending, configuration->opening, configuration->saving,
        configuration->top && gtk_widget_get_mapped(configuration->top),
        configuration->top && gtk_window_is_active(GTK_WINDOW(configuration->top)),
        configuration->popup && gtk_widget_get_mapped(configuration->popup),
        configuration->popup && gtk_widget_get_visible(configuration->popup), focus_kind);
}
static void language_configuration_emit(LanguageConfiguration *configuration, const char *operation,
        const char *source, const char *target) {
    JsonObject *event = json_object_new(), *value = json_object_new();
    json_object_set_string_member(event, "operation", operation);
    json_object_set_string_member(value, "openingId", configuration->id);
    if (source && target) {
        json_object_set_string_member(value, "source", source);
        json_object_set_string_member(value, "target", target);
    }
    json_object_set_object_member(event, "value", value);
    tl_send_event("ui", event);
}
static void language_configuration_close(gboolean notify) {
    LanguageConfiguration *configuration = language_configuration;
    if (!configuration) return;
    language_configuration_trace(configuration, notify ? "close-notify" : "close-silent");
    language_configuration = NULL;
    configuration->retired = TRUE; /* Before any GTK or transport callback. */
    if (configuration->timeout) g_source_remove(configuration->timeout);
    configuration->timeout = 0;
    g_signal_handlers_disconnect_by_data(configuration->top, configuration);
    if (configuration->context) {
        if (configuration->ime_handler) g_signal_handler_disconnect(configuration->context, configuration->ime_handler);
        g_object_remove_weak_pointer(G_OBJECT(configuration->context), (gpointer *)&configuration->context);
        configuration->context = NULL;
    }
    if (notify) language_configuration_emit(configuration, "languageConfigurationCancel", NULL, NULL);
    if (configuration->popup) {
        GtkWidget *popup = configuration->popup;
        configuration->popup = NULL;
        gtk_popover_popdown(GTK_POPOVER(popup));
        if (gtk_widget_get_parent(popup)) gtk_widget_unparent(popup);
        g_object_unref(popup);
    }
    if (configuration->css && configuration->display)
        gtk_style_context_remove_provider_for_display(configuration->display, GTK_STYLE_PROVIDER(configuration->css));
    g_clear_object(&configuration->css); g_clear_object(&configuration->display);
    language_configuration_unref(configuration);
}
static void language_configuration_changed(GtkWidget *widget, GParamSpec *spec, gpointer data) {
    (void)widget;
    LanguageConfiguration *configuration = language_configuration_ref(data);
    /* No pending ACK may reclaim focus after the user moved elsewhere. This
     * records retirement, not the original source widget or its authority. */
    language_configuration_trace(configuration,
        g_strcmp0(g_param_spec_get_name(spec), "focus-widget") == 0 ? "signal-focus" : "signal-active");
    gboolean pending_focus_change = configuration->pending && !configuration->opening
        && g_strcmp0(g_param_spec_get_name(spec), "focus-widget") == 0;
    if (language_configuration == configuration && (!language_configuration_current(configuration)
        || pending_focus_change
        || (!configuration->pending && !configuration->opening
            && !language_configuration_contains(configuration,
                gtk_window_get_focus(GTK_WINDOW(configuration->top))))))
        language_configuration_close(TRUE);
    language_configuration_unref(configuration);
}
static void language_configuration_unmapped(GtkWidget *widget, gpointer data) {
    (void)widget;
    LanguageConfiguration *configuration = language_configuration_ref(data);
    language_configuration_trace(configuration, widget == configuration->top ? "top-unmap" : "popup-closed");
    if (language_configuration == configuration) language_configuration_close(TRUE);
    language_configuration_unref(configuration);
}
static gboolean language_configuration_timeout(gpointer data) {
    LanguageConfiguration *configuration = data;
    configuration->timeout = 0;
    language_configuration_trace(configuration, "timeout");
    if (language_configuration == configuration && configuration->pending) language_configuration_close(TRUE);
    return G_SOURCE_REMOVE;
}
static void language_configuration_ime_changed(GtkIMContext *context, guint causes, gpointer data) {
    LanguageConfiguration *configuration = data;
    if (context == configuration->context && causes != TL_FCITX_CHANGE_FOCUS) configuration->clear = FALSE;
}
static gboolean language_configuration_ime(LanguageConfiguration *configuration) {
    if (!language_configuration_current(configuration) || !configuration->client || configuration->probing) return FALSE;
    GtkIMContext *context = g_object_get_data(G_OBJECT(configuration->client), TL_FCITX_CONTEXT_V1);
    if (context != configuration->context) {
        if (configuration->context) {
            if (configuration->ime_handler) g_signal_handler_disconnect(configuration->context, configuration->ime_handler);
            g_object_remove_weak_pointer(G_OBJECT(configuration->context), (gpointer *)&configuration->context);
        }
        configuration->context = context; configuration->ime_handler = 0; configuration->clear = FALSE;
        if (context) {
            g_object_add_weak_pointer(G_OBJECT(context), (gpointer *)&configuration->context);
            guint signal = g_signal_lookup(TL_FCITX_CHANGED_V1, G_OBJECT_TYPE(context));
            GSignalQuery query = {0};
            if (signal) g_signal_query(signal, &query);
            if (query.return_type == G_TYPE_NONE && query.n_params == 1 && query.param_types[0] == G_TYPE_UINT)
                configuration->ime_handler = g_signal_connect(context, TL_FCITX_CHANGED_V1,
                    G_CALLBACK(language_configuration_ime_changed), configuration);
        }
    }
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(configuration->top));
    if (focus == configuration->client) {
        configuration->probing = TRUE;
        gboolean clear = tl_widget_ime_clear(focus);
        configuration->probing = FALSE;
        configuration->clear = clear && language_configuration_current(configuration) && !configuration->composing
            && gtk_window_get_focus(GTK_WINDOW(configuration->top)) == focus
            && g_object_get_data(G_OBJECT(focus), TL_FCITX_CONTEXT_V1) == configuration->context;
    }
    return language_configuration_current(configuration) && !configuration->composing && configuration->clear
        && configuration->context && configuration->ime_handler
        && g_object_get_data(G_OBJECT(configuration->client), TL_FCITX_CONTEXT_V1) == configuration->context;
}
static void language_configuration_preedit(GtkWidget *client, const char *text, gpointer data) {
    LanguageConfiguration *configuration = data;
    if (language_configuration == configuration && configuration->client == client) {
        configuration->composing = text && *text;
        configuration->clear = FALSE;
    }
}
static gboolean language_configuration_action_ready(LanguageConfiguration *configuration) {
    if (!language_configuration_current(configuration) || configuration->pending || configuration->saving
        || !configuration->popup || !gtk_widget_get_mapped(configuration->popup)) return FALSE;
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(configuration->top));
    if (!language_configuration_contains(configuration, focus)) return FALSE;
    g_object_ref(focus);
    gboolean ready = language_configuration_ime(configuration) && language_configuration_current(configuration)
        && gtk_window_get_focus(GTK_WINDOW(configuration->top)) == focus;
    g_object_unref(focus);
    return ready;
}
static gboolean language_configuration_control_current(LanguageConfiguration *configuration, GtkWidget *control) {
    return language_configuration_current(configuration) && gtk_widget_get_mapped(control)
        && gtk_widget_is_sensitive(control)
        && (control == configuration->swap ? configuration->swap_allowed
            : configuration->list && gtk_widget_is_ancestor(control, configuration->list));
}
static void language_configuration_save(LanguageConfiguration *configuration, GtkWidget *control,
        const char *source, const char *target) {
    /* The real IME provider can synchronously filter or retire this row. Keep
     * this activation's values and widget alive, then recheck the exact control. */
    g_object_ref(control);
    char *saved_source = g_strdup(source), *saved_target = g_strdup(target);
    if (!language_configuration_action_ready(configuration)
        || !language_configuration_control_current(configuration, control)) goto done;
    configuration->saving = TRUE;
    /* Do not disable the focused row: GTK would move focus during its own
     * activation and cancel this opening before the save can be sent. The
     * opening's saving gate rejects every further action without moving focus. */
    gtk_widget_add_css_class(configuration->popup, "saving");
    if (!language_configuration_control_current(configuration, control)) goto failed;
    gtk_label_set_text(GTK_LABEL(configuration->feedback), configuration->english ? "Saving…" : "正在保存…");
    if (!language_configuration_control_current(configuration, control)) goto failed;
    language_configuration_emit(configuration, "languageConfigurationSave", saved_source, saved_target);
    goto done;
failed:
    /* A synchronous callback may hide this control without replacing the
     * opening. Do not leave its saving gate latched with no request in flight. */
    if (language_configuration == configuration) language_configuration_close(TRUE);
done:
    g_free(saved_source); g_free(saved_target);
    g_object_unref(control);
}
static void language_configuration_pick(GtkButton *control, gpointer data) {
    LanguageConfiguration *configuration = language_configuration_ref(data);
    if (language_configuration_control_current(configuration, GTK_WIDGET(control))
        && GTK_WIDGET(control) != configuration->swap) {
        const char *code = g_object_get_data(G_OBJECT(control), "typelay-language-code");
        if (code) language_configuration_save(configuration, GTK_WIDGET(control), configuration->source, code);
    }
    language_configuration_unref(configuration);
}
static void language_configuration_swap(GtkButton *control, gpointer data) {
    LanguageConfiguration *configuration = language_configuration_ref(data);
    if (language_configuration_control_current(configuration, GTK_WIDGET(control))
        && GTK_WIDGET(control) == configuration->swap)
        language_configuration_save(configuration, GTK_WIDGET(control), configuration->target, configuration->source);
    language_configuration_unref(configuration);
}
static void language_configuration_filter(GtkEditable *editable, gpointer data) {
    LanguageConfiguration *configuration = language_configuration_ref(data);
    if (!language_configuration_current(configuration) || GTK_WIDGET(editable) != configuration->search) goto done;
    char *query = g_utf8_casefold(gtk_editable_get_text(editable), -1);
    g_strstrip(query);
    guint visible = 0;
    for (GtkWidget *row = gtk_widget_get_first_child(configuration->list); row; row = gtk_widget_get_next_sibling(row)) {
        const char *terms = g_object_get_data(G_OBJECT(row), "typelay-language-search");
        gboolean match = terms && (!*query || strstr(terms, query));
        gtk_widget_set_visible(row, match);
        if (!language_configuration_current(configuration)) break;
        if (match) visible++;
    }
    if (language_configuration_current(configuration) && !configuration->saving)
        gtk_label_set_text(GTK_LABEL(configuration->feedback), visible ? ""
            : configuration->english ? "No matching languages" : "没有匹配的语言");
    g_free(query);
done:
    language_configuration_unref(configuration);
}
static gboolean language_configuration_key(GtkEventControllerKey *keys, guint keyval, guint keycode,
        GdkModifierType modifiers, gpointer data) {
    (void)keys; (void)keycode;
    modifiers &= GDK_SHIFT_MASK | GDK_CONTROL_MASK | GDK_ALT_MASK | GDK_SUPER_MASK | GDK_HYPER_MASK | GDK_META_MASK;
    if (modifiers || (keyval != GDK_KEY_Escape && keyval != GDK_KEY_Return && keyval != GDK_KEY_KP_Enter)) return FALSE;
    LanguageConfiguration *configuration = language_configuration_ref(data);
    gboolean handled = FALSE;
    GtkWidget *focus = configuration->top ? gtk_window_get_focus(GTK_WINDOW(configuration->top)) : NULL;
    if (keyval != GDK_KEY_Escape && focus != configuration->client) goto done;
    gboolean ready = language_configuration_action_ready(configuration);
    if (language_configuration != configuration) { handled = TRUE; goto done; }
    if (!ready) goto done; /* Unknown/preedit remains the original input's event. */
    if (keyval == GDK_KEY_Escape) { language_configuration_close(TRUE); handled = TRUE; }
    else {
        for (GtkWidget *row = gtk_widget_get_first_child(configuration->list); row; row = gtk_widget_get_next_sibling(row))
            if (gtk_widget_get_visible(row) && gtk_widget_is_sensitive(row)) {
                language_configuration_pick(GTK_BUTTON(row), configuration); handled = TRUE; break;
            }
    }
done:
    language_configuration_unref(configuration);
    return handled;
}
static gboolean language_configuration_replace_default_keys(GtkWidget *popup, LanguageConfiguration *configuration) {
    /* GtkPopover 4.22's default bubble handler unconditionally closes on Escape.
     * Replace only the unique key controller of this newly-owned, empty popup;
     * do not touch Entry IM controllers, focus controllers or any host widget. */
    GListModel *controllers = gtk_widget_observe_controllers(popup);
    GtkEventController *original = NULL;
    guint count = 0;
    for (guint index = 0; index < g_list_model_get_n_items(controllers); index++) {
        GObject *item = g_list_model_get_item(controllers, index);
        if (GTK_IS_EVENT_CONTROLLER_KEY(item)) { count++; g_set_object(&original, GTK_EVENT_CONTROLLER(item)); }
        g_object_unref(item);
    }
    g_object_unref(controllers);
    if (count != 1) { g_clear_object(&original); return FALSE; }
    gtk_widget_remove_controller(popup, original); g_object_unref(original);
    GtkEventController *keys = gtk_event_controller_key_new();
    gtk_event_controller_set_propagation_phase(keys, GTK_PHASE_BUBBLE);
    language_configuration_signal(keys, "key-pressed", G_CALLBACK(language_configuration_key), configuration);
    gtk_widget_add_controller(popup, keys);
    return TRUE;
}
static gboolean language_configuration_text(JsonObject *value, const char *name, gsize maximum) {
    const char *text = tl_string(value, name);
    return text && *text && strlen(text) <= maximum && g_utf8_validate(text, -1, NULL);
}
static gboolean language_configuration_show(LanguageConfiguration *configuration, JsonObject *data) {
    language_configuration_trace(configuration, "show-entry");
    const char *stage = "default-keys";
    JsonNode *node = data ? json_object_get_member(data, "choices") : NULL;
    if (!language_configuration_current(configuration) || !configuration->pending || tl_now() > configuration->expires
        || !node || !JSON_NODE_HOLDS_ARRAY(node) || tl_integer(data, "settingsRevision", -1) < 0
        || !language_configuration_text(data, "source", 16) || !language_configuration_text(data, "target", 16)
        || !language_configuration_text(data, "sourceLabel", 256) || !language_configuration_text(data, "targetLabel", 256)) return FALSE;
    JsonArray *choices = json_node_get_array(node);
    guint count = json_array_get_length(choices);
    if (count == 0 || count > 32) return FALSE;
    for (guint index = 0; index < count; index++) {
        JsonNode *item = json_array_get_element(choices, index);
        JsonObject *choice = item && JSON_NODE_HOLDS_OBJECT(item) ? json_node_get_object(item) : NULL;
        JsonNode *selectable = choice ? json_object_get_member(choice, "selectable") : NULL;
        if (!choice || !language_configuration_text(choice, "code", 16)
            || !language_configuration_text(choice, "label", 256) || !language_configuration_text(choice, "nativeLabel", 256)
            || !selectable || !JSON_NODE_HOLDS_VALUE(selectable) || json_node_get_value_type(selectable) != G_TYPE_BOOLEAN) return FALSE;
        const char *code = tl_string(choice, "code");
        for (const char *character = code; *character; character++)
            if (!g_ascii_isalnum(*character) && *character != '-') return FALSE;
        for (guint previous = 0; previous < index; previous++)
            if (g_strcmp0(code, tl_string(json_array_get_object_element(choices, previous), "code")) == 0) return FALSE;
    }
    /* Construction may invoke synchronous widget callbacks. Hold only this
     * invocation's focus reference; it is never retained by the config shell. */
    g_autoptr(GtkWidget) opening_focus = gtk_window_get_focus(GTK_WINDOW(configuration->top));
    if (opening_focus) g_object_ref(opening_focus);
    configuration->source = g_strdup(tl_string(data, "source"));
    configuration->target = g_strdup(tl_string(data, "target"));
    configuration->choices = json_array_ref(choices);
    configuration->settings_revision = tl_integer(data, "settingsRevision", -1);
    configuration->swap_allowed = boolean(data, "swapAllowed");
    JsonObject *appearance = tl_object(data, "appearance");
    const char *language = text_or_empty(appearance, "uiLanguage");
    if (!*language || strcmp(language, "system") == 0) language = g_get_language_names()[0];
    configuration->english = !g_str_has_prefix(language, "zh");
    gboolean dark = FALSE;
    char *name = NULL;
    g_object_get(gtk_settings_get_default(), "gtk-application-prefer-dark-theme", &dark, "gtk-theme-name", &name, NULL);
    const char *theme_name = text_or_empty(data, "theme");
    if (*theme_name && strcmp(theme_name, "system") != 0) dark = strcmp(theme_name, "dark") == 0;
    else if (name) { char *lower = g_ascii_strdown(name, -1); dark |= strstr(lower, "dark") != NULL; g_free(lower); }
    g_free(name);
    const char *accent = text_or_empty(appearance, "accent");
    TlOverlayPalette palette = tl_overlay_palette(dark, accent, boolean(appearance, "reduceTransparency"));
    /* The configuration menu uses the prototype's strong row fill, not the
     * brighter accent used by standalone controls. These five fills are the
     * same in both themes; tick ink still follows the theme-aware palette. */
    const char *accent_strong = strcmp(accent, "ocean") == 0 ? "#1f63bb"
        : strcmp(accent, "sage") == 0 ? "#3c7650" : strcmp(accent, "iris") == 0 ? "#6a4cbc"
        : strcmp(accent, "graphite") == 0 ? "#55534e" : "#b5552f";
    const char *search_fill = dark ? "rgba(255,255,255,.05)" : "rgba(20,20,19,.045)";
    const char *font = tl_overlay_ui_font(tl_string(appearance, "font"));
    char *font_css = font ? g_strdup_printf("font-family:%s;", font) : g_strdup("");
    char *css = g_strdup_printf(
        "#typelay-language-configuration {background:none;}"
        "#typelay-language-configuration > contents {background:%s;color:%s;border:1px solid %s;border-radius:12px;padding:5px;box-shadow:0 8px 28px rgba(0,0,0,.18);%sfont-size:13px;}"
        "#typelay-language-configuration button {background:none;background-image:none;box-shadow:none;border:0;min-height:30px;padding:0 10px;border-radius:7px;color:inherit;font-weight:400;}"
        "#typelay-language-configuration .language-choices button:hover:not(:disabled),#typelay-language-configuration .language-choices button:focus-visible {background:%s;color:white;}"
        "#typelay-language-configuration .language-choices button:disabled {color:%s;}"
        "#typelay-language-configuration .language-head {padding:6px 8px 8px 10px;font-weight:600;}"
        "#typelay-language-configuration .language-swap {min-width:28px;min-height:28px;padding:0;color:%s;}"
        "#typelay-language-configuration .language-swap:hover:not(:disabled) {background:%s;color:%s;}"
        "#typelay-language-configuration .language-swap:disabled {opacity:.35;}"
        "#typelay-language-configuration .language-search-icon {color:%s;}"
        "#typelay-language-configuration .language-check-icon {color:%s;}"
        "#typelay-language-configuration .language-choices button:hover:not(:disabled) .language-check-icon {color:white;}"
        "#typelay-language-configuration .language-search {min-height:30px;border-radius:8px;background:%s;margin:2px 2px 6px;padding:0 8px;}"
        "#typelay-language-configuration entry {background:none;border:0;box-shadow:none;min-height:30px;padding:0;font-size:13px;line-height:18px;font-weight:400;}"
        "#typelay-language-configuration .language-native {font-size:12px;opacity:.55;}"
        "#typelay-language-configuration.saving .language-choices {opacity:.55;}"
        "#typelay-language-configuration .language-feedback {color:%s;font-size:12px;}",
        palette.surface, palette.ink, palette.line, font_css, accent_strong, palette.tertiary,
        palette.secondary, palette.hover, palette.ink, palette.tertiary, palette.accent_ink, search_fill, palette.secondary);
    g_free(font_css);
    configuration->css = gtk_css_provider_new();
    gtk_css_provider_load_from_data(configuration->css, css, -1); g_free(css);
    configuration->display = g_object_ref(gtk_widget_get_display(configuration->top));
    gtk_style_context_add_provider_for_display(configuration->display, GTK_STYLE_PROVIDER(configuration->css), GTK_STYLE_PROVIDER_PRIORITY_APPLICATION + 2);
    language_configuration_trace(configuration, "provider-return");
    if (!language_configuration_current(configuration) || !configuration->pending
        || tl_now() > configuration->expires) return FALSE;
    configuration->opening = TRUE;
    GtkWidget *popup = gtk_popover_new();
    configuration->popup = g_object_ref_sink(popup);
    g_object_ref(popup); /* Local construction ref survives a synchronous retirement. */
    gtk_widget_set_name(popup, "typelay-language-configuration");
    g_object_set_data(G_OBJECT(popup), "typelay-overlay", GINT_TO_POINTER(1));
    gtk_popover_set_has_arrow(GTK_POPOVER(popup), FALSE);
    gtk_popover_set_autohide(GTK_POPOVER(popup), TRUE);
    gtk_popover_set_pointing_to(GTK_POPOVER(popup), &configuration->anchor);
    gtk_popover_set_position(GTK_POPOVER(popup), GTK_POS_BOTTOM);
    if (!language_configuration_replace_default_keys(popup, configuration)) goto failed;
    gtk_widget_set_font_map(popup, font ? tl_overlay_font_map() : NULL);
    GtkWidget *box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 0);
    gtk_widget_set_size_request(box, 252, -1); /* 264 total: 5px padding + 1px edge per side. */
    gtk_popover_set_child(GTK_POPOVER(popup), box);
    GtkWidget *head = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
    gtk_widget_add_css_class(head, "language-head");
    char *direction = g_strdup_printf("%s → %s", tl_string(data, "sourceLabel"), tl_string(data, "targetLabel"));
    GtkWidget *label = gtk_label_new(direction); g_free(direction);
    gtk_label_set_ellipsize(GTK_LABEL(label), PANGO_ELLIPSIZE_END);
    gtk_label_set_xalign(GTK_LABEL(label), 0); gtk_widget_set_hexpand(label, TRUE);
    gtk_box_append(GTK_BOX(head), label);
    configuration->swap = gtk_button_new();
    gtk_widget_add_css_class(configuration->swap, "language-swap");
    gtk_widget_set_name(configuration->swap, "typelay-language-swap");
    gtk_button_set_child(GTK_BUTTON(configuration->swap), more_icon("language-swap"));
    gtk_widget_set_focus_on_click(configuration->swap, FALSE);
    gtk_widget_set_sensitive(configuration->swap, configuration->swap_allowed);
    gtk_accessible_update_property(GTK_ACCESSIBLE(configuration->swap), GTK_ACCESSIBLE_PROPERTY_LABEL,
        configuration->english ? "Swap languages" : "交换语言", -1);
    language_configuration_signal(configuration->swap, "clicked", G_CALLBACK(language_configuration_swap), configuration);
    gtk_box_append(GTK_BOX(head), configuration->swap); gtk_box_append(GTK_BOX(box), head);
    GtkWidget *search_row = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
    gtk_widget_add_css_class(search_row, "language-search");
    gtk_box_append(GTK_BOX(search_row), more_icon("language-search"));
    configuration->search = gtk_entry_new();
    gtk_widget_set_name(configuration->search, "typelay-language-search");
    gtk_entry_set_placeholder_text(GTK_ENTRY(configuration->search), configuration->english ? "Search languages…" : "搜索语言…");
    gtk_widget_set_hexpand(configuration->search, TRUE);
    gtk_box_append(GTK_BOX(search_row), configuration->search); gtk_box_append(GTK_BOX(box), search_row);
    configuration->client = GTK_WIDGET(gtk_editable_get_delegate(GTK_EDITABLE(configuration->search)));
    language_configuration_signal(configuration->client, "preedit-changed", G_CALLBACK(language_configuration_preedit), configuration);
    GtkWidget *scroll = gtk_scrolled_window_new();
    gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(scroll), GTK_POLICY_NEVER, GTK_POLICY_AUTOMATIC);
    gtk_scrolled_window_set_propagate_natural_height(GTK_SCROLLED_WINDOW(scroll), TRUE);
    gtk_scrolled_window_set_max_content_height(GTK_SCROLLED_WINDOW(scroll), 232);
    configuration->list = gtk_box_new(GTK_ORIENTATION_VERTICAL, 0);
    gtk_widget_set_name(configuration->list, "typelay-language-choices");
    gtk_widget_add_css_class(configuration->list, "language-choices");
    gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(scroll), configuration->list);
    gtk_box_append(GTK_BOX(box), scroll);
    for (guint index = 0; index < count; index++) {
        JsonObject *choice = json_array_get_object_element(choices, index);
        const char *code = tl_string(choice, "code"), *title = tl_string(choice, "label"), *native = tl_string(choice, "nativeLabel");
        GtkWidget *row = gtk_button_new(), *content = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 10);
        char *row_name = g_strdup_printf("typelay-language-choice-%s", code);
        gtk_widget_set_name(row, row_name); g_free(row_name);
        gtk_widget_set_focus_on_click(row, FALSE);
        gtk_widget_set_sensitive(row, boolean(choice, "selectable"));
        g_object_set_data_full(G_OBJECT(row), "typelay-language-code", g_strdup(code), g_free);
        char *terms = g_strdup_printf("%s\n%s\n%s", code, title, native), *folded = g_utf8_casefold(terms, -1); g_free(terms);
        g_object_set_data_full(G_OBJECT(row), "typelay-language-search", folded, g_free);
        GtkWidget *tick = more_icon("language-check");
        gtk_widget_set_opacity(tick, strcmp(code, configuration->target) == 0 ? 1 : 0);
        gtk_box_append(GTK_BOX(content), tick);
        GtkWidget *title_label = gtk_label_new(title), *native_label = gtk_label_new(native);
        gtk_label_set_xalign(GTK_LABEL(title_label), 0); gtk_widget_set_hexpand(title_label, TRUE);
        gtk_widget_add_css_class(native_label, "language-native");
        gtk_box_append(GTK_BOX(content), title_label); gtk_box_append(GTK_BOX(content), native_label);
        gtk_button_set_child(GTK_BUTTON(row), content);
        gtk_accessible_update_property(GTK_ACCESSIBLE(row), GTK_ACCESSIBLE_PROPERTY_LABEL, title, -1);
        language_configuration_signal(row, "clicked", G_CALLBACK(language_configuration_pick), configuration);
        gtk_box_append(GTK_BOX(configuration->list), row);
    }
    configuration->feedback = gtk_label_new("");
    gtk_widget_add_css_class(configuration->feedback, "language-feedback");
    gtk_label_set_wrap(GTK_LABEL(configuration->feedback), TRUE);
    gtk_box_append(GTK_BOX(box), configuration->feedback);
    language_configuration_signal(configuration->search, "changed", G_CALLBACK(language_configuration_filter), configuration);
    language_configuration_signal(popup, "closed", G_CALLBACK(language_configuration_unmapped), configuration);
    stage = "before-parent";
    language_configuration_trace(configuration, stage);
    if (!language_configuration_current(configuration) || !configuration->pending
        || tl_now() > configuration->expires) goto failed;
    gtk_widget_set_parent(popup, configuration->top);
    stage = "parent-return";
    language_configuration_trace(configuration, stage);
    if (!language_configuration_current(configuration) || !configuration->pending
        || tl_now() > configuration->expires
        || gtk_window_get_focus(GTK_WINDOW(configuration->top)) != opening_focus) goto failed;
    gtk_popover_popup(GTK_POPOVER(popup));
    stage = "popup-return";
    language_configuration_trace(configuration, stage);
    if (!language_configuration_current(configuration) || !configuration->pending
        || tl_now() > configuration->expires) goto failed;
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(configuration->top));
    if (focus != opening_focus && !language_configuration_contains(configuration, focus)) goto failed;
    gtk_widget_grab_focus(configuration->search);
    configuration->opening = FALSE;
    stage = "focus-return";
    language_configuration_trace(configuration, stage);
    if (!language_configuration_current(configuration) || !configuration->pending
        || tl_now() > configuration->expires || !gtk_widget_get_mapped(popup)
        || gtk_window_get_focus(GTK_WINDOW(configuration->top)) != configuration->client) goto failed;
    configuration->pending = FALSE;
    if (configuration->timeout) g_source_remove(configuration->timeout);
    configuration->timeout = 0;
    (void)language_configuration_ime(configuration);
    language_configuration_trace(configuration, "ime-return");
    gboolean current = language_configuration_current(configuration) && tl_now() <= configuration->expires;
    g_object_unref(popup);
    return current;
failed:
    language_configuration_trace(configuration, stage);
    language_configuration_trace(configuration, "show-rejected");
    g_object_unref(popup);
    return FALSE;
}
static JsonObject *language_configuration_command(const char *operation, JsonObject *data) {
    LanguageConfiguration *configuration = language_configuration_ref(language_configuration);
    gboolean matches = configuration && g_strcmp0(configuration->id, tl_string(data, "openingId")) == 0;
    gboolean opened = FALSE;
    if (matches && strcmp(operation, "overlay-language-configuration-open") == 0) {
        language_configuration_trace(configuration, "peer-open");
        opened = language_configuration_show(configuration, data);
        if (!opened && language_configuration == configuration) language_configuration_close(TRUE);
    } else if (matches && strcmp(operation, "overlay-language-configuration-close") == 0) {
        language_configuration_trace(configuration, "peer-close");
        language_configuration_close(FALSE);
    } else if (matches && strcmp(operation, "overlay-language-configuration-result") == 0
        && language_configuration_current(configuration) && !configuration->pending && configuration->saving) {
        configuration->saving = FALSE;
        gtk_widget_remove_css_class(configuration->popup, "saving");
        if (language_configuration_current(configuration)) gtk_label_set_text(GTK_LABEL(configuration->feedback),
            configuration->english ? "Save not confirmed. Choose a language to retry." : "保存未确认，请重新选择语言。");
    }
    JsonObject *reply = json_object_new();
    json_object_set_boolean_member(reply, "opened", opened);
    if (configuration) json_object_set_string_member(reply, "openingId", configuration->id);
    language_configuration_unref(configuration);
    return reply;
}
static void begin_language_configuration(GtkWidget *trigger, JsonObject *view) {
    if (language_configuration || overlay.view != view || trigger != overlay.language || !active()
        || paused() || overlay.glossary || tl_now() > overlay.expires
        || !tl_configuration_current(overlay.generation) || !tl_view_context_matches(overlay.target, view)) return;
    Target *target = overlay.target;
    GtkWidget *top = g_object_ref(overlay.top), *focus = gtk_window_get_focus(GTK_WINDOW(overlay.top));
    gint64 revision = overlay.revision;
    if (!focus) { g_object_unref(top); return; }
    g_object_ref(focus);
    graphene_rect_t rect;
    gboolean located = gtk_widget_compute_bounds(trigger, top, &rect);
    gboolean clear = focus == overlay.text ? !overlay.composing && tl_widget_ime_clear(focus)
        : !target->dead && focus == target->client && tl_focused() == target && tl_ime_clear(target);
    if (!located || !clear || overlay.view != view || overlay.target != target || overlay.revision != revision
        || overlay.top != top || !active() || gtk_window_get_focus(GTK_WINDOW(top)) != focus
        || (focus == overlay.text && overlay.composing) || tl_now() > overlay.expires
        || !tl_configuration_current(overlay.generation) || !tl_view_context_matches(target, view)
        || !isfinite(rect.origin.x) || !isfinite(rect.origin.y)
        || !isfinite(rect.size.width) || !isfinite(rect.size.height)
        || rect.origin.x < 0 || rect.origin.y < 0
        || rect.origin.x > gtk_widget_get_width(top) || rect.origin.y > gtk_widget_get_height(top)
        || rect.size.width <= 1 || rect.size.height <= 1
        || rect.size.width > gtk_widget_get_width(top) || rect.size.height > gtk_widget_get_height(top)
        || rect.origin.x + rect.size.width > gtk_widget_get_width(top)
        || rect.origin.y + rect.size.height > gtk_widget_get_height(top)) goto done;
    LanguageConfiguration *configuration = g_new0(LanguageConfiguration, 1);
    configuration->refs = 1;
    configuration->id = g_uuid_string_random();
    configuration->top = g_object_ref(top);
    configuration->anchor = (GdkRectangle){(int)rect.origin.x, (int)rect.origin.y, (int)rect.size.width, (int)rect.size.height};
    configuration->expires = tl_now() + 600;
    configuration->pending = TRUE;
    JsonObject *event = json_object_new(), *value = json_object_new();
    json_object_set_string_member(event, "operation", "languageConfigurationBegin");
    json_object_set_string_member(event, "target", target->id);
    json_object_set_string_member(event, "generation", overlay.generation);
    json_object_set_int_member(event, "revision", revision);
    json_object_set_string_member(value, "openingId", configuration->id);
    json_object_set_object_member(event, "value", value);
    tl_clear_permit();
    if (overlay.view != view || overlay.target != target || overlay.revision != revision
        || overlay.top != top || !active() || gtk_window_get_focus(GTK_WINDOW(top)) != focus
        || (focus == overlay.text && overlay.composing) || tl_now() > overlay.expires
        || !tl_configuration_current(overlay.generation) || !tl_view_context_matches(target, view)) {
        json_object_unref(event); language_configuration_unref(configuration); goto done;
    }
    hide_overlay("language-configuration");
    if (overlay.popover || language_configuration || !gtk_widget_get_mapped(top)
        || !gtk_window_is_active(GTK_WINDOW(top)) || tl_now() > configuration->expires) {
        json_object_unref(event); language_configuration_unref(configuration); goto done;
    }
    language_configuration = configuration;
    language_configuration_signal(top, "notify::is-active", G_CALLBACK(language_configuration_changed), configuration);
    language_configuration_signal(top, "notify::focus-widget", G_CALLBACK(language_configuration_changed), configuration);
    language_configuration_signal(top, "unmap", G_CALLBACK(language_configuration_unmapped), configuration);
    configuration->timeout = g_timeout_add_full(G_PRIORITY_DEFAULT,
        (guint)MAX(1, configuration->expires - tl_now()), language_configuration_timeout,
        language_configuration_ref(configuration), language_configuration_unref);
    if (language_configuration_current(configuration) && configuration->pending
        && tl_now() <= configuration->expires) tl_send_event("ui", event);
    else {
        json_object_unref(event);
        if (language_configuration == configuration) language_configuration_close(TRUE);
    }
done:
    g_object_unref(focus); g_object_unref(top);
}

static GtkWidget *more_item(GtkWidget *box, const char *label, const char *operation, const char *name) {
    const char *translated = tl_overlay_ui(label, overlay.english);
    GtkWidget *item = button(box, translated, operation);
    gtk_widget_set_name(item, name);
    GtkWidget *icon = more_icon(operation);
    if (icon) {
        GtkWidget *row = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 10);
        GtkWidget *text = gtk_label_new(translated);
        gtk_label_set_xalign(GTK_LABEL(text), 0);
        gtk_widget_set_hexpand(text, TRUE);
        gtk_box_append(GTK_BOX(row), icon);
        gtk_box_append(GTK_BOX(row), text);
        gtk_button_set_child(GTK_BUTTON(item), row);
        g_object_set_data(G_OBJECT(item), "typelay-more-label", text);
    }
    gtk_accessible_update_property(GTK_ACCESSIBLE(item), GTK_ACCESSIBLE_PROPERTY_LABEL, translated, -1);
    return item;
}
static void more_unmapped(GtkWidget *popup, gpointer unused) {
    (void)unused;
    if (overlay.more_popup == popup) close_more(FALSE);
}
static void toggle_more(JsonObject *view) {
    if (overlay.more_popup) { close_more(FALSE); return; }
    if (overlay.view != view || !active() || !overlay.more || !gtk_widget_get_mapped(overlay.more)
        || !tl_view_context_matches(overlay.target, view) || tl_now() > overlay.expires) return;
    close_language_menu(view);
    if (overlay.view != view || !overlay.more || !tl_configuration_current(overlay.generation)) return;
    freeze_translation_menu(view);
    GtkWidget *trigger = g_object_ref(overlay.more), *card = g_object_ref(overlay.card);
    /* Publish a complete, source-bound opening. Never enable autohide: its
     * native focus grab would change the source/editor's input ownership. */
    GtkWidget *popup = gtk_popover_new();
    g_object_ref_sink(popup);
    gtk_widget_set_name(popup, "typelay-more-popup");
    g_object_set_data(G_OBJECT(popup), "typelay-overlay", GINT_TO_POINTER(1));
    gtk_popover_set_autohide(GTK_POPOVER(popup), FALSE);
    gtk_popover_set_has_arrow(GTK_POPOVER(popup), FALSE);
    gtk_popover_set_position(GTK_POPOVER(popup), GTK_POS_BOTTOM);
    gtk_widget_set_focusable(popup, FALSE);
    g_signal_connect(popup, "unmap", G_CALLBACK(more_unmapped), NULL);
    gtk_widget_set_font_map(popup, gtk_widget_get_font_map(card));
    GtkWidget *items = gtk_box_new(GTK_ORIENTATION_VERTICAL, 0);
    gtk_widget_set_name(items, "typelay-more-items");
    gtk_widget_set_size_request(items, 224, -1);
    gtk_popover_set_child(GTK_POPOVER(popup), items);
    GtkWidget *regenerate = more_item(items, "重新翻译", "regenerate", "typelay-regenerate");
    char *text = candidate();
    gboolean has_text = *text != '\0';
    g_free(text);
    gtk_widget_set_sensitive(regenerate, has_text
        || g_strcmp0(tl_string(view, "state"), "TranslationFailed") == 0);
    GtkWidget *alternative = more_item(items, "换种说法", "alternative", "typelay-alternative");
    GtkWidget *row = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 10);
    gtk_widget_set_name(row, "typelay-style-row");
    gtk_box_append(GTK_BOX(items), row);
    const char *state = tl_string(view, "state");
    gboolean draft = g_strcmp0(state, "EditingTranslation") == 0
        || g_strcmp0(state, "ReturningToSource") == 0 || g_strcmp0(state, "EditedTranslationPending") == 0;
    gtk_box_append(GTK_BOX(row), more_icon(draft ? "lock" : "style"));
    GtkWidget *style_label = gtk_label_new(overlay.english ? "Style" : "风格");
    gtk_box_append(GTK_BOX(row), style_label);
    GtkWidget *segments = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 0);
    gtk_widget_set_name(segments, "typelay-style-segments");
    gtk_widget_set_hexpand(segments, TRUE);
    gtk_widget_set_halign(segments, GTK_ALIGN_END);
    gtk_box_append(GTK_BOX(row), segments);
    GtkWidget *style_natural = more_item(segments, "自然", "style-natural", "typelay-style-natural");
    GtkWidget *style_formal = more_item(segments, "正式", "style-formal", "typelay-style-formal");
    for (guint index = 0; index < 2; index++) {
        GtkWidget *unsupported = gtk_button_new_with_label(overlay.english
            ? (index ? "Casual" : "Concise") : (index ? "口语" : "简洁"));
        gtk_widget_set_name(unsupported, index ? "typelay-style-casual" : "typelay-style-concise");
        gtk_widget_set_sensitive(unsupported, FALSE);
        gtk_widget_set_focusable(unsupported, FALSE);
        gtk_widget_set_tooltip_text(unsupported, overlay.english ? "Not available yet" : "暂不可用");
        gtk_box_append(GTK_BOX(segments), unsupported);
    }
    GtkWidget *style_inherit = more_item(items, "继承", "style-inherit", "typelay-style-inherit");
    GtkWidget *copy = more_item(items, "复制译文", "copy", "typelay-copy");
    gtk_widget_set_sensitive(copy, has_text);
    GtkWidget *glossary_action = more_item(items, "加入术语表", "glossary", "typelay-glossary-add");
    GtkWidget *feedback = gtk_label_new("");
    gtk_widget_set_name(feedback, "typelay-action-feedback");
    gtk_label_set_xalign(GTK_LABEL(feedback), 0);
    gtk_label_set_wrap(GTK_LABEL(feedback), TRUE);
    gtk_label_set_wrap_mode(GTK_LABEL(feedback), PANGO_WRAP_WORD_CHAR);
    gtk_label_set_max_width_chars(GTK_LABEL(feedback), 32);
    gtk_box_append(GTK_BOX(items), feedback);
    gtk_box_append(GTK_BOX(items), gtk_separator_new(GTK_ORIENTATION_HORIZONTAL));
    more_item(items, overlay.english ? "Hide for now" : "暂时隐藏", "escape", "typelay-hide");
    more_item(items, "不要在这个应用中显示", "exclude", "typelay-exclude");
    more_item(items, "设置…", "settings", "typelay-settings");
    if (overlay.view == view && overlay.more == trigger && overlay.card == card
        && active() && tl_configuration_current(overlay.generation) && tl_now() <= overlay.expires) {
        overlay.more_popup = g_object_ref(popup);
        overlay.more_view = json_object_ref(view);
        overlay.style_label = style_label; overlay.style_inherit = style_inherit;
        overlay.style_natural = style_natural; overlay.style_formal = style_formal;
        overlay.alternative = alternative; overlay.glossary_action = glossary_action;
        overlay.action_feedback = feedback;
        gtk_widget_set_parent(popup, trigger);
        if (overlay.more_popup == popup && overlay.view == view && render_translation_actions(view)
            && overlay.more_popup == popup && more_current()) gtk_popover_popup(GTK_POPOVER(popup));
        else if (overlay.more_popup == popup) close_more(FALSE);
    }
    g_object_unref(popup); g_object_unref(card); g_object_unref(trigger);
}

static gboolean theme(JsonObject *data, gboolean stale_preview, gboolean capsule) {
    JsonObject *appearance = tl_object(data, "appearance");
    const char *choice = text_or_empty(data, "theme"), *language = text_or_empty(appearance, "uiLanguage");
    gboolean dark = FALSE;
    char *name = NULL;
    g_object_get(gtk_settings_get_default(), "gtk-application-prefer-dark-theme", &dark, "gtk-theme-name", &name, NULL);
    if (*choice && strcmp(choice, "system") != 0) dark = strcmp(choice, "dark") == 0;
    else if (name) { char *lower = g_ascii_strdown(name, -1); dark |= strstr(lower, "dark") != NULL; g_free(lower); }
    g_free(name);
    if (!*language || strcmp(language, "system") == 0) language = g_get_language_names()[0];
    overlay.english = !g_str_has_prefix(language, "zh");
    overlay.reduce_motion = tl_overlay_gtk_motion_reduced(overlay.card,
        tl_overlay_motion(tl_string(appearance, "motion"), boolean(appearance, "reduceMotion")));
    overlay.text_size = (gint)CLAMP(tl_integer(appearance, "textSize", 15), 13, 19);
    TlOverlayPalette palette = tl_overlay_palette(dark, tl_string(appearance, "accent"), boolean(appearance, "reduceTransparency"));
    char css[16384];
    char content_font[128];
    int count = tl_overlay_gtk_css(css, sizeof css, palette,
        tl_overlay_content_font(tl_string(appearance, "font"), tl_string(overlay.view, "targetLanguage"),
            overlay.text_size, content_font, sizeof content_font),
        tl_overlay_ui_font(tl_string(appearance, "font")), overlay.text_size, capsule, stale_preview, overlay.reduce_motion, GTK_MAJOR_VERSION == 4);
    if (count < 0 || (gsize)count >= sizeof css) return FALSE;
    int more_count = tl_overlay_gtk_more_css(css + count, sizeof css - (gsize)count, palette,
        tl_overlay_ui_font(tl_string(appearance, "font")), tl_string(appearance, "accent"));
    if (more_count < 0 || (gsize)more_count >= sizeof css - (gsize)count) return FALSE;
    count += more_count;
    int contents_count = snprintf(css + count, sizeof css - (gsize)count,
        "#typelay-more-popup { background:transparent; border:0; box-shadow:none; padding:0; }"
        "#typelay-more-popup > contents { background:%s; color:%s; border:1px solid %s;"
        " border-radius:12px; padding:5px; box-shadow:0 12px 32px rgba(20,20,19,0.18); }"
        /* Default's all-property scrollbar transition interpolates border and
         * margin independently. Even an unmapped AUTOMATIC scrollbar contributes
         * its minimum height to this body, making the same text change height.
         * Keep its colour/fade transitions, but apply its current geometry at
         * once. This is only the owned candidate scroll, not the host's theme. */
        "#typelay-overlay #typelay-body scrollbar,"
        "#typelay-overlay #typelay-body scrollbar slider {"
        " transition-property:background-color,border-top-color,border-right-color,"
        "border-bottom-color,border-left-color,opacity; }",
        palette.surface, palette.ink, palette.line);
    if (contents_count < 0 || (gsize)contents_count >= sizeof css - (gsize)count) return FALSE;
    JsonObject *view = json_object_ref(overlay.view);
    GtkWidget *card = g_object_ref(overlay.card);
    GtkCssProvider *provider = g_object_ref(overlay.css);
    tl_overlay_install_fonts(card, tl_string(appearance, "font"));
    if (overlay.view != view || overlay.card != card) {
        g_object_unref(provider); g_object_unref(card); json_object_unref(view); return FALSE;
    }
#if GTK_CHECK_VERSION(4, 12, 0)
    gtk_css_provider_load_from_string(provider, css);
#else
    gtk_css_provider_load_from_data(provider, css, -1);
#endif
    gboolean current = overlay.view == view && overlay.card == card && localize_widgets(card, view);
    g_object_unref(provider); g_object_unref(card); json_object_unref(view);
    return current;
}

/* GtkEntry/TextView allocate only their own children, not this attached
 * popover. GtkNative validates the root's CSS in update-after; present after
 * that validation and before layout consumes the old surface dimensions.
 * This listens to existing frames only: no tick loop or frame request, and
 * present reallocates a visible popup rather than reopening a hidden one. */
static gboolean popup_layout_current(GtkWidget *popover, JsonObject *view, Target *target, gint64 revision) {
    return overlay.popover == popover && overlay.view == view && overlay.target == target
        && overlay.revision == revision && active() && gtk_widget_get_mapped(popover)
        && tl_now() <= overlay.expires && tl_configuration_current(overlay.generation);
}
static void update_popup_layout(GdkFrameClock *clock, gpointer unused) {
    (void)unused;
    if (clock != overlay.layout_clock || !active() || !overlay.popover || !overlay.view
        || !gtk_widget_get_mapped(overlay.popover) || tl_now() > overlay.expires
        || !tl_configuration_current(overlay.generation)) return;
    GtkWidget *popover = g_object_ref(overlay.popover);
    JsonObject *view = json_object_ref(overlay.view);
    Target *target = overlay.target;
    gint64 revision = overlay.revision;
    gboolean dirty = overlay.layout_popover == popover && overlay.layout_view == view
        && overlay.layout_revision == revision;
    /* Consume before queue_resize: a callback may render a different view and
     * place its own pending request. Never clear that newer request below. */
    clear_layout_request();
    if (dirty && popup_layout_current(popover, view, target, revision))
        gtk_widget_queue_resize(popover);
    if (!popup_layout_current(popover, view, target, revision)) goto done;
    /* A menu can outgrow both sides of a multiline field. Let its viewport
     * shrink instead of forcing GtkPopover to pop down an undersized surface.
     * Keep the existing anchor and placement; only bound scrollable content. */
    if (overlay.menu && gtk_widget_get_visible(overlay.menu)) {
        GtkWidget *menu = g_object_ref(overlay.menu);
        JsonObject *geometry = tl_geometry(overlay.target);
        if (geometry) {
            JsonObject *bounds = tl_object(geometry, "bounds");
            gint64 above = tl_integer(bounds, "y", 0);
            gint64 below = tl_integer(geometry, "frameHeight", 0)
                - above - tl_integer(bounds, "height", 0);
            gint available = (gint)CLAMP(MAX(above, below), 1, G_MAXINT);
            gint minimum = 0;
            gtk_widget_measure(popover, GTK_ORIENTATION_VERTICAL,
                MAX(1, gtk_widget_get_width(popover)), &minimum, NULL, NULL, NULL);
            if (overlay.popover == popover && overlay.menu == menu)
                gtk_scrolled_window_set_max_content_height(GTK_SCROLLED_WINDOW(menu),
                    MAX(1, available - minimum + 1));
            json_object_unref(geometry);
        }
        g_object_unref(menu);
    }
    if (popup_layout_current(popover, view, target, revision))
        gtk_popover_present(GTK_POPOVER(popover));
    if (popup_layout_current(popover, view, target, revision) && more_current()
        && gtk_widget_get_mapped(overlay.more_popup)) {
        GtkWidget *more = g_object_ref(overlay.more_popup);
        gtk_popover_present(GTK_POPOVER(more));
        g_object_unref(more);
    }
done:
    json_object_unref(view); g_object_unref(popover);
}
static void create(Target *target) {
    overlay.target = target;
    overlay.top = GTK_WIDGET(gtk_widget_get_root(target->widget));
    g_object_add_weak_pointer(G_OBJECT(overlay.top), (gpointer *)&overlay.top);
    overlay.popover = gtk_popover_new();
    g_object_ref_sink(overlay.popover);
    g_object_set_data(G_OBJECT(overlay.popover), "typelay-overlay", GINT_TO_POINTER(1));
    gtk_widget_set_name(overlay.popover, "typelay-overlay");
    gtk_popover_set_autohide(GTK_POPOVER(overlay.popover), FALSE);
    gtk_widget_set_parent(overlay.popover, target->widget);
    gtk_popover_set_position(GTK_POPOVER(overlay.popover), GTK_POS_TOP);
    overlay.card = gtk_box_new(GTK_ORIENTATION_VERTICAL, 0);
    gtk_popover_set_child(GTK_POPOVER(overlay.popover), overlay.card);
    overlay.header = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 4);
    gtk_widget_set_name(overlay.header, "typelay-header");
    gtk_box_append(GTK_BOX(overlay.card), overlay.header);
    overlay.dot = gtk_label_new("●");
    gtk_widget_set_name(overlay.dot, "typelay-dot");
    gtk_box_append(GTK_BOX(overlay.header), overlay.dot);
    overlay.language = button(overlay.header, "", "menu");
    gtk_widget_set_name(overlay.language, "typelay-menu-trigger");
    /* First open keeps source focus; an owned close explicitly hands off here. */
    gtk_widget_set_focusable(overlay.language, FALSE);
    overlay.status = gtk_label_new("");
    gtk_widget_set_name(overlay.status, "typelay-status");
    gtk_label_set_wrap(GTK_LABEL(overlay.status), TRUE);
    gtk_label_set_wrap_mode(GTK_LABEL(overlay.status), PANGO_WRAP_WORD_CHAR);
    gtk_label_set_max_width_chars(GTK_LABEL(overlay.status), 14);
    gtk_widget_set_hexpand(overlay.status, TRUE);
    gtk_widget_set_halign(overlay.status, GTK_ALIGN_END);
    gtk_box_append(GTK_BOX(overlay.header), overlay.status);
    overlay.actions = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 2);
    gtk_widget_set_name(overlay.actions, "typelay-actions");
    gtk_box_append(GTK_BOX(overlay.header), overlay.actions);
    overlay.more = button(overlay.actions, "⋯", "more");
    gtk_widget_set_name(overlay.more, "typelay-more");
    gtk_widget_set_tooltip_text(overlay.more, "更多");
    gtk_accessible_update_property(GTK_ACCESSIBLE(overlay.more), GTK_ACCESSIBLE_PROPERTY_LABEL, "更多", -1);
    overlay.expand = button(overlay.actions, "展开", "expand");
    overlay.resume = button(overlay.header, "恢复", "resume");
    gtk_widget_set_name(overlay.resume, "typelay-resume");
    if (g_getenv("TYPELAY_TRACE")) {
        g_signal_connect(overlay.resume, "activate", G_CALLBACK(trace_resume), "activate");
        g_signal_connect(overlay.resume, "unmap", G_CALLBACK(trace_resume), "unmap");
    }
    overlay.body = gtk_overlay_new();
    overlay.scroll = gtk_scrolled_window_new();
    gtk_widget_set_name(overlay.scroll, "typelay-body");
    gtk_scrolled_window_set_propagate_natural_height(GTK_SCROLLED_WINDOW(overlay.scroll), FALSE);
    gtk_widget_set_margin_start(overlay.scroll, 8);
    gtk_widget_set_margin_end(overlay.scroll, 8);
    /* GtkScrolledWindow can itself take Tab focus when its text child cannot. */
    gtk_widget_set_can_focus(overlay.scroll, FALSE);
    gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(overlay.scroll), GTK_POLICY_NEVER, GTK_POLICY_AUTOMATIC);
    overlay.text = gtk_text_view_new();
    gtk_widget_set_name(overlay.text, "typelay-candidate");
    gtk_text_view_set_wrap_mode(GTK_TEXT_VIEW(overlay.text), GTK_WRAP_WORD_CHAR);
    gtk_text_view_set_left_margin(GTK_TEXT_VIEW(overlay.text), 6);
    gtk_text_view_set_right_margin(GTK_TEXT_VIEW(overlay.text), 6);
    gtk_text_view_set_top_margin(GTK_TEXT_VIEW(overlay.text), 12);
    gtk_text_view_set_bottom_margin(GTK_TEXT_VIEW(overlay.text), 4);
    gtk_text_view_set_pixels_below_lines(GTK_TEXT_VIEW(overlay.text), 5);
    gtk_text_view_set_accepts_tab(GTK_TEXT_VIEW(overlay.text), FALSE);
    gtk_text_view_set_editable(GTK_TEXT_VIEW(overlay.text), FALSE);
    gtk_text_view_set_cursor_visible(GTK_TEXT_VIEW(overlay.text), FALSE);
    gtk_widget_set_focusable(overlay.text, FALSE);
    GtkGesture *click = gtk_gesture_click_new();
    gtk_gesture_single_set_button(GTK_GESTURE_SINGLE(click), GDK_BUTTON_PRIMARY);
    gtk_event_controller_set_propagation_phase(GTK_EVENT_CONTROLLER(click), GTK_PHASE_CAPTURE);
    g_signal_connect(click, "pressed", G_CALLBACK(begin_edit), NULL);
    gtk_widget_add_controller(overlay.text, GTK_EVENT_CONTROLLER(click));
    GtkEventController *keys = gtk_event_controller_legacy_new();
    gtk_event_controller_set_propagation_phase(keys, GTK_PHASE_CAPTURE);
    gtk_event_controller_set_propagation_limit(keys, GTK_LIMIT_SAME_NATIVE);
    g_signal_connect(keys, "event", G_CALLBACK(key_event), NULL);
    g_object_set_data_full(G_OBJECT(overlay.text), "typelay-session-controller", g_object_ref(keys), g_object_unref);
    gtk_widget_add_controller(overlay.text, keys);
    g_signal_connect(overlay.text, "preedit-changed", G_CALLBACK(preedit), NULL);
    GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text));
    g_signal_connect(buffer, "changed", G_CALLBACK(draft_changed), NULL);
    g_signal_connect(buffer, "insert-text", G_CALLBACK(limit_draft), NULL);
    gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(overlay.scroll), overlay.text);
    gtk_widget_set_vexpand(overlay.scroll, TRUE);
    gtk_overlay_set_child(GTK_OVERLAY(overlay.body), overlay.scroll);
    gtk_box_append(GTK_BOX(overlay.card), overlay.body);
    gtk_box_reorder_child_after(GTK_BOX(overlay.card), overlay.header, overlay.body);
    overlay.skeleton = gtk_box_new(GTK_ORIENTATION_VERTICAL, 9);
    gtk_widget_set_name(overlay.skeleton, "typelay-skeleton");
    gtk_widget_set_valign(overlay.skeleton, GTK_ALIGN_START);
    for (guint line = 0; line < 2; line++) {
        GtkWidget *stripe = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 0);
        gtk_widget_add_css_class(stripe, "typelay-skeleton-line");
        gtk_box_append(GTK_BOX(overlay.skeleton), stripe);
        if (line) { overlay.skeleton_short = stripe; gtk_widget_set_halign(stripe, GTK_ALIGN_START); }
    }
    gtk_overlay_add_overlay(GTK_OVERLAY(overlay.body), overlay.skeleton);
    gtk_widget_set_can_target(overlay.skeleton, FALSE);

    overlay.footer = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 4);
    gtk_widget_set_name(overlay.footer, "typelay-footer");
    overlay.hint = gtk_label_new("");
    gtk_label_set_xalign(GTK_LABEL(overlay.hint), 0);
    gtk_label_set_wrap(GTK_LABEL(overlay.hint), TRUE);
    gtk_label_set_wrap_mode(GTK_LABEL(overlay.hint), PANGO_WRAP_WORD_CHAR);
    gtk_widget_set_hexpand(overlay.hint, TRUE);
    gtk_box_append(GTK_BOX(overlay.footer), overlay.hint);
    overlay.exit = button(overlay.footer, "", "escape");
    gtk_box_append(GTK_BOX(overlay.header), overlay.footer);
    overlay.menu = gtk_scrolled_window_new();
    gtk_widget_set_focusable(overlay.menu, FALSE);
    gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(overlay.menu), GTK_POLICY_NEVER, GTK_POLICY_AUTOMATIC);
    gtk_scrolled_window_set_min_content_height(GTK_SCROLLED_WINDOW(overlay.menu), 1);
    gtk_scrolled_window_set_propagate_natural_height(GTK_SCROLLED_WINDOW(overlay.menu), TRUE);
    GtkWidget *menu_content = gtk_box_new(GTK_ORIENTATION_VERTICAL, 6);
    gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(overlay.menu), menu_content);
    gtk_widget_set_name(overlay.menu, "typelay-language-menu");
    GtkEventController *menu_keys = gtk_event_controller_key_new();
    gtk_event_controller_set_propagation_phase(menu_keys, GTK_PHASE_BUBBLE);
    g_signal_connect(menu_keys, "key-pressed", G_CALLBACK(language_menu_key), NULL);
    gtk_widget_add_controller(overlay.menu, menu_keys);
    gtk_widget_set_margin_start(menu_content, 12);
    gtk_widget_set_margin_end(menu_content, 12);
    gtk_widget_set_margin_top(menu_content, 12);
    gtk_widget_set_margin_bottom(menu_content, 12);
    overlay.source = language_button();
    overlay.destination = language_button();
    gtk_widget_set_name(overlay.source, "typelay-source-language");
    gtk_widget_set_name(overlay.destination, "typelay-target-language");
    gtk_box_append(GTK_BOX(menu_content), gtk_label_new("源语言"));
    gtk_box_append(GTK_BOX(menu_content), overlay.source);
    gtk_box_append(GTK_BOX(menu_content), gtk_label_new("目标语言"));
    gtk_box_append(GTK_BOX(menu_content), overlay.destination);
    overlay.apply_languages = button(menu_content, "应用语言方向", "languages");
    gtk_widget_set_name(overlay.apply_languages, "typelay-apply-languages");
    gtk_box_append(GTK_BOX(overlay.card), overlay.menu);
    overlay.css = gtk_css_provider_new();
    overlay.display = g_object_ref(gtk_widget_get_display(overlay.popover));
    gtk_style_context_add_provider_for_display(overlay.display, GTK_STYLE_PROVIDER(overlay.css),
        GTK_STYLE_PROVIDER_PRIORITY_APPLICATION + 1);
    gtk_widget_set_visible(overlay.menu, FALSE);
    GtkEventController *pointer = gtk_event_controller_legacy_new();
    gtk_event_controller_set_propagation_phase(pointer, GTK_PHASE_CAPTURE);
    gtk_event_controller_set_propagation_limit(pointer, GTK_LIMIT_NONE);
    g_signal_connect(pointer, "event", G_CALLBACK(language_pointer_event), NULL);
    overlay.language_pointer = g_object_ref(pointer);
    gtk_widget_add_controller(overlay.top, pointer);
    overlay.focus_handler = g_signal_connect_after(overlay.top, "notify::focus-widget", G_CALLBACK(focus_changed), NULL);
    overlay.active_handler = g_signal_connect(overlay.top, "notify::is-active", G_CALLBACK(active_changed), NULL);
    GdkFrameClock *clock = gtk_widget_get_frame_clock(overlay.top);
    if (clock) {
        overlay.layout_clock = g_object_ref(clock);
        overlay.layout_handler = g_signal_connect_after(clock, "update", G_CALLBACK(update_popup_layout), NULL);
    }
}
static gboolean pause_render_result(gboolean is_paused, const char *stage, gint64 revision, gboolean result) {
    static guint count;
    if (is_paused && g_getenv("TYPELAY_TRACE") && count < 96) {
        count++;
        g_printerr("TypeLay GTK4 pause-render stage=%s revision=%" G_GINT64_FORMAT
            " atMs=%" G_GINT64_FORMAT " result=%d\n", stage, revision, tl_now(), result);
    }
    return result;
}
static gboolean refresh_translation_actions(JsonObject *value, JsonObject *authority, gint64 until) {
    JsonObject *view = json_object_ref(overlay.view);
    JsonObject *context = tl_copy(tl_object(view, "viewContext"));
    JsonObject *token = tl_object(tl_object(view, "viewContext"), "token");
    JsonNode *actions = json_object_get_member(tl_object(value, "viewContext"), "translationActions");
    /* Keep the exact token object held by an in-flight EditorControl. Copy the
     * context so nested menu-only renders can be detected without replacing view. */
    if (token) json_object_set_object_member(context, "token", json_object_ref(token));
    if (actions) json_object_set_member(context, "translationActions", json_node_copy(actions));
    else json_object_remove_member(context, "translationActions");
    json_object_set_object_member(view, "viewContext", json_object_ref(context));
    overlay.expires = until;
    if (overlay.authority) json_object_unref(overlay.authority);
    overlay.authority = tl_copy(authority);
    /* This refresh extends the display lease only. editor_control.expires is
     * frozen at key-down and is neither replaced nor renewed here. */
    gboolean current = render_translation_actions(view) && translation_render_current(view, context);
    if (current && g_strcmp0(tl_string(value, "state"), "Paused") != 0) {
        /* Menu-only updates project the same visible phase as a full render.
         * Empty feedback must not erase Edited/Updating and move the header. */
        const char *diagnostic = text_or_empty(value, "status");
        if (!*diagnostic) diagnostic = text_or_empty(translation_actions(value), "feedback");
        char *tooltip = g_strdup(diagnostic);
        char *phase = g_strdup(tl_overlay_phase(tl_string(value, "state"),
            tl_string(value, "text"), diagnostic, overlay.english));
        GtkWidget *status = g_object_ref(overlay.status);
        gtk_widget_set_tooltip_text(status, tooltip);
        current = translation_render_current(view, context) && overlay.status == status;
        if (current) {
            gtk_label_set_text(GTK_LABEL(status), phase);
            current = translation_render_current(view, context) && overlay.status == status;
        }
        g_object_unref(status); g_free(phase); g_free(tooltip);
    }
    json_object_unref(context); json_object_unref(view);
    return current;
}
static gboolean language_focus_current(void) {
    GtkWidget *focus = overlay.top ? gtk_window_get_focus(GTK_WINDOW(overlay.top)) : NULL;
    if (!focus || !overlay.menu) return FALSE;
    if (!gtk_widget_get_mapped(overlay.menu))
        return focus == overlay.language && overlay.language && gtk_widget_get_mapped(overlay.language);
    GtkWidget *buttons[] = {overlay.source, overlay.destination};
    for (guint index = 0; index < G_N_ELEMENTS(buttons); index++) {
        GtkWidget *button = buttons[index];
        if (button && gtk_widget_get_mapped(button) && gtk_widget_get_mapped(focus)
            && (focus == button || gtk_widget_is_ancestor(focus, button))) return TRUE;
    }
    return FALSE;
}
static gboolean retained_language_preview(Target *target, JsonObject *data) {
    JsonObject *value = tl_object(data, "view"), *basis = tl_object(data, "authority");
    const char *state = tl_string(overlay.view, "state");
    if (!target || target != overlay.target || !active() || !overlay.popover || !overlay.text
        || overlay.glossary || !gtk_widget_get_mapped(overlay.popover) || !language_focus_current()
        || gtk_text_view_get_editable(GTK_TEXT_VIEW(overlay.text)) || tl_now() > overlay.expires
        || g_strcmp0(overlay.generation, tl_string(data, "generation")) != 0
        || (g_strcmp0(state, "ValidTranslation") != 0 && g_strcmp0(state, "Translating") != 0
            && g_strcmp0(state, "EditedTranslationPending") != 0)
        || !value || !basis || !overlay.authority || !json_object_equal(overlay.authority, basis)
        || boolean(tl_object(overlay.view, "presentation"), "editorOwnsText")
        || !tl_view_context_matches(target, overlay.view) || !tl_view_context_matches(target, value)) return FALSE;
    JsonObject *previous = json_object_ref(overlay.view);
    gboolean current = tl_preview_detached_current(target, basis)
        && overlay.view == previous && overlay.target == target && active() && language_focus_current()
        && overlay.popover && overlay.text && !overlay.glossary && gtk_widget_get_mapped(overlay.popover)
        && !gtk_text_view_get_editable(GTK_TEXT_VIEW(overlay.text))
        && tl_now() <= overlay.expires && tl_configuration_current(tl_string(data, "generation"));
    if (current) {
        JsonObject *old_view = tl_copy(previous), *new_view = tl_copy(value);
        JsonObject *old_token = tl_object(tl_object(old_view, "viewContext"), "token");
        JsonObject *new_token = tl_object(tl_object(new_view, "viewContext"), "token");
        JsonNode *old_status = json_object_get_member(old_view, "status");
        JsonNode *new_status = json_object_get_member(new_view, "status");
        /* Host session identity is independent of the transport repaint serial.
         * Keep the complete token, including its revision and unknown fields. */
        current = old_token && new_token && tl_integer(old_token, "revision", -1) >= 0
            && tl_integer(new_token, "revision", -1) >= 0 && json_object_equal(old_token, new_token)
            && (!old_status || JSON_NODE_HOLDS_NULL(old_status) || tl_string(old_view, "status"))
            && (!new_status || JSON_NODE_HOLDS_NULL(new_status) || tl_string(new_view, "status"));
        if (current) {
            /* Only language choices and their read-only feedback may change.
             * Body, permissions, full context and every other field stay equal. */
            const char *menu_fields[] = {"sourceLanguage", "targetLanguage", "sourceLanguageLabel",
                "targetLanguageLabel", "languageChoices", "status"};
            for (guint index = 0; index < G_N_ELEMENTS(menu_fields); index++) {
                json_object_remove_member(old_view, menu_fields[index]);
                json_object_remove_member(new_view, menu_fields[index]);
            }
            current = json_object_equal(old_view, new_view);
        }
        json_object_unref(old_view); json_object_unref(new_view);
    }
    json_object_unref(previous);
    return current;
}
static gboolean render(Target *target, JsonObject *data) {
    language_configuration_close(TRUE);
    JsonObject *value = tl_object(data, "view"), *presentation = tl_object(value, "presentation");
    JsonObject *authority = tl_object(data, "authority");
    gint64 until = tl_integer(data, "expiresAtMs", 0), now = tl_now();
    const char *generation = tl_string(data, "generation");
    if (!tl_configuration_current(generation)) return FALSE;
    if (overlay.target == target && g_strcmp0(overlay.generation, generation) == 0
        && tl_integer(data, "revision", 0) < overlay.revision) return TRUE;
    gboolean is_paused = g_strcmp0(tl_string(value, "state"), "Paused") == 0;
    gboolean retained_editor = !is_paused && overlay.target == target && boolean(presentation, "editorOwnsText")
        && target && !target->dead
        && (gint64)target->content_revision == tl_integer(authority, "contentRevision", -1)
        && (gint64)target->input_revision == tl_integer(authority, "inputRevision", -1);
    gboolean retained_source_form = glossary_current(overlay.glossary)
        && glossary_source_render(overlay.glossary, target, data);
    gint64 request_revision = tl_integer(data, "revision", 0);
    if (!target || !boolean(presentation, "visible"))
        return pause_render_result(is_paused, "target-or-view", request_revision, FALSE);
    if (!tl_metadata_matches(target, authority))
        return pause_render_result(is_paused, "metadata", request_revision, FALSE);
    if (until < now || until - now > 1000)
        return pause_render_result(is_paused, "expiry", request_revision, FALSE);
    if (is_paused ? !pause_current(target, authority)
        : !retained_source_form && !retained_editor && !tl_ime_clear(target) && !retained_language_preview(target, data))
        return pause_render_result(is_paused, "source-observation", request_revision, FALSE);
    GtkWidget *top = GTK_WIDGET(gtk_widget_get_root(target->widget));
    if (!GTK_IS_WINDOW(top) || !gtk_window_is_active(GTK_WINDOW(top)))
        return pause_render_result(is_paused, "window", request_revision, FALSE);
    if (tl_focused() != target && !(!is_paused && overlay.target == target && overlay.focused))
        return pause_render_result(is_paused, "focus", request_revision, FALSE);
    JsonObject *geometry = tl_geometry(target);
    if (!geometry) return pause_render_result(is_paused, "geometry", request_revision, FALSE);
    if (!tl_configuration_current(generation)) { json_object_unref(geometry); return FALSE; }
    if (overlay.target == target && overlay.top == top && overlay.popover && overlay.text && overlay.status
        && g_strcmp0(overlay.generation, generation) == 0
        && overlay.revision == tl_integer(data, "revision", -1)
        && (!overlay.glossary || !overlay.glossary->source_origin || retained_source_form)
        && translation_actions_only(overlay.view, value)) {
        gboolean refreshed = refresh_translation_actions(value, authority, until);
        json_object_unref(geometry);
        return refreshed;
    }
    clear_layout_request();
    if (overlay.more_popup && overlay.target == target && overlay.top == top) {
        close_more(overlay.pending_glossary);
        if (!tl_configuration_current(generation)) { json_object_unref(geometry); return FALSE; }
    }
    GtkWidget *old_popover = overlay.popover, *old_editor = overlay.text;
    tl_overlay_control_cancel();
    if (overlay.glossary && (overlay.target != target || overlay.top != top
        || g_strcmp0(overlay.generation, generation) != 0
        || overlay.revision != tl_integer(data, "revision", -1)
        || g_strcmp0(tl_string(value, "state"), overlay.glossary->origin_state) != 0
        || (overlay.glossary->source_origin && !retained_source_form)
        || !tl_object(tl_object(value, "viewContext"), "token")
        || !json_object_equal(overlay.glossary->token, tl_object(tl_object(value, "viewContext"), "token"))))
        glossary_close(FALSE, TRUE);
    if (overlay.revision != tl_integer(data, "revision", -1)) overlay.pending_glossary = FALSE;
    if (overlay.target != target || overlay.top != top) { hide_overlay("render-owner-changed"); create(target); }
    g_free(overlay.generation);
    overlay.generation = g_strdup(generation);
    overlay.expires = until;
    if (overlay.revision != tl_integer(data, "revision", 0)) overlay.pending_resume = FALSE;
    overlay.revision = tl_integer(data, "revision", 0);
    if (overlay.authority) json_object_unref(overlay.authority);
    overlay.authority = tl_copy(authority);
    if (overlay.view) json_object_unref(overlay.view);
    overlay.view = tl_copy(value);
    gboolean editing = !is_paused && tl_view_context_matches(target, value)
        && boolean(tl_object(value, "viewContext"), "canEdit")
        && g_strcmp0(tl_string(value, "state"), "EditingTranslation") == 0;
    JsonObject *draft_token = tl_object(tl_object(value, "viewContext"), "token");
    gboolean source_draft = overlay.glossary_draft_buffer
        && gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text)) == overlay.glossary_draft_buffer
        && (g_strcmp0(tl_string(value, "state"), "ValidTranslation") == 0
            || g_strcmp0(tl_string(value, "state"), "EditedTranslationPending") == 0);
    char *draft_text = source_draft ? candidate() : NULL;
    source_draft = source_draft && g_strcmp0(draft_text, text_or_empty(value, "text")) == 0;
    g_free(draft_text);
    gboolean glossary_draft = overlay.glossary_draft_token && draft_token
        && json_object_equal(overlay.glossary_draft_token, draft_token)
        && ((g_strcmp0(tl_string(value, "state"), "EditingTranslation") == 0
                && boolean(presentation, "editorOwnsText"))
            || source_draft);
    if (overlay.glossary_draft_token && !glossary_draft) {
        json_object_unref(overlay.glossary_draft_token); overlay.glossary_draft_token = NULL;
        if (overlay.glossary_draft_buffer) g_object_unref(overlay.glossary_draft_buffer);
        overlay.glossary_draft_buffer = NULL;
    }
    if (!glossary_draft && (is_paused
        || !(boolean(presentation, "editorOwnsText") && gtk_text_view_get_editable(GTK_TEXT_VIEW(overlay.text))))) {
        overlay.setting_text = TRUE;
        gtk_text_buffer_set_text(gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text)), is_paused ? "" : text_or_empty(value, "text"), -1);
        overlay.setting_text = FALSE;
    }
    gtk_text_view_set_editable(GTK_TEXT_VIEW(overlay.text), editing);
    gtk_text_view_set_cursor_visible(GTK_TEXT_VIEW(overlay.text), editing);
    gtk_widget_set_focusable(overlay.text, editing);
    JsonObject *focus_view = json_object_ref(overlay.view);
    gtk_widget_set_can_focus(overlay.scroll, editing);
    gboolean focus_retained = overlay.view == focus_view && overlay.target == target && !target->dead
        && tl_configuration_current(generation) && g_strcmp0(overlay.generation, generation) == 0
        && overlay.popover && overlay.text;
    json_object_unref(focus_view);
    if (!focus_retained) { json_object_unref(geometry); return FALSE; }
    if (!render_languages(value) || !render_translation_actions(overlay.view)) {
        json_object_unref(geometry); return FALSE;
    }
    const char *ui_language = tl_string(tl_object(data, "appearance"), "uiLanguage");
    if (!ui_language || strcmp(ui_language, "system") == 0) ui_language = g_get_language_names()[0];
    overlay.english = !g_str_has_prefix(ui_language, "zh");
    const char *status = text_or_empty(value, "status");
    if (!*status) status = text_or_empty(translation_actions(value), "feedback");
    GtkWidget *status_widget = g_object_ref(overlay.status);
    JsonObject *status_view = json_object_ref(overlay.view);
    char *phase = g_strdup(tl_overlay_phase(tl_string(value, "state"), tl_string(value, "text"), status, overlay.english));
    gtk_widget_set_tooltip_text(status_widget, status);
    if (overlay.view == status_view && overlay.status == status_widget)
        gtk_label_set_text(GTK_LABEL(status_widget), phase);
    gboolean status_current = overlay.view == status_view && overlay.status == status_widget;
    g_free(phase); g_object_unref(status_widget); json_object_unref(status_view);
    if (!status_current) { json_object_unref(geometry); return FALSE; }
    JsonObject *rendered = json_object_ref(overlay.view);
    tl_overlay_acceptance();
    gboolean retained = overlay.view == rendered && overlay.target == target && !target->dead
        && tl_configuration_current(generation) && g_strcmp0(overlay.generation, generation) == 0
        && overlay.popover && overlay.text && overlay.status;
    json_object_unref(rendered);
    if (!retained) { json_object_unref(geometry); return FALSE; }
    gtk_widget_remove_css_class(overlay.status, "ready"); gtk_widget_remove_css_class(overlay.status, "pending");
    if (g_strcmp0(tl_string(presentation, "dot"), "Green") == 0) gtk_widget_add_css_class(overlay.status, "ready");
    if (g_strcmp0(tl_string(presentation, "dot"), "Yellow") == 0) gtk_widget_add_css_class(overlay.status, "pending");
    const char *state = tl_string(value, "state");
    gboolean editable_preview = boolean(value, "editable") && tl_view_context_matches(target, value)
        && boolean(tl_object(value, "viewContext"), "canEdit")
        && (g_strcmp0(state, "ValidTranslation") == 0 || g_strcmp0(state, "EditedTranslationPending") == 0);
    if (editable_preview) gtk_widget_add_css_class(overlay.text, "editable-preview");
    else gtk_widget_remove_css_class(overlay.text, "editable-preview");
    gtk_widget_set_tooltip_text(overlay.text, editable_preview ? tl_overlay_ui("点击译文编辑", overlay.english) : NULL);
    gboolean stale_preview = g_strcmp0(state, "Translating") == 0
        && *text_or_empty(value, "text") && !boolean(presentation, "editorOwnsText");
    gboolean capsule = is_paused || g_strcmp0(tl_string(value, "state"), "IdleCapsule") == 0;
    if (!theme(data, stale_preview, capsule)) { json_object_unref(geometry); return FALSE; }
    gint source_width = gtk_widget_get_width(target->widget);
    gint width = capsule ? 160 : overlay.expanded ? CLAMP(source_width, 300, 560)
        : editing ? CLAMP(source_width * 72 / 100, 400, 440) : CLAMP(source_width * 66 / 100, 300, 440);
    width = MIN(width, MAX(160, gtk_widget_get_width(top) - 48));
    /* Freeze before GTK size/visibility callbacks can replace this render. */
    GtkWidget *rendered_popover = g_object_ref(overlay.popover);
    JsonObject *rendered_view = json_object_ref(overlay.view);
    gint64 rendered_revision = overlay.revision;
    gint previous_width = -1, previous_height = -1;
    gtk_widget_get_size_request(overlay.card, &previous_width, NULL);
    gtk_widget_get_size_request(overlay.scroll, NULL, &previous_height);
    gtk_widget_set_size_request(overlay.card, width, -1);
    char *draft = editing && boolean(presentation, "editorOwnsText")
        && overlay.target == target && overlay.popover == old_popover && overlay.text == old_editor
        && tl_configuration_current(generation) && tl_view_context_matches(target, overlay.view)
        ? candidate() : NULL;
    gint height = measured_body_height(draft ? draft : text_or_empty(value, "text"), width);
    g_free(draft);
    gint collapsed_height = (overlay.text_size * 155 * 7 + 99) / 100 + 4;
    /* Use immediate sizing pending investigation of expand-time editor focus loss. */
    gboolean geometry_changed = previous_width != width || previous_height != height;
    gtk_widget_set_size_request(overlay.scroll, -1, height);
    gboolean busy = g_strcmp0(state, "Translating") == 0 && !boolean(presentation, "editorOwnsText");
    if (busy && !*text_or_empty(value, "text")) height = MAX(54, height);
    gtk_widget_set_visible(overlay.skeleton, busy && !*text_or_empty(value, "text"));
    gtk_widget_set_size_request(overlay.skeleton_short, MAX(1, (width - 32) * 62 / 100), -1);
    gtk_widget_set_visible(overlay.body, !capsule);
    gtk_widget_set_visible(overlay.scroll, !capsule);
    gtk_widget_set_visible(overlay.dot, !is_paused);
    gtk_widget_set_visible(overlay.expand, overlay.expanded || height >= collapsed_height);
    if (busy) { gtk_widget_add_css_class(overlay.dot, "typelay-busy"); gtk_widget_add_css_class(overlay.skeleton, "typelay-busy"); }
    else { gtk_widget_remove_css_class(overlay.dot, "typelay-busy"); gtk_widget_remove_css_class(overlay.skeleton, "typelay-busy"); }
    gtk_widget_set_visible(overlay.footer, !capsule);
    gtk_widget_set_visible(overlay.actions, !capsule);
    gtk_widget_set_visible(overlay.status, is_paused || !capsule);
    gtk_widget_set_visible(overlay.language, !is_paused);
    gtk_widget_set_visible(overlay.resume, is_paused);
    gtk_widget_set_sensitive(overlay.resume, !overlay.pending_resume);
    gtk_widget_set_opacity(overlay.popover, is_paused ? 0.78 : 1.0);
    if (is_paused) gtk_widget_set_visible(overlay.menu, FALSE);
    graphene_point_t zero = GRAPHENE_POINT_INIT(0, 0), origin;
    if (!gtk_widget_compute_point(target->widget, top, &zero, &origin)) {
        json_object_unref(rendered_view); g_object_unref(rendered_popover);
        json_object_unref(geometry);
        return pause_render_result(is_paused, "coordinates", request_revision, FALSE);
    }
    JsonObject *bounds = tl_object(geometry, "bounds");
    GdkRectangle anchor = {
        .x = MAX(0, (gint)tl_integer(bounds, "x", 0) - (gint)origin.x + (gint)tl_integer(bounds, "width", 1) - width / 2),
        .y = (gint)tl_integer(bounds, "y", 0) - (gint)origin.y,
        .width = 1, .height = (gint)tl_integer(bounds, "height", 1)
    };
    gtk_popover_set_pointing_to(GTK_POPOVER(overlay.popover), &anchor);
    json_object_unref(geometry);
    if (overlay.popover != rendered_popover || overlay.view != rendered_view
        || overlay.target != target || overlay.revision != rendered_revision) {
        json_object_unref(rendered_view); g_object_unref(rendered_popover);
        return FALSE;
    }
    gtk_popover_popup(GTK_POPOVER(rendered_popover));
    if (popup_layout_current(rendered_popover, rendered_view, target, rendered_revision))
        gtk_popover_present(GTK_POPOVER(rendered_popover));
    if (!overlay.expiry_timer) overlay.expiry_timer = g_timeout_add(100, expired, NULL);
    tl_overlay_sync();
    if (geometry_changed && popup_layout_current(rendered_popover, rendered_view, target, rendered_revision)) {
        /* The initial present can precede the root's CSS validation. Requery
         * preferred geometry once in its existing next update-after phase. */
        overlay.layout_popover = g_object_ref(rendered_popover);
        overlay.layout_view = json_object_ref(rendered_view);
        overlay.layout_revision = rendered_revision;
    }
    json_object_unref(rendered_view); g_object_unref(rendered_popover);
    return pause_render_result(is_paused, "shown", request_revision, TRUE);
}
static gboolean resume_current(Target *target, JsonObject *data, JsonObject *view) {
    return view && overlay.view == view && overlay.target == target && active() && !paused()
        && overlay.text && overlay.scroll && overlay.status
        && tl_configuration_current(tl_string(data, "generation"))
        && g_strcmp0(overlay.generation, tl_string(data, "generation")) == 0
        && overlay.revision == tl_integer(data, "revision", -1)
        && tl_view_context_matches(target, view)
        && boolean(tl_object(view, "viewContext"), "canEdit");
}
static void resume_editor(Target *target, JsonObject *data) {
    if (!resume_current(target, data, overlay.view)) return;
    JsonObject *view = json_object_ref(overlay.view);
    GtkWidget *editor = g_object_ref(overlay.text), *scroll = g_object_ref(overlay.scroll);
    GtkWidget *status = g_object_ref(overlay.status);
    gtk_label_set_text(GTK_LABEL(status), "无法返回原输入框，请点击译文继续编辑");
    if (!resume_current(target, data, view) || !(tl_focused() == target || overlay.focused)) goto done;
    /* ReturningToSource disabled keyboard focus for this subtree. Restore it
       only under the existing editor authorization, before grabbing focus. */
    gtk_widget_set_can_focus(scroll, TRUE);
    if (!resume_current(target, data, view)) goto done;
    gtk_text_view_set_editable(GTK_TEXT_VIEW(editor), TRUE);
    if (!resume_current(target, data, view)) goto done;
    gtk_text_view_set_cursor_visible(GTK_TEXT_VIEW(editor), TRUE);
    if (!resume_current(target, data, view)) goto done;
    gtk_widget_set_focusable(editor, TRUE);
    if (!resume_current(target, data, view) || !(tl_focused() == target || overlay.focused)) goto done;
    gtk_widget_grab_focus(editor);
    if (resume_current(target, data, view)) tl_overlay_sync();
done:
    g_object_unref(status); g_object_unref(scroll); g_object_unref(editor);
    json_object_unref(view);
}
JsonObject *tl_overlay_command(const char *operation, Target *target, JsonObject *data) {
    if (strcmp(operation, "overlay-language-configuration-open") == 0
        || strcmp(operation, "overlay-language-configuration-result") == 0
        || strcmp(operation, "overlay-language-configuration-close") == 0)
        return language_configuration_command(operation, data);
    gboolean visible = FALSE;
    if (strcmp(operation, "overlay-hide") != 0 && !tl_configuration_current(tl_string(data, "generation")))
        goto finished;
    if (strcmp(operation, "overlay-render") == 0) {
        visible = render(target, data);
        if (!visible && overlay.target == target
            && tl_configuration_current(tl_string(data, "generation"))
            && g_strcmp0(overlay.generation, tl_string(data, "generation")) == 0
            && overlay.revision <= tl_integer(data, "revision", -1)) hide_overlay("render-rejected");
    } else if (strcmp(operation, "overlay-renew") == 0) {
        gint64 until = tl_integer(data, "expiresAtMs", 0), now = tl_now();
        if (overlay.target == target && active() && until >= now && until - now <= 1000
            && tl_configuration_current(tl_string(data, "generation"))
            && g_strcmp0(overlay.generation, tl_string(data, "generation")) == 0
            && (tl_focused() == target || (!paused() && overlay.focused))
            && tl_metadata_matches(target, tl_object(data, "authority"))
            && (!paused() || pause_current(target, tl_object(data, "authority")))) {
            JsonObject *geometry = tl_geometry(target);
            if (geometry && tl_configuration_current(tl_string(data, "generation"))
                && g_strcmp0(overlay.generation, tl_string(data, "generation")) == 0) {
                overlay.expires = until;
                if (overlay.authority) json_object_unref(overlay.authority);
                overlay.authority = tl_copy(tl_object(data, "authority"));
                json_object_unref(geometry);
            }
            else { if (geometry) json_object_unref(geometry); hide_overlay("renew-geometry-or-generation"); }
        } else hide_overlay("renew-admission-rejected");
    } else if (strcmp(operation, "overlay-glossary-open") == 0) glossary_open(target, data);
    else if (strcmp(operation, "overlay-glossary-result") == 0) glossary_result(target, data);
    else if (strcmp(operation, "overlay-glossary-close") == 0) {
        GlossaryForm *form = overlay.glossary;
        if (form && overlay.target == target && form->revision == tl_integer(data, "revision", -1)
            && g_strcmp0(form->generation, tl_string(data, "generation")) == 0
            && g_strcmp0(form->id, tl_string(data, "formId")) == 0) glossary_close(FALSE, FALSE);
    } else if (strcmp(operation, "overlay-hide") == 0) hide_overlay("host-hide");
    else if (strcmp(operation, "overlay-edit") == 0) {
        if (overlay.target != target || overlay.revision != tl_integer(data, "revision", -1)
            || g_strcmp0(overlay.generation, tl_string(data, "generation")) != 0) goto finished;
        if (overlay.target == target && active() && overlay.pending_edit && boolean(data, "allowed")
            && tl_configuration_current(tl_string(data, "generation"))
            && g_strcmp0(overlay.generation, tl_string(data, "generation")) == 0
            && overlay.revision == tl_integer(data, "revision", -1)
            && tl_view_context_matches(target, overlay.view)
            && boolean(tl_object(overlay.view, "viewContext"), "canEdit")
            && g_strcmp0(tl_string(overlay.view, "state"), "EditingTranslation") == 0) {
            GtkWidget *editor = g_object_ref(overlay.text);
            GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(editor));
            GtkTextIter position;
            gtk_text_buffer_get_iter_at_offset(buffer, &position, MIN(overlay.edit_offset, gtk_text_buffer_get_char_count(buffer)));
            gtk_text_buffer_place_cursor(buffer, &position);
            if (overlay.text == editor && overlay.target == target
                && overlay.revision == tl_integer(data, "revision", -1)
                && tl_configuration_current(tl_string(data, "generation"))
                && tl_view_context_matches(target, overlay.view)) gtk_widget_grab_focus(editor);
            g_object_unref(editor);
        }
        overlay.pending_edit = FALSE;
        tl_overlay_sync();
    } else if (strcmp(operation, "overlay-resume") == 0) resume_editor(target, data);
finished:;
    JsonObject *reply = json_object_new();
    json_object_set_boolean_member(reply, "visible", visible || (overlay.popover && gtk_widget_get_visible(overlay.popover)));
    json_object_set_boolean_member(reply, "editing", overlay.text && gtk_text_view_get_editable(GTK_TEXT_VIEW(overlay.text)));
    if (strcmp(operation, "overlay-glossary-open") == 0) {
        GlossaryForm *form = overlay.glossary;
        json_object_set_boolean_member(reply, "opened", glossary_current(form) && overlay.target == target
            && form->revision == tl_integer(data, "revision", -1)
            && g_strcmp0(form->generation, tl_string(data, "generation")) == 0
            && g_strcmp0(form->id, tl_string(tl_object(data, "entry"), "formId")) == 0
            && form->settings_revision == tl_integer(tl_object(data, "entry"), "settingsRevision", -1)
            && tl_object(tl_object(data, "entry"), "token")
            && json_object_equal(form->token, tl_object(tl_object(data, "entry"), "token"))
            && glossary_contains(form, gtk_window_get_focus(GTK_WINDOW(overlay.top))));
    }
    return reply;
}
gboolean tl_overlay_key(gboolean accept, gboolean exit, gboolean control, guint keycode, guint64 sequence) {
    if (!overlay.popover) return FALSE;
    if (overlay.glossary) { tl_overlay_control_cancel(); return FALSE; }
    tl_overlay_sync();
    if ((!accept && !exit) || !overlay.focused || overlay.composing || paused()
        || !tl_configuration_current(overlay.generation) || !tl_view_context_matches(overlay.target, overlay.view)) return FALSE;
    EditorControl ticket = editor_control;
    if (control && !editor_control_current(&ticket)) return FALSE;
    Target *target = overlay.target;
    gint64 revision = overlay.revision;
    char *generation = g_strdup(overlay.generation);
    GtkWidget *editor = g_object_ref(overlay.text);
    JsonObject *view = json_object_ref(overlay.view);
    /* Provider inspection can dispatch a new configuration or destroy this
       editor. Revalidate the displayed context after that callback. */
    gboolean clear = tl_widget_ime_clear(editor);
    gboolean current = clear && overlay.text == editor && overlay.target == target && overlay.view == view
        && overlay.revision == revision && g_strcmp0(overlay.generation, generation) == 0
        && tl_configuration_current(generation) && tl_view_context_matches(target, overlay.view)
        && overlay.focused && !overlay.composing && active() && tl_now() <= overlay.expires;
    gboolean consumed = current && (exit || (accept && gtk_text_view_get_editable(GTK_TEXT_VIEW(editor))));
    if (control) consumed = consumed && editor_control_current(&ticket)
        && tl_overlay_key_claim(keycode, sequence, ticket.keycode, ticket.sequence);
    tl_overlay_control_cancel();
    if (consumed) emit(exit ? "escape" : "return", NULL);
    json_object_unref(view);
    g_object_unref(editor);
    g_free(generation);
    return consumed;
}
