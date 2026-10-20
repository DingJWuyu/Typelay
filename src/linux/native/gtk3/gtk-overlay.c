#include "gtk-overlay.h"
#include <string.h>

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
typedef struct { GtkEntryBuffer parent_instance; gboolean locked; } TlGtk3GlossaryBuffer;
typedef GtkEntryBufferClass TlGtk3GlossaryBufferClass;
G_DEFINE_TYPE(TlGtk3GlossaryBuffer, tl_gtk3_glossary_buffer, GTK_TYPE_ENTRY_BUFFER)
static guint glossary_buffer_insert(GtkEntryBuffer *buffer, guint position, const gchar *chars, guint count) {
    if (((TlGtk3GlossaryBuffer *)buffer)->locked) return 0;
    return GTK_ENTRY_BUFFER_CLASS(tl_gtk3_glossary_buffer_parent_class)->insert_text(buffer, position, chars, count);
}
static guint glossary_buffer_delete(GtkEntryBuffer *buffer, guint position, guint count) {
    if (((TlGtk3GlossaryBuffer *)buffer)->locked) return 0;
    return GTK_ENTRY_BUFFER_CLASS(tl_gtk3_glossary_buffer_parent_class)->delete_text(buffer, position, count);
}
static void tl_gtk3_glossary_buffer_class_init(TlGtk3GlossaryBufferClass *klass) {
    klass->insert_text = glossary_buffer_insert; klass->delete_text = glossary_buffer_delete;
}
static void tl_gtk3_glossary_buffer_init(TlGtk3GlossaryBuffer *buffer) { buffer->locked = FALSE; }

/* Auxiliary inputs belong to the overlay family, but are never source targets
 * or translation editors. Their identity and IME proofs live only for this form. */
typedef struct {
    guint refs, selected_direction;
    GtkWidget *box, *terms[2], *clients[2], *scope, *save, *cancel, *feedback;
    GtkWidget *scope_popup, *scope_choices;
    GPtrArray *scope_options;
    TlGtk3GlossaryBuffer *buffers[2]; /* owned by the corresponding entry */
    GtkIMContext *contexts[2]; /* weak provider-owned bindings */
    gulong handlers[2];
    JsonObject *token, *source_authority, *source_view;
    JsonArray *directions;
    char *id, *target, *generation, *origin_state;
    Target *origin;
    GtkWidget *origin_top;
    gpointer origin_buffer; /* compared only while the original target is live */
    gboolean source_origin, return_started, returned;
    guint64 returned_focus;
    gint64 revision, settings_revision;
    gboolean clear[2], composing[2], probing, pending, finished, closing;
    gint reported_ime;
} GlossaryForm;

static struct {
    Target *target;
    GtkWidget *popover, *top, *card, *header, *language, *status, *scroll, *text;
    GtkWidget *footer, *hint, *actions, *expand, *menu, *source, *destination, *resume, *exit, *apply_languages;
    GtkWidget *style_label, *style_inherit, *style_natural, *style_formal, *alternative, *action_feedback, *glossary_action;
    GlossaryForm *glossary;
    JsonObject *glossary_draft_token;
    GtkTextBuffer *glossary_draft_buffer; /* held only for source-origin preservation */
    gboolean pending_glossary;
    LanguageMenu source_languages, target_languages;
    JsonObject *menu_token;
    char *menu_target, *menu_generation;
    gint64 menu_revision, menu_settings_revision;
    GtkCssProvider *css, *geometry_css;
    JsonObject *view, *authority, *editor_token;
    gulong focus_handler, active_handler;
    gboolean setting_text, setting_languages, composing, focused, expanded, pending_edit, pending_resume, private_editor;
    gboolean return_started, return_source_seen;
    char *generation;
    gint edit_offset, body_height, body_duration;
    gint64 revision, expires;
    guint expiry_timer;
} overlay;

/* Only the synchronous grab_focus call owns this proof. Keep its monotonic
 * failure outside overlay: hide/destroy clears overlay and must not revive it. */
typedef struct {
    GtkWidget *source, *editor, *top;
    gboolean failed;
} EditHandoff;
static EditHandoff *edit_handoff;

typedef struct {
    GtkWidget *top, *destination;
    guint emissions;
    gboolean failed;
} GlossaryHandoff;
static GlossaryHandoff *glossary_handoff;

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
static gboolean render_translation_actions(JsonObject *view) {
    JsonObject *state = translation_actions(view), *context = tl_object(view, "viewContext");
    if (state) json_object_ref(state);
    if (context) json_object_ref(context);
    const char *style = tl_string(state, "applicationStyle"), *feedback = text_or_empty(state, "feedback");
    gboolean released = boolean(state, "styleReleased"), current = translation_menu_current();
    gboolean change = current && boolean(state, "canChangeStyle");
    GtkWidget *widgets[] = {overlay.style_label, overlay.style_inherit, overlay.style_natural,
        overlay.style_formal, overlay.alternative, overlay.glossary_action, overlay.action_feedback};
    const char *labels[] = {NULL, !style ? "✓ 继承" : "继承",
        g_strcmp0(style, "Natural") == 0 ? "✓ 自然" : "自然",
        g_strcmp0(style, "Formal") == 0 ? "✓ 正式" : "正式", "换种说法", "加入术语表", feedback};
    gboolean visible[] = {released, released, released, released, boolean(state, "alternativeReleased"), boolean(state, "glossaryReleased"), *feedback != '\0'};
    gboolean enabled[] = {TRUE, current && boolean(state, "canInheritStyle"), change, change,
        current && boolean(state, "canRequestAlternative"), current && boolean(state, "canOpenGlossary"), TRUE};
    const char *reasons[] = {NULL, tl_string(state, "inheritUnavailableReason"),
        tl_string(state, "styleUnavailableReason"), tl_string(state, "styleUnavailableReason"),
        tl_string(state, "alternativeUnavailableReason"), tl_string(state, "glossaryUnavailableReason"), NULL};
    /* Every GTK setter can dispatch callbacks. Keep widgets and JSON alive and
     * stop immediately if a callback replaces this rendered view. */
    json_object_ref(view);
    for (guint index = 0; index < G_N_ELEMENTS(widgets); index++) g_object_ref(widgets[index]);
    for (guint index = 0; index < G_N_ELEMENTS(widgets) && translation_render_current(view, context); index++) {
        GtkWidget *widget = widgets[index];
        if (labels[index]) {
            if (GTK_IS_LABEL(widget)) gtk_label_set_text(GTK_LABEL(widget), labels[index]);
            else gtk_button_set_label(GTK_BUTTON(widget), labels[index]);
        }
        if (!translation_render_current(view, context)) break;
        gtk_widget_set_sensitive(widget, enabled[index]);
        if (!translation_render_current(view, context)) break;
        gtk_widget_set_tooltip_text(widget, enabled[index] ? NULL : reasons[index]);
        if (!translation_render_current(view, context)) break;
        gtk_widget_set_visible(widget, visible[index]);
    }
    gboolean retained = translation_render_current(view, context);
    for (guint index = 0; index < G_N_ELEMENTS(widgets); index++) g_object_unref(widgets[index]);
    json_object_unref(view);
    if (context) json_object_unref(context);
    if (state) json_object_unref(state);
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
/* GtkComboBox opens a separate GtkMenu surface on Wayland. Its keyboard grab
 * deactivates the source toplevel, correctly retiring our source authority.
 * Keep language navigation in a nonmodal child popover instead; never exempt
 * menus from the normal active-window or source-identity checks. */
static GtkTreeView *language_list(GtkWidget *widget) {
    GtkWidget *popup = GTK_WIDGET(gtk_menu_button_get_popover(GTK_MENU_BUTTON(widget)));
    return GTK_TREE_VIEW(gtk_bin_get_child(GTK_BIN(gtk_bin_get_child(GTK_BIN(popup)))));
}
static const char *selected_language(GtkWidget *widget, LanguageMenu *menu) {
    GtkTreeModel *model;
    GtkTreeIter iter;
    if (!gtk_tree_selection_get_selected(gtk_tree_view_get_selection(language_list(widget)), &model, &iter)) return NULL;
    GtkTreePath *path = gtk_tree_model_get_path(model, &iter);
    gint index = gtk_tree_path_get_indices(path)[0];
    gtk_tree_path_free(path);
    return menu->choices && index >= 0 && (guint)index < json_array_get_length(menu->choices)
        ? tl_string(json_array_get_object_element(menu->choices, (guint)index), "code") : NULL;
}
static void select_language(GtkWidget *widget, LanguageMenu *menu, const char *code) {
    gint index = language_index(menu->choices, code);
    if (index < 0) index = menu->choices ? (gint)json_array_get_length(menu->choices) : 0;
    GtkTreePath *path = gtk_tree_path_new_from_indices(index, -1);
    gtk_tree_view_set_cursor(language_list(widget), path, NULL, FALSE);
    gtk_tree_path_free(path);
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
static void close_language_menu(JsonObject *view) {
    GtkWidget *menu = overlay.menu;
    /* Only the user's close/Apply/Escape inside this live language family may
     * hand focus to its trigger. Outside focus and expired views stay untouched. */
    if (language_handoff_current(view) && gtk_widget_get_mapped(menu) && language_focus_current())
        gtk_widget_grab_focus(overlay.language);
    if (overlay.view == view && overlay.menu == menu && menu) gtk_widget_hide(menu);
}
static void language_selection_changed(GtkTreeSelection *selection, gpointer data) {
    GtkWidget *button = data;
    if (!overlay.popover || (button != overlay.source && button != overlay.destination)) return;
    GtkTreeModel *model;
    GtkTreeIter iter;
    if (gtk_tree_selection_get_selected(selection, &model, &iter)) {
        char *label = NULL;
        gtk_tree_model_get(model, &iter, 1, &label, -1);
        char *title = g_strdup_printf("%s ▾", label ? label : "");
        gtk_button_set_label(GTK_BUTTON(button), title);
        g_free(title); g_free(label);
    }
    refresh_language_apply();
}
static void language_popup_toggled(GtkToggleButton *button, gpointer data) {
    (void)data;
    GtkWidget *widget = GTK_WIDGET(button);
    if (!overlay.popover || (widget != overlay.source && widget != overlay.destination) || !active()) return;
    if (gtk_toggle_button_get_active(button))
        gtk_widget_grab_focus(GTK_WIDGET(language_list(widget)));
}
static void language_row_activated(GtkTreeView *list, GtkTreePath *path, GtkTreeViewColumn *column, gpointer data) {
    (void)list; (void)path; (void)column;
    GtkWidget *button = g_object_ref(data);
    if ((button != overlay.source && button != overlay.destination) || !active()
        || !gtk_toggle_button_get_active(GTK_TOGGLE_BUTTON(button))) {
        g_object_unref(button);
        return;
    }
    /* Return focus before closing the child surface. Both are in the same
     * source-bound overlay family, with no native keyboard grab. */
    gtk_widget_grab_focus(button);
    if (button == overlay.source || button == overlay.destination)
        gtk_toggle_button_set_active(GTK_TOGGLE_BUTTON(button), FALSE);
    g_object_unref(button);
}
static gboolean language_popup_key(GtkWidget *popup, GdkEventKey *event, gpointer data) {
    (void)popup;
    if (event->keyval != GDK_KEY_Escape) return FALSE;
    language_row_activated(NULL, NULL, NULL, data);
    return TRUE;
}
static GtkWidget *language_button(void) {
    GtkWidget *button = gtk_menu_button_new();
    GtkWidget *popup = gtk_popover_new(button);
    gtk_popover_set_modal(GTK_POPOVER(popup), FALSE);
    g_object_set_data(G_OBJECT(popup), "typelay-overlay", GINT_TO_POINTER(1));
    GtkListStore *model = gtk_list_store_new(2, G_TYPE_STRING, G_TYPE_STRING);
    GtkWidget *list = gtk_tree_view_new_with_model(GTK_TREE_MODEL(model));
    g_object_unref(model);
    gtk_tree_view_set_headers_visible(GTK_TREE_VIEW(list), FALSE);
    gtk_tree_view_set_enable_search(GTK_TREE_VIEW(list), FALSE);
    gtk_tree_view_set_activate_on_single_click(GTK_TREE_VIEW(list), TRUE);
    gtk_tree_view_insert_column_with_attributes(GTK_TREE_VIEW(list), -1, "",
        gtk_cell_renderer_text_new(), "text", 1, NULL);
    GtkWidget *scroll = gtk_scrolled_window_new(NULL, NULL);
    gtk_widget_set_can_focus(scroll, FALSE);
    gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(scroll), GTK_POLICY_NEVER, GTK_POLICY_AUTOMATIC);
    gtk_scrolled_window_set_max_content_height(GTK_SCROLLED_WINDOW(scroll), 240);
    gtk_scrolled_window_set_propagate_natural_height(GTK_SCROLLED_WINDOW(scroll), TRUE);
    gtk_container_add(GTK_CONTAINER(scroll), list);
    gtk_container_add(GTK_CONTAINER(popup), scroll);
    gtk_widget_show_all(scroll);
    gtk_menu_button_set_popover(GTK_MENU_BUTTON(button), popup);
    g_signal_connect_after(button, "toggled", G_CALLBACK(language_popup_toggled), NULL);
    g_signal_connect_object(gtk_tree_view_get_selection(GTK_TREE_VIEW(list)), "changed",
        G_CALLBACK(language_selection_changed), button, 0);
    g_signal_connect_object(list, "row-activated", G_CALLBACK(language_row_activated), button, 0);
    g_signal_connect_object(popup, "key-press-event", G_CALLBACK(language_popup_key), button, 0);
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
        GtkListStore *model = GTK_LIST_STORE(gtk_tree_view_get_model(language_list(widget)));
        gtk_list_store_clear(model);
        if (!language_menu_current(widget, menu, view)) goto done;
        if (choices) for (guint index = 0; index < json_array_get_length(choices); index++) {
            JsonObject *choice = json_array_get_object_element(choices, index);
            GtkTreeIter iter;
            gtk_list_store_insert_with_values(model, &iter, -1, 0, tl_string(choice, "code"), 1, tl_string(choice, "label"), -1);
            if (!language_menu_current(widget, menu, view)) goto done;
        }
        if (missing) {
            GtkTreeIter iter;
            gtk_list_store_insert_with_values(model, &iter, -1, 0, saved, 1, label, -1);
        }
    }
    if (!language_menu_current(widget, menu, view)) goto done;
    /* Renewals must not discard an unapplied choice while the menu is open. */
    if (reset || saved_changed || rebuild)
        select_language(widget, menu, !reset && !saved_changed && language_index(choices, selection) >= 0 ? selection : saved);
done:
    g_free(selection);
    return language_menu_current(widget, menu, view);
}
static gboolean reset_language_selection(JsonObject *view) {
    GtkWidget *source = g_object_ref(overlay.source), *target = g_object_ref(overlay.destination);
    char *source_code = g_strdup(overlay.source_languages.saved), *target_code = g_strdup(overlay.target_languages.saved);
    overlay.setting_languages = TRUE;
    select_language(source, &overlay.source_languages, source_code);
    if (overlay.view == view && overlay.destination == target)
        select_language(target, &overlay.target_languages, target_code);
    gboolean current = overlay.view == view && overlay.source == source && overlay.destination == target;
    if (current) { overlay.setting_languages = FALSE; refresh_language_apply(); current = overlay.view == view; }
    g_object_unref(source); g_object_unref(target); g_free(source_code); g_free(target_code);
    return current;
}
static gboolean render_languages(JsonObject *view) {
    JsonArray *source_choices = language_choices(view, "source"), *target_choices = language_choices(view, "target");
    const char *source = text_or_empty(view, "sourceLanguage"), *target = text_or_empty(view, "targetLanguage");
    char *source_label = language_title(view, "sourceLanguageLabel", source, source_choices);
    char *target_label = language_title(view, "targetLanguageLabel", target, target_choices);
    char *title = g_strdup_printf("%s → %s", source_label, target_label);
    gboolean valid = source_choices && target_choices;
    gboolean reset = !gtk_widget_get_visible(overlay.menu) || !valid;
    JsonObject *rendered = json_object_ref(overlay.view);
    GtkWidget *source_widget = g_object_ref(overlay.source), *target_widget = g_object_ref(overlay.destination);
    overlay.setting_languages = TRUE;
    gboolean current = sync_language(source_widget, &overlay.source_languages, source_choices, source, source_label, reset, rendered)
        && sync_language(target_widget, &overlay.target_languages, target_choices, target, target_label, reset, rendered);
    if (current) { gtk_widget_set_sensitive(source_widget, valid); current = overlay.view == rendered; }
    if (current) { gtk_widget_set_sensitive(target_widget, valid); current = overlay.view == rendered; }
    if (current) { overlay.setting_languages = FALSE; refresh_language_apply(); current = overlay.view == rendered; }
    if (current) { gtk_button_set_label(GTK_BUTTON(overlay.language), title); current = overlay.view == rendered; }
    g_object_unref(source_widget); g_object_unref(target_widget); json_object_unref(rendered);
    if (source_choices) json_array_unref(source_choices);
    if (target_choices) json_array_unref(target_choices);
    g_free(source_label); g_free(target_label); g_free(title);
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
    char *hint_text = g_strdup(hint), *exit_text = g_strdup(text_or_empty(shortcuts, "exitHint"));
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
        && tl_configuration_current(overlay.generation) && tl_view_context_matches(target, overlay.view);
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
static void close_language_popups_outside(GtkWidget *widget, gboolean trigger_click) {
    for (guint index = 0; index < 2; index++) {
        GtkWidget *button = index == 0 ? overlay.source : overlay.destination;
        if (!button || !gtk_toggle_button_get_active(GTK_TOGGLE_BUTTON(button))) continue;
        GtkWidget *popup = GTK_WIDGET(gtk_menu_button_get_popover(GTK_MENU_BUTTON(button)));
        if (widget && (widget == button || gtk_widget_is_ancestor(widget, button)
            || widget == popup || gtk_widget_is_ancestor(widget, popup))) continue;
        /* A real trigger click closes this menu; preserve its owned focus
         * before hiding the child popup. Every other destination owns its own
         * transition and must never be stolen, including external clicks. */
        if (trigger_click && widget && overlay.language && (widget == overlay.language
            || gtk_widget_is_ancestor(widget, overlay.language))
            && language_handoff_current(overlay.view) && language_focus_current())
            gtk_widget_grab_focus(overlay.language);
        if (!overlay.popover || (button != overlay.source && button != overlay.destination)) return;
        gtk_toggle_button_set_active(GTK_TOGGLE_BUTTON(button), FALSE);
        if (!overlay.popover) return;
    }
}
void tl_overlay_pointer(GdkEvent *event) {
    if (event->type == GDK_BUTTON_PRESS) close_language_popups_outside(gtk_get_event_widget(event), TRUE);
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
        && tl_configuration_current(overlay.generation)
        && tl_view_context_matches(overlay.target, overlay.view)
        && tl_now() <= ticket->expires;
}
void tl_overlay_control_begin(guint keycode, guint64 sequence) {
    tl_overlay_control_cancel();
    if (overlay.glossary || !keycode || !sequence || editor_control_serial == G_MAXUINT64 || !active()
        || !overlay.text || !overlay.view || paused() || overlay.composing || !overlay.focused
        || gtk_window_get_focus(GTK_WINDOW(overlay.top)) != overlay.text
        || !gtk_text_view_get_editable(GTK_TEXT_VIEW(overlay.text))
        || !tl_configuration_current(overlay.generation)
        || !tl_view_context_matches(overlay.target, overlay.view) || tl_now() > overlay.expires) return;
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
        gtk_widget_destroy(form->scope_popup);
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
        && G_OBJECT_TYPE(target->widget) == GTK_TYPE_ENTRY && G_OBJECT_TYPE(target->buffer) == GTK_TYPE_ENTRY_BUFFER
        && !target->mutating && !target->quarantined && target->im_context && target->im_observed
        && g_object_get_data(G_OBJECT(target->widget), TL_FCITX_CONTEXT_V1) == target->im_context
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
static gboolean glossary_focus_emission(GSignalInvocationHint *hint, guint count, const GValue *values, gpointer data) {
    (void)hint;
    GlossaryHandoff *guard = data;
    if (count != 2 || g_value_get_object(&values[0]) != guard->top
        || g_value_get_object(&values[1]) != guard->destination || guard->emissions++) guard->failed = TRUE;
    return TRUE;
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
    if (returning ? !glossary_contains(form, from) : from != form->origin->widget) return FALSE;
    GtkWidget *to = returning ? form->origin->widget : form->clients[0];
    GlossaryHandoff guard = {overlay.top, to, 0, FALSE};
    guint signal = g_signal_lookup("set-focus", GTK_TYPE_WINDOW);
    gulong hook = signal ? g_signal_add_emission_hook(signal, 0, glossary_focus_emission, &guard, NULL) : 0;
    if (!hook) return FALSE;
    JsonObject *identity = glossary_receipt(form);
    gboolean begun = tl_glossary_transfer_begin(form->origin, identity, returning ? "Returning" : "Opening");
    json_object_unref(identity);
    if (!begun) { g_signal_remove_emission_hook(signal, hook); return FALSE; }
    glossary_handoff = &guard;
    gboolean moved = TRUE;
    if (returning) moved = tl_return(form->origin, form->source_authority);
    else gtk_widget_grab_focus(to);
    glossary_handoff = NULL;
    g_signal_remove_emission_hook(signal, hook);
    gboolean current = moved && !guard.failed && guard.emissions == 1 && glossary_current(form)
        && glossary_source_basis(form) && overlay.top == guard.top && active()
        && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == to;
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
        && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == target->widget;
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
    if (return_editor) {
        /* The host can retire editor readiness while aux owns focus. Return to
         * its original read-only widget; fresh binding/IME ACK enables editing. */
        if (form->source_origin) {
            if (glossary_identity_current(form) && glossary_source_basis(form)
                && glossary_contains(form, gtk_window_get_focus(GTK_WINDOW(overlay.top))))
                (void)tl_return(form->origin, form->source_authority);
        } else {
            gtk_widget_set_can_focus(editor, TRUE);
            if (glossary_identity_current(form) && overlay.text == editor
                && glossary_contains(form, gtk_window_get_focus(GTK_WINDOW(overlay.top)))) gtk_widget_grab_focus(editor);
        }
    }
    if (owned) {
        if (gtk_widget_get_parent(form->box)) gtk_container_remove(GTK_CONTAINER(gtk_widget_get_parent(form->box)), form->box);
        gtk_widget_destroy(form->box);
        glossary_unref(form);
    }
    if (editor) g_object_unref(editor);
    glossary_unref(form);
}
gboolean tl_overlay_auxiliary_key(guint keyval, GdkModifierType modifiers) {
    /* Match the dispatcher's effective modifiers; lock states are not chords. */
    modifiers &= GDK_SHIFT_MASK | GDK_CONTROL_MASK | GDK_MOD1_MASK | GDK_MOD3_MASK
        | GDK_MOD4_MASK | GDK_MOD5_MASK | GDK_SUPER_MASK | GDK_HYPER_MASK | GDK_META_MASK;
    if (keyval != GDK_KEY_Escape || modifiers) return FALSE;
    if (active() && overlay.top) {
        GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(overlay.top));
        GtkWidget *buttons[] = {overlay.source, overlay.destination};
        for (guint index = 0; index < G_N_ELEMENTS(buttons); index++) {
            GtkWidget *button = buttons[index];
            if (!button || !gtk_toggle_button_get_active(GTK_TOGGLE_BUTTON(button))) continue;
            GtkWidget *popup = GTK_WIDGET(gtk_menu_button_get_popover(GTK_MENU_BUTTON(button)));
            if (focus && gtk_widget_is_ancestor(focus, popup)) {
                language_row_activated(NULL, NULL, NULL, button);
                return TRUE;
            }
        }
        if (overlay.menu && gtk_widget_get_mapped(overlay.menu) && language_focus_current()
            && language_handoff_current(overlay.view)) {
            close_language_menu(overlay.view);
            return TRUE;
        }
    }
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
/* A rejected MOVE drop must not report success and delete its source just
 * because GtkEntry's default drop handler ignores a zero-byte insertion. */
static gboolean glossary_drag_motion(GtkWidget *widget, GdkDragContext *context, gint x, gint y, guint time, gpointer data) {
    (void)x; (void)y; (void)data;
    if (!((TlGtk3GlossaryBuffer *)gtk_entry_get_buffer(GTK_ENTRY(widget)))->locked) return FALSE;
    gdk_drag_status(context, 0, time);
    return TRUE;
}
static gboolean glossary_drag_drop(GtkWidget *widget, GdkDragContext *context, gint x, gint y, guint time, gpointer data) {
    (void)x; (void)y; (void)data;
    if (!((TlGtk3GlossaryBuffer *)gtk_entry_get_buffer(GTK_ENTRY(widget)))->locked) return FALSE;
    gtk_drag_finish(context, FALSE, FALSE, time);
    return TRUE;
}
static void glossary_drag_received(GtkWidget *widget, GdkDragContext *context, gint x, gint y,
    GtkSelectionData *selection, guint info, guint time, gpointer data) {
    (void)x; (void)y; (void)selection; (void)info; (void)data;
    if (!((TlGtk3GlossaryBuffer *)gtk_entry_get_buffer(GTK_ENTRY(widget)))->locked) return;
    g_signal_stop_emission_by_name(widget, "drag-data-received");
    gtk_drag_finish(context, FALSE, FALSE, time);
}
static gboolean glossary_pointer(GtkWidget *widget, GdkEventButton *event, gpointer data) {
    (void)event; (void)data;
    return ((TlGtk3GlossaryBuffer *)gtk_entry_get_buffer(GTK_ENTRY(widget)))->locked;
}
static void glossary_freeze_inputs(GlossaryForm *form, gboolean frozen) {
    if (!glossary_current(form)) return;
    for (guint index = 0; index < 2; index++) form->buffers[index]->locked = frozen;
    for (guint index = 0; index < 2 && glossary_current(form); index++)
        gtk_widget_set_can_focus(form->terms[index], !frozen);
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
    const char *source = gtk_entry_get_text(GTK_ENTRY(form->terms[0])), *target = gtk_entry_get_text(GTK_ENTRY(form->terms[1]));
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
    modifiers &= GDK_SHIFT_MASK | GDK_CONTROL_MASK | GDK_MOD1_MASK | GDK_MOD3_MASK
        | GDK_MOD4_MASK | GDK_MOD5_MASK | GDK_SUPER_MASK | GDK_HYPER_MASK | GDK_META_MASK;
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
static gboolean glossary_key_event(GtkWidget *widget, GdkEventKey *event, gpointer data) {
    (void)data;
    if (!overlay.glossary || (widget != overlay.glossary->box && widget != overlay.glossary->scope_popup)) return FALSE;
    return glossary_key(event->keyval, event->state);
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
static gboolean glossary_source_ready(void) {
    Target *target = overlay.target;
    const char *state = tl_string(overlay.view, "state");
    if (!target || target->dead || !target->widget || !target->buffer || !active()
        || (g_strcmp0(state, "ValidTranslation") != 0 && g_strcmp0(state, "EditedTranslationPending") != 0)
        || G_OBJECT_TYPE(target->widget) != GTK_TYPE_ENTRY || G_OBJECT_TYPE(target->buffer) != GTK_TYPE_ENTRY_BUFFER
        || gtk_window_get_focus(GTK_WINDOW(overlay.top)) != target->widget || tl_focused() != target
        || !translation_menu_current()
        || (overlay.pending_glossary && (!overlay.text || !overlay.glossary_draft_buffer
            || gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text)) != overlay.glossary_draft_buffer))) return FALSE;
    JsonObject *view = json_object_ref(overlay.view), *basis = tl_copy(overlay.authority);
    gboolean current = tl_ime_clear(target) && overlay.target == target && overlay.view == view && active()
        && tl_focused() == target && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == target->widget
        && tl_metadata_matches(target, basis)
        && (gint64)target->im_state.context_generation == tl_integer(tl_object(basis, "contextStamp"), "context", -1)
        && (gint64)target->input_revision == tl_integer(basis, "inputRevision", -1)
        && (gint64)target->content_revision == tl_integer(basis, "contentRevision", -1)
        && overlay.view == view && translation_menu_current() && active() && tl_focused() == target
        && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == target->widget;
    json_object_unref(view); json_object_unref(basis);
    return current;
}
static gboolean glossary_origin_ready(void) {
    return g_strcmp0(tl_string(overlay.view, "state"), "EditingTranslation") == 0
        ? glossary_editor_ready() : glossary_source_ready();
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
    gboolean origin_ready = overlay.pending_glossary && glossary_origin_ready();
    if (!origin_ready || !overlay.pending_glossary || overlay.glossary || !target || overlay.target != target
        || !translation_menu_current() || !active()
        || overlay.revision != tl_integer(data, "revision", -1)
        || g_strcmp0(overlay.generation, tl_string(data, "generation")) != 0
        || !id || !*id || !token || !json_object_equal(token, overlay.menu_token)
        || tl_integer(entry, "settingsRevision", -1) != overlay.menu_settings_revision
        || !glossary_directions_valid(directions)
        || source_origin != (g_strcmp0(tl_string(overlay.view, "state"), "EditingTranslation") != 0)
        || !boolean(translation_actions(overlay.view), "glossaryReleased")) {
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
        gtk_box_pack_start(GTK_BOX(form->box), label, FALSE, FALSE, 0);
        form->buffers[index] = g_object_new(tl_gtk3_glossary_buffer_get_type(), NULL);
        form->terms[index] = gtk_entry_new_with_buffer(GTK_ENTRY_BUFFER(form->buffers[index]));
        g_object_unref(form->buffers[index]);
        /* Parent before realization so the production target scanner excludes it. */
        gtk_box_pack_start(GTK_BOX(form->box), form->terms[index], FALSE, FALSE, 0);
        form->clients[index] = form->terms[index];
        gtk_widget_set_name(form->terms[index], index == 0 ? "typelay-glossary-source" : "typelay-glossary-target");
        gtk_entry_set_placeholder_text(GTK_ENTRY(form->terms[index]), names[index]);
        g_signal_connect(form->terms[index], "changed", G_CALLBACK(glossary_term_changed), form);
        g_signal_connect(form->clients[index], "preedit-changed", G_CALLBACK(glossary_preedit), form);
        g_signal_connect(form->terms[index], "drag-motion", G_CALLBACK(glossary_drag_motion), NULL);
        g_signal_connect(form->terms[index], "drag-drop", G_CALLBACK(glossary_drag_drop), NULL);
        g_signal_connect(form->terms[index], "drag-data-received", G_CALLBACK(glossary_drag_received), NULL);
        g_signal_connect(form->terms[index], "button-press-event", G_CALLBACK(glossary_pointer), NULL);
        g_signal_connect(form->terms[index], "button-release-event", G_CALLBACK(glossary_pointer), NULL);
    }
    form->scope = gtk_button_new_with_label("全局 ▾");
    gtk_widget_set_name(form->scope, "typelay-glossary-scope");
    gtk_box_pack_start(GTK_BOX(form->box), form->scope, FALSE, FALSE, 0);
    form->scope_popup = gtk_popover_new(form->scope);
    g_object_ref_sink(form->scope_popup);
    gtk_popover_set_modal(GTK_POPOVER(form->scope_popup), FALSE);
    gtk_widget_set_no_show_all(form->scope_popup, TRUE);
    g_object_set_data(G_OBJECT(form->scope_popup), "typelay-overlay", GINT_TO_POINTER(1));
    form->scope_choices = gtk_box_new(GTK_ORIENTATION_VERTICAL, 0);
    form->scope_options = g_ptr_array_new_with_free_func(g_object_unref);
    GtkWidget *scope_scroll = gtk_scrolled_window_new(NULL, NULL);
    gtk_widget_set_can_focus(scope_scroll, FALSE);
    gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(scope_scroll), GTK_POLICY_NEVER, GTK_POLICY_AUTOMATIC);
    gtk_scrolled_window_set_max_content_height(GTK_SCROLLED_WINDOW(scope_scroll), 240);
    gtk_scrolled_window_set_propagate_natural_height(GTK_SCROLLED_WINDOW(scope_scroll), TRUE);
    gtk_container_add(GTK_CONTAINER(scope_scroll), form->scope_choices);
    gtk_container_add(GTK_CONTAINER(form->scope_popup), scope_scroll);
    for (guint index = 0; index <= json_array_get_length(directions); index++) {
        JsonObject *direction = index ? json_array_get_object_element(directions, index - 1) : NULL;
        char *label = direction ? g_strdup_printf("%s → %s", tl_string(direction, "sourceName"), tl_string(direction, "targetName")) : g_strdup("全局");
        GtkWidget *choice = gtk_button_new_with_label(label); g_free(label);
        gtk_box_pack_start(GTK_BOX(form->scope_choices), choice, FALSE, FALSE, 0);
        g_ptr_array_add(form->scope_options, g_object_ref(choice));
        g_signal_connect(choice, "clicked", G_CALLBACK(glossary_scope_clicked), form);
    }
    g_signal_connect(form->scope, "clicked", G_CALLBACK(glossary_scope_clicked), form);
    gtk_widget_show_all(scope_scroll);
    g_signal_connect(form->scope_popup, "key-press-event", G_CALLBACK(glossary_key_event), NULL);
    form->feedback = gtk_label_new("仅保存明确填写的两个词，不从译文自动学习");
    gtk_label_set_xalign(GTK_LABEL(form->feedback), 0);
    gtk_label_set_line_wrap(GTK_LABEL(form->feedback), TRUE);
    gtk_box_pack_start(GTK_BOX(form->box), form->feedback, FALSE, FALSE, 0);
    GtkWidget *buttons = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
    form->save = gtk_button_new_with_label("保存"); form->cancel = gtk_button_new_with_label("取消");
    gtk_widget_set_name(form->save, "typelay-glossary-save"); gtk_widget_set_name(form->cancel, "typelay-glossary-cancel");
    g_signal_connect(form->save, "clicked", G_CALLBACK(glossary_clicked), form);
    g_signal_connect(form->cancel, "clicked", G_CALLBACK(glossary_clicked), form);
    gtk_box_pack_start(GTK_BOX(buttons), form->save, FALSE, FALSE, 0);
    gtk_box_pack_start(GTK_BOX(buttons), form->cancel, FALSE, FALSE, 0);
    gtk_box_pack_start(GTK_BOX(form->box), buttons, FALSE, FALSE, 0);
    g_signal_connect_after(form->box, "key-press-event", G_CALLBACK(glossary_key_event), NULL);
    /* Widget setters can dispatch callbacks while the form is still detached.
     * Recheck the initiating editor before attaching or taking its focus. */
    if (overlay.glossary || !overlay.top || !translation_menu_current() || !active()
        || overlay.target != target || overlay.revision != form->revision
        || g_strcmp0(overlay.generation, form->generation) != 0
        || gtk_window_get_focus(GTK_WINDOW(overlay.top)) != (source_origin ? target->widget : overlay.text)
        || (source_origin && !glossary_source_ready())) {
        glossary_reject_open(target, data, id);
        glossary_unref(form);
        return;
    }
    overlay.glossary = form;
    if (overlay.glossary_draft_token) json_object_unref(overlay.glossary_draft_token);
    overlay.glossary_draft_token = tl_copy(form->token);
    glossary_ref(form);
    gtk_box_pack_start(GTK_BOX(overlay.card), form->box, FALSE, FALSE, 0);
    gtk_widget_show_all(form->box);
    if (glossary_current(form)) gtk_widget_set_visible(overlay.menu, FALSE);
    if (glossary_current(form)) {
        if (source_origin) {
            if (!glossary_transfer(form, FALSE)) glossary_close(FALSE, TRUE);
        } else gtk_widget_grab_focus(form->terms[0]);
    }
    if (glossary_current(form)) glossary_sync();
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
static void emit_editor_focus(GtkWidget *widget) {
    const char *family = !active() || !widget ? "Other"
        : widget == overlay.target->widget ? "Source"
        : widget == overlay.text || glossary_contains(overlay.glossary, widget) ? "Overlay" : "Other";
    JsonObject *event = json_object_new();
    json_object_set_string_member(event, "target", overlay.target->id);
    json_object_set_string_member(event, "operation", "focus");
    json_object_set_int_member(event, "revision", overlay.revision);
    json_object_set_string_member(event, "generation", overlay.generation);
    json_object_set_boolean_member(event, "value", strcmp(family, "Overlay") == 0);
    json_object_set_string_member(event, "family", family);
    tl_send_event("ui", event);
}
static void focus_changed(GtkWindow *window, GtkWidget *widget, gpointer data) {
    (void)data;
    if (glossary_handoff) return;
    close_language_popups_outside(widget, FALSE);
    tl_overlay_control_cancel();
    if (widget != overlay.text) overlay.pending_glossary = FALSE;
    glossary_sync();
    if (overlay.glossary && overlay.glossary->source_origin
        && (overlay.glossary->return_started || !glossary_contains(overlay.glossary, widget))) {
        /* External buttons/NULL are not Targets and may emit no source input.
         * After return, even source again cannot reuse the owned transfer. */
        tl_overlay_hide();
        return;
    }
    /* set-focus's argument is the destination. GTK internally clears its old
     * focus pointer during transfer; that transient NULL is not set-focus(NULL). */
    if (edit_handoff && GTK_WIDGET(window) == edit_handoff->top
        && widget != edit_handoff->source && widget != edit_handoff->editor)
        edit_handoff->failed = TRUE;
    if ((edit_handoff || overlay.private_editor) && overlay.target) {
        /* Once this private return reaches the source, even a trip through the
         * same editor invalidates it. F2 cannot launder source/editor/source. */
        gboolean reversed = overlay.return_started && overlay.return_source_seen && widget == overlay.text;
        if (overlay.return_started && widget == overlay.target->widget) overlay.return_source_seen = TRUE;
        emit_editor_focus(widget);
        if (reversed || !widget || (widget != overlay.target->widget && widget != overlay.text
            && !glossary_contains(overlay.glossary, widget))) {
            tl_overlay_hide();
            return;
        }
    }
    tl_overlay_sync();
    if (active() && tl_focused() != overlay.target && (paused() || tl_focused())) tl_overlay_hide();
}
static void active_changed(GObject *object, GParamSpec *property, gpointer data) {
    (void)object; (void)property; (void)data;
    tl_overlay_control_cancel();
    if (!active()) {
        if (glossary_handoff) glossary_handoff->failed = TRUE;
        if (edit_handoff) edit_handoff->failed = TRUE;
        if ((edit_handoff || overlay.private_editor) && overlay.target) emit_editor_focus(NULL);
        tl_overlay_hide();
    }
}
static gboolean expired(gpointer data) {
    (void)data;
    if (!overlay.popover || tl_now() > overlay.expires
        || (paused() && !pause_current(overlay.target, overlay.authority))) {
        overlay.expiry_timer = 0;
        tl_overlay_hide();
        return G_SOURCE_REMOVE;
    }
    glossary_sync();
    return G_SOURCE_CONTINUE;
}
void tl_overlay_hide(void) {
    if (glossary_handoff) glossary_handoff->failed = TRUE;
    tl_overlay_control_cancel();
    glossary_close(FALSE, TRUE);
    if (edit_handoff) edit_handoff->failed = TRUE;
    /* Clear state before destroy: source destruction and GTK callbacks are reentrant. */
    if (overlay.focused) emit_boolean("focus", FALSE);
    if (overlay.popover) emit_boolean("visible", FALSE);
    if (overlay.composing) emit_boolean("composition", FALSE);
    GtkWidget *popover = overlay.popover, *top = overlay.top;
    gulong focus_handler = overlay.focus_handler, active_handler = overlay.active_handler;
    GtkCssProvider *css = overlay.css, *geometry_css = overlay.geometry_css;
    JsonObject *view = overlay.view, *authority = overlay.authority, *editor_token = overlay.editor_token;
    char *generation = overlay.generation;
    if (overlay.expiry_timer) g_source_remove(overlay.expiry_timer);
    clear_language_menu(&overlay.source_languages);
    clear_language_menu(&overlay.target_languages);
    clear_translation_menu();
    if (overlay.glossary_draft_token) json_object_unref(overlay.glossary_draft_token);
    if (overlay.glossary_draft_buffer) g_object_unref(overlay.glossary_draft_buffer);
    memset(&overlay, 0, sizeof overlay);
    if (top) {
        if (focus_handler) g_signal_handler_disconnect(top, focus_handler);
        if (active_handler) g_signal_handler_disconnect(top, active_handler);
        g_object_remove_weak_pointer(G_OBJECT(top), (gpointer *)&overlay.top);
    }
    if (popover) { gtk_widget_destroy(popover); g_object_unref(popover); }
    if (css) g_object_unref(css);
    if (geometry_css) g_object_unref(geometry_css);
    if (view) json_object_unref(view);
    if (editor_token) json_object_unref(editor_token);
    if (authority) json_object_unref(authority);
    g_free(generation);
}
void tl_overlay_forget(Target *target) {
    if (overlay.target == target) tl_overlay_hide();
}
static char *candidate(void) {
    GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text));
    GtkTextIter start, end;
    gtk_text_buffer_get_bounds(buffer, &start, &end);
    return gtk_text_buffer_get_text(buffer, &start, &end, TRUE);
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
static gboolean begin_edit(GtkWidget *widget, GdkEventButton *event, gpointer data) {
    (void)data;
    static guint trace_count;
    if (g_getenv("TYPELAY_TRACE") && trace_count < 96) {
        trace_count++;
        g_printerr("TypeLay GTK beginEdit revision=%" G_GINT64_FORMAT
            " left=%d editor=%d editable=%d pending=%d remainingMs=%" G_GINT64_FORMAT "\n",
            overlay.revision, event->button == 1,
            gtk_text_view_get_editable(GTK_TEXT_VIEW(widget)),
            boolean(overlay.view, "editable"), overlay.pending_edit, overlay.expires - tl_now());
    }
    if (widget != overlay.text || event->button != 1
        || gtk_text_view_get_editable(GTK_TEXT_VIEW(widget))) return FALSE;
    if (paused() || !boolean(overlay.view, "editable") || overlay.pending_edit
        || !tl_configuration_current(overlay.generation)
        || !tl_view_context_matches(overlay.target, overlay.view)
        || !boolean(tl_object(overlay.view, "viewContext"), "canEdit")) return TRUE;
    gint x, y;
    GtkTextIter position;
    gtk_text_view_window_to_buffer_coords(GTK_TEXT_VIEW(widget), GTK_TEXT_WINDOW_WIDGET,
        (gint)event->x, (gint)event->y, &x, &y);
    gtk_text_view_get_iter_at_location(GTK_TEXT_VIEW(widget), &position, x, y);
    overlay.edit_offset = gtk_text_iter_get_offset(&position);
    overlay.pending_edit = TRUE;
    emit("beginEdit", NULL);
    return TRUE;
}
static void translation_action_clicked(const char *operation, JsonObject *view) {
    if (!active() || !gtk_widget_get_visible(overlay.menu) || !translation_menu_current()) return;
    gboolean glossary = strcmp(operation, "glossary") == 0;
    if (glossary && (!glossary_origin_ready() || overlay.view != view)) return;
    JsonObject *state = translation_actions(view);
    gboolean alternative = strcmp(operation, "alternative") == 0;
    gboolean inherit = strcmp(operation, "style-inherit") == 0;
    if (glossary ? !boolean(state, "glossaryReleased") || !boolean(state, "canOpenGlossary")
        || overlay.pending_glossary || overlay.glossary
        : alternative ? !boolean(state, "alternativeReleased") || !boolean(state, "canRequestAlternative")
        : !boolean(state, "styleReleased") || !boolean(state, inherit ? "canInheritStyle" : "canChangeStyle")) return;
    /* The bridge resolves this exact transport record to its original complete
     * session token. Never emit() here: it reads the latest overlay identity. */
    JsonObject *event = json_object_new(), *token = json_object_ref(overlay.menu_token);
    JsonObject *context = json_object_ref(tl_object(view, "viewContext"));
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
    gtk_widget_set_visible(overlay.menu, FALSE);
    if (translation_render_current(view, context) && overlay.menu_token == token && active() && translation_menu_current())
        tl_send_event("ui", event);
    else json_object_unref(event);
    json_object_unref(context); json_object_unref(token);
}
static void button_clicked(GtkButton *button, gpointer data) {
    if (!overlay.popover || !gtk_widget_is_ancestor(GTK_WIDGET(button), overlay.popover)) return;
    const char *operation = data;
    if (strcmp(operation, "resume") == 0) {
        Target *target = overlay.target;
        gint64 revision = overlay.revision;
        if (GTK_WIDGET(button) != overlay.resume || !paused() || overlay.pending_resume) return;
        if (!active() || tl_now() > overlay.expires || !pause_current(target, overlay.authority)) {
            tl_overlay_hide(); return;
        }
        if (overlay.target != target || overlay.revision != revision || !paused() || tl_now() > overlay.expires) return;
        overlay.pending_resume = TRUE;
        gtk_widget_set_sensitive(overlay.resume, FALSE);
        emit("resume", NULL);
        return;
    }
    if (paused() || overlay.glossary || !tl_configuration_current(overlay.generation)) return;
    /* Source view/configuration changes replace this identity. The menu-only
       projection preserves it without changing the original token or revision. */
    JsonObject *clicked_view = json_object_ref(overlay.view);
    if (strcmp(operation, "menu") == 0) {
        gboolean current = TRUE;
        if (!gtk_widget_get_visible(overlay.menu)) {
            freeze_translation_menu(clicked_view);
            current = reset_language_selection(clicked_view) && render_translation_actions(clicked_view);
        }
        if (current && overlay.view == clicked_view) {
            if (gtk_widget_get_visible(overlay.menu)) close_language_menu(clicked_view);
            else {
                gboolean trigger_owned = language_handoff_current(clicked_view)
                    && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == overlay.language;
                gtk_widget_show(overlay.menu);
                /* Reopening a collapsed owned menu restores navigation, not
                 * source focus. The ordinary first open still leaves source alone. */
                if (trigger_owned && language_handoff_current(clicked_view)
                    && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == overlay.language
                    && overlay.source && gtk_widget_get_mapped(overlay.source))
                    gtk_widget_grab_focus(overlay.source);
            }
        }
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
        gtk_clipboard_set_text(gtk_widget_get_clipboard(overlay.text, GDK_SELECTION_CLIPBOARD), text, -1);
        g_free(text);
        if (overlay.view == clicked_view) gtk_widget_hide(overlay.menu);
    } else if (strcmp(operation, "languages") == 0) {
        const char *source = selected_language(overlay.source, &overlay.source_languages);
        const char *target = selected_language(overlay.destination, &overlay.target_languages);
        if (overlay.setting_languages || !overlay.source_languages.choices || !overlay.target_languages.choices || !source || !target) {
            json_object_unref(clicked_view); return;
        }
        JsonObject *value = json_object_new();
        json_object_set_string_member(value, "source", source);
        json_object_set_string_member(value, "target", target);
        JsonNode *node = json_node_new(JSON_NODE_OBJECT);
        json_node_take_object(node, value);
        emit(operation, node);
        if (overlay.view == clicked_view) close_language_menu(clicked_view);
    } else {
        gtk_widget_hide(overlay.menu);
        if (overlay.view == clicked_view && tl_configuration_current(overlay.generation))
            emit(operation, NULL);
    }
    json_object_unref(clicked_view);
}
static GtkWidget *button(GtkWidget *box, const char *label, const char *operation) {
    GtkWidget *widget = gtk_button_new_with_label(label);
    gtk_button_set_relief(GTK_BUTTON(widget), GTK_RELIEF_NONE);
    gtk_widget_set_can_focus(widget, FALSE);
    gtk_button_set_focus_on_click(GTK_BUTTON(widget), FALSE);
    g_signal_connect(widget, "clicked", G_CALLBACK(button_clicked), (gpointer)operation);
    gtk_box_pack_start(GTK_BOX(box), widget, FALSE, FALSE, 0);
    return widget;
}
static void style(GtkWidget *widget, gpointer provider) {
    gtk_style_context_add_provider(gtk_widget_get_style_context(widget), GTK_STYLE_PROVIDER(provider),
        GTK_STYLE_PROVIDER_PRIORITY_APPLICATION + 1);
    if (GTK_IS_CONTAINER(widget)) gtk_container_forall(GTK_CONTAINER(widget), style, provider);
}
static void theme(const char *choice, gboolean stale_preview) {
    gboolean dark = FALSE;
    char *name = NULL;
    g_object_get(gtk_settings_get_default(), "gtk-application-prefer-dark-theme", &dark, "gtk-theme-name", &name, NULL);
    if (strcmp(choice, "system") != 0) dark = strcmp(choice, "dark") == 0;
    else if (name) { char *lower = g_ascii_strdown(name, -1); dark |= strstr(lower, "dark") != NULL; g_free(lower); }
    g_free(name);
    const char *bg = dark ? "#292a2e" : "#ffffff", *fg = dark ? "#f4f4f6" : "#222327";
    const char *border = dark ? "#42434b" : "#d0d1d7", *muted = dark ? "#b6b7c0" : "#666771";
    const char *line = dark ? "#42434b" : "#e5e5e9", *hover = dark ? "#393a41" : "#ededf0";
    const char *accent = dark ? "#86b0ff" : "#2365d8";
    char *css = g_strdup_printf(
        "#typelay-overlay { background: %s; color: %s; border: 1px solid %s; border-radius: 14px; padding: 0; }"
        "#typelay-overlay #typelay-header { padding: 8px 12px 2px; color: %s; font-size: 12px; }"
        "#typelay-overlay #typelay-footer { margin: 0 14px 4px; padding: 6px 0 4px; border-top: 1px solid %s; color: %s; font-size: 11px; }"
        "#typelay-overlay button { padding: 2px 3px; min-height: 22px; min-width: 0; border: 1px solid transparent; border-radius: 6px; background: transparent; color: %s; box-shadow: none; text-shadow: none; font-weight: normal; }"
        "#typelay-overlay button:hover { background: %s; color: %s; }"
        "#typelay-overlay #typelay-actions button { font-size: 11px; }"
        "#typelay-overlay #typelay-candidate, #typelay-overlay #typelay-candidate text { background: %s; color: %s; font-size: 15px; }"
        "#typelay-overlay #typelay-candidate { border: 1px solid transparent; border-radius: 7px; outline: none; box-shadow: none; }"
        /* GTK3 paints the text node's frame; the main node keeps layout padding. */
        "#typelay-overlay #typelay-candidate > text { border: 1px solid transparent; border-radius: 7px; }"
        "#typelay-overlay #typelay-candidate:focus > text { border-color: %s; }"
        "#typelay-overlay #typelay-header .pending { color: %s; }",
        bg, fg, border, muted, line, muted, muted, hover, fg, bg, stale_preview ? muted : fg, accent,
        dark ? "#f2bb8d" : "#99511f");
    gtk_css_provider_load_from_data(overlay.css, css, -1, NULL);
    g_free(css);
}
/* GTK owns interpolation and frame scheduling. Geometry CSS is separate from
 * theme refreshes; unchanged render/renew requests must not restart a resize. */
static void body_height(gint height, gboolean animate) {
    gboolean enabled = FALSE;
    GtkSettings *settings = gtk_widget_get_settings(overlay.scroll);
    g_object_get(settings, "gtk-enable-animations", &enabled, NULL);
    gint duration = animate && enabled && gtk_widget_get_mapped(overlay.scroll) ? 280 : 0;
    if (height == overlay.body_height && duration == overlay.body_duration) return;
    overlay.body_height = height;
    overlay.body_duration = duration;
    char *css = g_strdup_printf(
        "#typelay-body { min-height: %dpx; transition: min-height %dms ease-out; }", height, duration);
    gtk_css_provider_load_from_data(overlay.geometry_css, css, -1, NULL);
    g_free(css);
}
static void create(Target *target) {
    overlay.target = target;
    overlay.top = gtk_widget_get_toplevel(target->widget);
    g_object_add_weak_pointer(G_OBJECT(overlay.top), (gpointer *)&overlay.top);
    overlay.popover = gtk_popover_new(target->widget);
    g_object_ref_sink(overlay.popover);
    g_object_set_data(G_OBJECT(overlay.popover), "typelay-overlay", GINT_TO_POINTER(1));
    gtk_widget_set_name(overlay.popover, "typelay-overlay");
    gtk_popover_set_modal(GTK_POPOVER(overlay.popover), FALSE);
    gtk_popover_set_position(GTK_POPOVER(overlay.popover), GTK_POS_TOP);
    overlay.card = gtk_box_new(GTK_ORIENTATION_VERTICAL, 0);
    gtk_container_add(GTK_CONTAINER(overlay.popover), overlay.card);
    overlay.header = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
    gtk_widget_set_name(overlay.header, "typelay-header");
    gtk_box_pack_start(GTK_BOX(overlay.card), overlay.header, FALSE, FALSE, 0);
    overlay.language = button(overlay.header, "", "menu");
    gtk_widget_set_name(overlay.language, "typelay-menu-trigger");
    /* No focus-on-click: source acceptance must not be stolen on first open.
     * A live language menu explicitly hands focus here before it is hidden. */
    gtk_widget_set_can_focus(overlay.language, TRUE);
    overlay.status = gtk_label_new("");
    gtk_label_set_line_wrap(GTK_LABEL(overlay.status), TRUE);
    gtk_label_set_line_wrap_mode(GTK_LABEL(overlay.status), PANGO_WRAP_WORD_CHAR);
    gtk_label_set_max_width_chars(GTK_LABEL(overlay.status), 24);
    gtk_label_set_xalign(GTK_LABEL(overlay.status), 1);
    gtk_box_pack_start(GTK_BOX(overlay.header), overlay.status, TRUE, TRUE, 0);
    overlay.actions = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 2);
    gtk_widget_set_name(overlay.actions, "typelay-actions");
    gtk_box_pack_start(GTK_BOX(overlay.header), overlay.actions, FALSE, FALSE, 0);
    button(overlay.actions, "重译", "regenerate");
    overlay.expand = button(overlay.actions, "展开", "expand");
    overlay.resume = button(overlay.header, "恢复", "resume");
    gtk_widget_set_name(overlay.resume, "typelay-resume");
    gtk_buildable_set_name(GTK_BUILDABLE(overlay.resume), "typelay-resume");
    overlay.scroll = gtk_scrolled_window_new(NULL, NULL);
    gtk_widget_set_name(overlay.scroll, "typelay-body");
    overlay.geometry_css = gtk_css_provider_new();
    gtk_style_context_add_provider(gtk_widget_get_style_context(overlay.scroll),
        GTK_STYLE_PROVIDER(overlay.geometry_css), GTK_STYLE_PROVIDER_PRIORITY_APPLICATION + 1);
    gtk_scrolled_window_set_propagate_natural_height(GTK_SCROLLED_WINDOW(overlay.scroll), FALSE);
    gtk_widget_set_margin_start(overlay.scroll, 8);
    gtk_widget_set_margin_end(overlay.scroll, 8);
    gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(overlay.scroll), GTK_POLICY_NEVER, GTK_POLICY_AUTOMATIC);
    overlay.text = gtk_text_view_new();
    gtk_widget_set_name(overlay.text, "typelay-candidate");
    atk_object_set_name(gtk_widget_get_accessible(overlay.text), "TypeLay 译文候选");
    gtk_buildable_set_name(GTK_BUILDABLE(overlay.text), "typelay-candidate");
    gtk_text_view_set_wrap_mode(GTK_TEXT_VIEW(overlay.text), GTK_WRAP_WORD_CHAR);
    gtk_text_view_set_left_margin(GTK_TEXT_VIEW(overlay.text), 6);
    gtk_text_view_set_right_margin(GTK_TEXT_VIEW(overlay.text), 6);
    gtk_text_view_set_top_margin(GTK_TEXT_VIEW(overlay.text), 12);
    gtk_text_view_set_bottom_margin(GTK_TEXT_VIEW(overlay.text), 12);
    gtk_text_view_set_pixels_below_lines(GTK_TEXT_VIEW(overlay.text), 5);
    gtk_text_view_set_accepts_tab(GTK_TEXT_VIEW(overlay.text), FALSE);
    gtk_text_view_set_editable(GTK_TEXT_VIEW(overlay.text), FALSE);
    gtk_text_view_set_cursor_visible(GTK_TEXT_VIEW(overlay.text), FALSE);
    gtk_widget_add_events(overlay.text, GDK_BUTTON_PRESS_MASK);
    g_signal_connect(overlay.text, "button-press-event", G_CALLBACK(begin_edit), NULL);
    g_signal_connect(overlay.text, "preedit-changed", G_CALLBACK(preedit), NULL);
    GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text));
    g_signal_connect(buffer, "changed", G_CALLBACK(draft_changed), NULL);
    g_signal_connect(buffer, "insert-text", G_CALLBACK(limit_draft), NULL);
    gtk_container_add(GTK_CONTAINER(overlay.scroll), overlay.text);
    gtk_box_pack_start(GTK_BOX(overlay.card), overlay.scroll, TRUE, TRUE, 0);
    overlay.footer = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
    gtk_widget_set_name(overlay.footer, "typelay-footer");
    overlay.hint = gtk_label_new("");
    gtk_label_set_xalign(GTK_LABEL(overlay.hint), 0);
    gtk_label_set_line_wrap(GTK_LABEL(overlay.hint), TRUE);
    gtk_label_set_line_wrap_mode(GTK_LABEL(overlay.hint), PANGO_WRAP_WORD_CHAR);
    gtk_box_pack_start(GTK_BOX(overlay.footer), overlay.hint, TRUE, TRUE, 0);
    overlay.exit = button(overlay.footer, "", "escape");
    gtk_box_pack_start(GTK_BOX(overlay.card), overlay.footer, FALSE, FALSE, 0);
    overlay.menu = gtk_box_new(GTK_ORIENTATION_VERTICAL, 6);
    gtk_widget_set_name(overlay.menu, "typelay-language-menu");
    gtk_container_set_border_width(GTK_CONTAINER(overlay.menu), 12);
    overlay.source = language_button();
    overlay.destination = language_button();
    gtk_widget_set_name(overlay.source, "typelay-source-language");
    gtk_widget_set_name(overlay.destination, "typelay-target-language");
    gtk_box_pack_start(GTK_BOX(overlay.menu), gtk_label_new("源语言"), FALSE, FALSE, 0);
    gtk_box_pack_start(GTK_BOX(overlay.menu), overlay.source, FALSE, FALSE, 0);
    gtk_box_pack_start(GTK_BOX(overlay.menu), gtk_label_new("目标语言"), FALSE, FALSE, 0);
    gtk_box_pack_start(GTK_BOX(overlay.menu), overlay.destination, FALSE, FALSE, 0);
    overlay.apply_languages = button(overlay.menu, "应用语言方向", "languages");
    gtk_widget_set_name(overlay.apply_languages, "typelay-apply-languages");
    overlay.style_label = gtk_label_new("当前应用风格");
    gtk_box_pack_start(GTK_BOX(overlay.menu), overlay.style_label, FALSE, FALSE, 0);
    overlay.style_inherit = button(overlay.menu, "继承", "style-inherit");
    overlay.style_natural = button(overlay.menu, "自然", "style-natural");
    overlay.style_formal = button(overlay.menu, "正式", "style-formal");
    overlay.alternative = button(overlay.menu, "换种说法", "alternative");
    overlay.glossary_action = button(overlay.menu, "加入术语表", "glossary");
    gtk_widget_set_name(overlay.glossary_action, "typelay-glossary-add");
    gtk_widget_set_name(overlay.style_inherit, "typelay-style-inherit");
    gtk_widget_set_name(overlay.style_natural, "typelay-style-natural");
    gtk_widget_set_name(overlay.style_formal, "typelay-style-formal");
    gtk_widget_set_name(overlay.alternative, "typelay-alternative");
    overlay.action_feedback = gtk_label_new("");
    gtk_widget_set_name(overlay.action_feedback, "typelay-action-feedback");
    gtk_label_set_xalign(GTK_LABEL(overlay.action_feedback), 0);
    gtk_label_set_line_wrap(GTK_LABEL(overlay.action_feedback), TRUE);
    gtk_label_set_line_wrap_mode(GTK_LABEL(overlay.action_feedback), PANGO_WRAP_WORD_CHAR);
    gtk_label_set_max_width_chars(GTK_LABEL(overlay.action_feedback), 40);
    gtk_style_context_add_class(gtk_widget_get_style_context(overlay.action_feedback), "dim-label");
    gtk_box_pack_start(GTK_BOX(overlay.menu), overlay.action_feedback, FALSE, FALSE, 0);
    button(overlay.menu, "复制译文", "copy");
    button(overlay.menu, "排除此应用", "exclude");
    button(overlay.menu, "设置…", "settings");
    gtk_box_pack_end(GTK_BOX(overlay.card), overlay.menu, FALSE, FALSE, 0);
    overlay.css = gtk_css_provider_new();
    style(overlay.popover, overlay.css);
    gtk_widget_show_all(overlay.card);
    gtk_widget_hide(overlay.menu);
    overlay.focus_handler = g_signal_connect_after(overlay.top, "set-focus", G_CALLBACK(focus_changed), NULL);
    overlay.active_handler = g_signal_connect(overlay.top, "notify::is-active", G_CALLBACK(active_changed), NULL);
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
    if (current && g_strcmp0(tl_string(value, "state"), "Paused") != 0 && !*text_or_empty(value, "status")) {
        char *feedback = g_strdup(text_or_empty(translation_actions(value), "feedback"));
        GtkWidget *status = g_object_ref(overlay.status);
        gtk_label_set_text(GTK_LABEL(status), feedback);
        current = translation_render_current(view, context) && overlay.status == status;
        g_object_unref(status); g_free(feedback);
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
        if (!buttons[index]) continue;
        GtkWidget *popup = GTK_WIDGET(gtk_menu_button_get_popover(GTK_MENU_BUTTON(buttons[index])));
        if ((focus == buttons[index] && gtk_widget_get_mapped(buttons[index]))
            || (popup && gtk_widget_get_mapped(popup)
                && (focus == popup || gtk_widget_is_ancestor(focus, popup)))) return TRUE;
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
        current = old_token && new_token && tl_integer(old_token, "revision", -1) == overlay.revision
            && tl_integer(new_token, "revision", -1) == tl_integer(data, "revision", -2);
        if (current) {
            /* tl_copy retains nested objects. Copy each edited level so neither
             * stored authority/view tokens nor the incoming request are changed. */
            JsonObject *old_context = tl_copy(tl_object(old_view, "viewContext"));
            JsonObject *new_context = tl_copy(tl_object(new_view, "viewContext"));
            old_token = tl_copy(old_token); new_token = tl_copy(new_token);
            json_object_remove_member(old_token, "revision");
            json_object_remove_member(new_token, "revision");
            json_object_set_object_member(old_context, "token", old_token);
            json_object_set_object_member(new_context, "token", new_token);
            json_object_set_object_member(old_view, "viewContext", old_context);
            json_object_set_object_member(new_view, "viewContext", new_context);
            const char *menu_fields[] = {"sourceLanguage", "targetLanguage", "sourceLanguageLabel",
                "targetLanguageLabel", "languageChoices"};
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
    if (!target || !boolean(presentation, "visible") || !tl_metadata_matches(target, authority)
        || until < now || until - now > 1000
        || (is_paused ? !pause_current(target, authority)
            : !retained_source_form && !retained_editor && !tl_ime_clear(target)
                && !retained_language_preview(target, data))) return FALSE;
    GtkWidget *top = gtk_widget_get_toplevel(target->widget);
    if (!GTK_IS_WINDOW(top) || !gtk_window_is_active(GTK_WINDOW(top))) return FALSE;
    if (tl_focused() != target && !(!is_paused && overlay.target == target && overlay.focused)) return FALSE;
    JsonObject *geometry = tl_geometry(target);
    if (!geometry) return FALSE;
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
    /* This transport hint is visual only. A new body, surface, generation or
       anchor lands directly, after all existing view/authority checks above. */
    GtkWidget *old_popover = overlay.popover, *old_editor = overlay.text, *old_scroll = overlay.scroll;
    gint old_width = -1;
    GtkAllocation old_parent, current_parent;
    gtk_widget_get_allocation(top, &old_parent);
    if (overlay.card) gtk_widget_get_size_request(overlay.card, &old_width, NULL);
    gboolean same_view = overlay.target == target && overlay.top == top && overlay.view
        && g_strcmp0(overlay.generation, generation) == 0 && json_object_equal(overlay.view, value);
    gboolean expansion = same_view && tl_integer(data, "visualExpansionFrom", -1) == overlay.revision
        && overlay.revision < G_MAXINT64
        && tl_integer(data, "revision", -1) == overlay.revision + 1;
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
    if (overlay.target != target || overlay.top != top) { tl_overlay_hide(); create(target); }
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
    gtk_widget_set_can_focus(overlay.text, editing);
    if (!render_languages(value) || !render_translation_actions(overlay.view)) {
        json_object_unref(geometry); return FALSE;
    }
    const char *status = text_or_empty(value, "status");
    if (!*status) status = text_or_empty(translation_actions(value), "feedback");
    gtk_label_set_text(GTK_LABEL(overlay.status), is_paused ? "已暂停" : status);
    JsonObject *rendered = json_object_ref(overlay.view);
    tl_overlay_acceptance();
    gboolean retained = overlay.view == rendered && overlay.target == target && !target->dead
        && tl_configuration_current(generation) && g_strcmp0(overlay.generation, generation) == 0
        && overlay.popover && overlay.text && overlay.status;
    json_object_unref(rendered);
    if (!retained) { json_object_unref(geometry); return FALSE; }
    GtkStyleContext *context = gtk_widget_get_style_context(overlay.status);
    gtk_style_context_remove_class(context, "ready"); gtk_style_context_remove_class(context, "pending");
    if (g_strcmp0(tl_string(presentation, "dot"), "Green") == 0) gtk_style_context_add_class(context, "ready");
    if (g_strcmp0(tl_string(presentation, "dot"), "Yellow") == 0) gtk_style_context_add_class(context, "pending");
    const char *state = tl_string(value, "state");
    gboolean editable_preview = boolean(value, "editable") && tl_view_context_matches(target, value)
        && boolean(tl_object(value, "viewContext"), "canEdit")
        && (g_strcmp0(state, "ValidTranslation") == 0 || g_strcmp0(state, "EditedTranslationPending") == 0);
    gtk_widget_set_tooltip_text(overlay.text, editable_preview ? "点击译文编辑" : NULL);
    gboolean stale_preview = g_strcmp0(state, "Translating") == 0
        && *text_or_empty(value, "text") && !boolean(presentation, "editorOwnsText");
    theme(text_or_empty(data, "theme"), stale_preview);
    gboolean capsule = is_paused || g_strcmp0(tl_string(value, "state"), "IdleCapsule") == 0;
    gint width = capsule ? 160 : CLAMP(gtk_widget_get_allocated_width(target->widget), 390, 520);
    width = MIN(width, MAX(160, gtk_widget_get_allocated_width(top) - 48));
    gtk_widget_set_size_request(overlay.card, width, -1);
    char *draft = editing && boolean(presentation, "editorOwnsText")
        && overlay.target == target && overlay.popover == old_popover && overlay.text == old_editor
        && tl_configuration_current(generation) && tl_view_context_matches(target, overlay.view)
        ? candidate() : NULL;
    PangoLayout *layout = gtk_widget_create_pango_layout(overlay.text, draft ? draft : text_or_empty(value, "text"));
    g_free(draft);
    pango_layout_set_width(layout, MAX(1, width - 30) * PANGO_SCALE);
    pango_layout_set_wrap(layout, PANGO_WRAP_WORD_CHAR);
    gint height;
    pango_layout_get_pixel_size(layout, NULL, &height); g_object_unref(layout);
    height = CLAMP(height + 24, 54, overlay.expanded ? 360 : 160);
    gtk_widget_set_visible(overlay.scroll, !capsule);
    gtk_widget_set_visible(overlay.footer, !capsule);
    gtk_widget_set_visible(overlay.actions, !capsule);
    gtk_widget_set_visible(overlay.status, is_paused || !capsule);
    gtk_widget_set_visible(overlay.language, !is_paused);
    gtk_widget_set_visible(overlay.resume, is_paused);
    gtk_widget_set_sensitive(overlay.resume, !overlay.pending_resume);
    gtk_widget_set_opacity(overlay.popover, is_paused ? 0.78 : 1.0);
    if (is_paused) gtk_widget_set_visible(overlay.menu, FALSE);
    gint x, y;
    gtk_widget_translate_coordinates(target->widget, top, 0, 0, &x, &y);
    JsonObject *bounds = tl_object(geometry, "bounds");
    GdkRectangle anchor = {
        .x = MAX(0, (gint)tl_integer(bounds, "x", 0) - x + (gint)tl_integer(bounds, "width", 1) - width / 2),
        .y = (gint)tl_integer(bounds, "y", 0) - y,
        .width = 1, .height = (gint)tl_integer(bounds, "height", 1)
    };
    gtk_widget_get_allocation(top, &current_parent);
    GdkRectangle previous_anchor;
    gboolean same_surface = overlay.popover == old_popover && overlay.text == old_editor
        && overlay.scroll == old_scroll && gtk_widget_get_mapped(overlay.scroll);
    gboolean same_anchor = gtk_popover_get_pointing_to(GTK_POPOVER(overlay.popover), &previous_anchor)
        && gdk_rectangle_equal(&anchor, &previous_anchor);
    body_height(height, !capsule && (editable_preview || editing)
        && same_view && same_surface && same_anchor && old_width == width
        && gdk_rectangle_equal(&old_parent, &current_parent)
        && (expansion || (height == overlay.body_height && overlay.body_duration > 0)));
    gtk_popover_set_pointing_to(GTK_POPOVER(overlay.popover), &anchor);
    json_object_unref(geometry);
    gtk_widget_show(overlay.popover);
    if (!overlay.expiry_timer) overlay.expiry_timer = g_timeout_add(100, expired, NULL);
    tl_overlay_sync();
    return TRUE;
}
gboolean tl_overlay_private_return(Target *target, JsonObject *proof, gboolean starting) {
    JsonObject *token = tl_object(proof, "token"), *editor = tl_object(proof, "editorToken");
    JsonObject *current = tl_object(tl_object(overlay.view, "viewContext"), "token");
    gboolean valid = target && !target->dead && overlay.target == target && active()
        && overlay.private_editor && overlay.editor_token && token && editor && current
        && json_object_equal(editor, overlay.editor_token) && json_object_equal(token, current)
        && tl_view_context_matches(target, overlay.view)
        && g_strcmp0(tl_string(overlay.view, "state"), "ReturningToSource") == 0
        && tl_integer(token, "revision", 0) > tl_integer(editor, "revision", 0)
        && (starting ? !overlay.return_started && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == overlay.text
                     : overlay.return_started && overlay.return_source_seen);
    if (valid && starting) overlay.return_started = TRUE;
    return valid;
}

static gboolean handoff_source_current(Target *target, JsonObject *proof) {
    JsonObject *token = tl_object(proof, "token");
    JsonObject *current = tl_object(tl_object(overlay.view, "viewContext"), "token");
    return target && !target->dead && overlay.target == target && active()
        && token && current && json_object_equal(token, current)
        && tl_focused() == target
        && (gint64)target->focus_revision == tl_integer(proof, "nativeFocusRevision", -1)
        && tl_integer(proof, "contextGeneration", 0) > 0
        && (gint64)target->im_state.context_generation == tl_integer(proof, "contextGeneration", -1)
        && (gint64)target->input_revision == tl_integer(overlay.authority, "inputRevision", -1)
        && (gint64)target->content_revision == tl_integer(overlay.authority, "contentRevision", -1);
}
JsonObject *tl_overlay_command(const char *operation, Target *target, JsonObject *data) {
    gboolean visible = FALSE, handoff = FALSE;
    if (strcmp(operation, "overlay-hide") != 0 && !tl_configuration_current(tl_string(data, "generation")))
        goto finished;
    if (strcmp(operation, "overlay-render") == 0) {
        visible = render(target, data);
        if (!visible && overlay.target == target
            && tl_configuration_current(tl_string(data, "generation"))
            && g_strcmp0(overlay.generation, tl_string(data, "generation")) == 0
            && overlay.revision <= tl_integer(data, "revision", -1)) tl_overlay_hide();
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
                body_height(overlay.body_height, overlay.body_duration > 0);
            }
            else { if (geometry) json_object_unref(geometry); tl_overlay_hide(); }
        } else tl_overlay_hide();
    } else if (strcmp(operation, "overlay-glossary-open") == 0) glossary_open(target, data);
    else if (strcmp(operation, "overlay-glossary-result") == 0) glossary_result(target, data);
    else if (strcmp(operation, "overlay-glossary-close") == 0) {
        GlossaryForm *form = overlay.glossary;
        if (form && overlay.target == target && form->revision == tl_integer(data, "revision", -1)
            && g_strcmp0(form->generation, tl_string(data, "generation")) == 0
            && g_strcmp0(form->id, tl_string(data, "formId")) == 0) glossary_close(FALSE, FALSE);
    } else if (strcmp(operation, "overlay-hide") == 0) tl_overlay_hide();
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
            JsonObject *proof = tl_object(data, "practiceHandoff");
            if (json_object_has_member(data, "practiceHandoff")
                && (!proof || edit_handoff || !handoff_source_current(target, proof))) goto finished;
            GtkWidget *editor = g_object_ref(overlay.text);
            GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(editor));
            GtkTextIter position;
            gtk_text_buffer_get_iter_at_offset(buffer, &position, MIN(overlay.edit_offset, gtk_text_buffer_get_char_count(buffer)));
            gtk_text_buffer_place_cursor(buffer, &position);
            if (overlay.text == editor && overlay.target == target
                && overlay.revision == tl_integer(data, "revision", -1)
                && tl_configuration_current(tl_string(data, "generation"))
                && tl_view_context_matches(target, overlay.view)
                && (!proof || handoff_source_current(target, proof))) {
                EditHandoff guard = {target->widget, editor, overlay.top, FALSE};
                guint64 input_revision = target->input_revision, content_revision = target->content_revision;
                JsonObject *view = json_object_ref(overlay.view);
                if (proof) edit_handoff = &guard;
                gtk_widget_grab_focus(editor);
                if (proof) {
                    handoff = !guard.failed && overlay.view == view && overlay.target == target
                        && !target->dead && overlay.text == editor && overlay.top == guard.top
                        && overlay.revision == tl_integer(data, "revision", -1)
                        && g_strcmp0(overlay.generation, tl_string(data, "generation")) == 0
                        && tl_configuration_current(overlay.generation) && active()
                        /* Focus-only callbacks advance focus_revision, but any
                         * input/context binding invalidation advances input_revision. */
                        && target->input_revision == input_revision
                        && target->content_revision == content_revision
                        && (gint64)target->im_state.context_generation == tl_integer(proof, "contextGeneration", -1)
                        && gtk_window_get_focus(GTK_WINDOW(overlay.top)) == editor;
                    edit_handoff = NULL;
                    overlay.private_editor = handoff;
                    if (handoff) {
                        if (overlay.editor_token) json_object_unref(overlay.editor_token);
                        overlay.editor_token = tl_copy(tl_object(proof, "token"));
                    }
                }
                json_object_unref(view);
            }
            g_object_unref(editor);
        }
        overlay.pending_edit = FALSE;
        tl_overlay_sync();
    } else if (strcmp(operation, "overlay-resume") == 0 && overlay.target == target && active() && !paused()
        && tl_configuration_current(tl_string(data, "generation"))
        && g_strcmp0(overlay.generation, tl_string(data, "generation")) == 0
        && overlay.revision == tl_integer(data, "revision", -1)
        && tl_view_context_matches(target, overlay.view)
        && boolean(tl_object(overlay.view, "viewContext"), "canEdit")) {
        gtk_label_set_text(GTK_LABEL(overlay.status), "无法返回原输入框，请点击译文继续编辑");
        if (tl_focused() == target || overlay.focused) {
            gtk_text_view_set_editable(GTK_TEXT_VIEW(overlay.text), TRUE);
            gtk_text_view_set_cursor_visible(GTK_TEXT_VIEW(overlay.text), TRUE);
            gtk_widget_set_can_focus(overlay.text, TRUE);
            gtk_widget_grab_focus(overlay.text);
            tl_overlay_sync();
        }
    }
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
    if (strcmp(operation, "overlay-edit") == 0 && data && json_object_has_member(data, "practiceHandoff"))
        json_object_set_boolean_member(reply, "handoff", handoff);
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
