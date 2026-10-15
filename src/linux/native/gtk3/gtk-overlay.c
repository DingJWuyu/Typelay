#include "gtk-overlay.h"
#include <string.h>

/* One source-bound popover per application, never another toplevel or an input grab.
 * This is deliberately in the GTK thread: niri does not publish tiled-window origins.
 * GTK owns the parent surface, clipping, scaling and movement of its own popover. */
static struct {
    Target *target;
    GtkWidget *popover, *top, *card, *header, *language, *status, *scroll, *text;
    GtkWidget *footer, *hint, *actions, *expand, *menu, *source, *destination, *resume, *exit;
    GtkCssProvider *css, *geometry_css;
    JsonObject *view, *authority, *editor_token;
    gulong focus_handler, active_handler;
    gboolean setting_text, composing, focused, expanded, pending_edit, pending_resume, private_editor;
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

static gboolean boolean(JsonObject *object, const char *key) {
    JsonNode *node = object ? json_object_get_member(object, key) : NULL;
    return node && JSON_NODE_HOLDS_VALUE(node) && json_node_get_value_type(node) == G_TYPE_BOOLEAN
        && json_node_get_boolean(node);
}
static const char *text_or_empty(JsonObject *object, const char *key) {
    const char *text = tl_string(object, key);
    return text ? text : "";
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
    return target && !target->dead && overlay.target == target && overlay.popover && overlay.text
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
static gboolean paused(void) {
    return g_strcmp0(tl_string(overlay.view, "state"), "Paused") == 0;
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
void tl_overlay_sync(void) {
    gboolean focused = active() && overlay.popover && gtk_widget_get_visible(overlay.popover)
        && tl_overlay_contains(gtk_window_get_focus(GTK_WINDOW(overlay.top)));
    if (focused != overlay.focused) {
        overlay.focused = focused;
        emit_boolean("focus", focused);
    }
}
static void emit_editor_focus(GtkWidget *widget) {
    const char *family = !active() || !widget ? "Other"
        : widget == overlay.target->widget ? "Source"
        : widget == overlay.text ? "Overlay" : "Other";
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
        if (reversed || !widget || (widget != overlay.target->widget && widget != overlay.text)) {
            tl_overlay_hide();
            return;
        }
    }
    tl_overlay_sync();
    if (active() && tl_focused() != overlay.target && (paused() || tl_focused())) tl_overlay_hide();
}
static void active_changed(GObject *object, GParamSpec *property, gpointer data) {
    (void)object; (void)property; (void)data;
    if (!active()) {
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
    return G_SOURCE_CONTINUE;
}
void tl_overlay_hide(void) {
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
    if (paused() || !tl_configuration_current(overlay.generation)) return;
    /* Every render/configuration replaces this view identity. Never relabel an
       old click with a newer transport revision after a widget callback. */
    JsonObject *clicked_view = json_object_ref(overlay.view);
    if (strcmp(operation, "menu") == 0) {
        gtk_widget_set_visible(overlay.menu, !gtk_widget_get_visible(overlay.menu));
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
        JsonObject *value = json_object_new();
        json_object_set_string_member(value, "source", gtk_combo_box_get_active_id(GTK_COMBO_BOX(overlay.source)));
        json_object_set_string_member(value, "target", gtk_combo_box_get_active_id(GTK_COMBO_BOX(overlay.destination)));
        JsonNode *node = json_node_new(JSON_NODE_OBJECT);
        json_node_take_object(node, value);
        emit(operation, node);
        if (overlay.view == clicked_view) gtk_widget_hide(overlay.menu);
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
static void theme(const char *choice) {
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
        bg, fg, border, muted, line, muted, muted, hover, fg, bg, fg, accent,
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
    gtk_container_set_border_width(GTK_CONTAINER(overlay.menu), 12);
    overlay.source = gtk_combo_box_text_new();
    overlay.destination = gtk_combo_box_text_new();
    gtk_combo_box_text_append(GTK_COMBO_BOX_TEXT(overlay.source), "auto", "自动识别");
    gtk_combo_box_text_append(GTK_COMBO_BOX_TEXT(overlay.source), "zh", "中文");
    gtk_combo_box_text_append(GTK_COMBO_BOX_TEXT(overlay.source), "en", "英语");
    gtk_combo_box_text_append(GTK_COMBO_BOX_TEXT(overlay.destination), "zh", "中文");
    gtk_combo_box_text_append(GTK_COMBO_BOX_TEXT(overlay.destination), "en", "英语");
    gtk_box_pack_start(GTK_BOX(overlay.menu), gtk_label_new("源语言"), FALSE, FALSE, 0);
    gtk_box_pack_start(GTK_BOX(overlay.menu), overlay.source, FALSE, FALSE, 0);
    gtk_box_pack_start(GTK_BOX(overlay.menu), gtk_label_new("目标语言"), FALSE, FALSE, 0);
    gtk_box_pack_start(GTK_BOX(overlay.menu), overlay.destination, FALSE, FALSE, 0);
    button(overlay.menu, "应用语言方向", "languages");
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
static const char *language_name(const char *code) {
    if (g_strcmp0(code, "zh") == 0) return "中文";
    if (g_strcmp0(code, "en") == 0) return "英语";
    return "自动";
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
    if (!target || !boolean(presentation, "visible") || !tl_metadata_matches(target, authority)
        || until < now || until - now > 1000
        || (is_paused ? !pause_current(target, authority) : !retained_editor && !tl_ime_clear(target))) return FALSE;
    GtkWidget *top = gtk_widget_get_toplevel(target->widget);
    if (!GTK_IS_WINDOW(top) || !gtk_window_is_active(GTK_WINDOW(top))) return FALSE;
    if (tl_focused() != target && !(!is_paused && overlay.target == target && overlay.focused)) return FALSE;
    JsonObject *geometry = tl_geometry(target);
    if (!geometry) return FALSE;
    if (!tl_configuration_current(generation)) { json_object_unref(geometry); return FALSE; }
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
    if (is_paused || !(boolean(presentation, "editorOwnsText") && gtk_text_view_get_editable(GTK_TEXT_VIEW(overlay.text)))) {
        overlay.setting_text = TRUE;
        gtk_text_buffer_set_text(gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text)), is_paused ? "" : text_or_empty(value, "text"), -1);
        overlay.setting_text = FALSE;
    }
    gtk_text_view_set_editable(GTK_TEXT_VIEW(overlay.text), editing);
    gtk_text_view_set_cursor_visible(GTK_TEXT_VIEW(overlay.text), editing);
    gtk_widget_set_can_focus(overlay.text, editing);
    const char *source = text_or_empty(value, "sourceLanguage"), *destination = text_or_empty(value, "targetLanguage");
    char *languages = g_strdup_printf("%s → %s", language_name(source), language_name(destination));
    gtk_button_set_label(GTK_BUTTON(overlay.language), languages); g_free(languages);
    gtk_combo_box_set_active_id(GTK_COMBO_BOX(overlay.source), source);
    gtk_combo_box_set_active_id(GTK_COMBO_BOX(overlay.destination), destination);
    gtk_label_set_text(GTK_LABEL(overlay.status), is_paused ? "已暂停" : text_or_empty(value, "status"));
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
    theme(text_or_empty(data, "theme"));
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
    if (strcmp(operation, "overlay-edit") == 0 && data && json_object_has_member(data, "practiceHandoff"))
        json_object_set_boolean_member(reply, "handoff", handoff);
    return reply;
}
gboolean tl_overlay_key(guint keyval, GdkModifierType state) {
    if (!overlay.popover) return FALSE;
    tl_overlay_sync();
    gboolean accept = keyval == tl_session_keyval(TRUE), exit = keyval == tl_session_keyval(FALSE);
    if ((!accept && !exit) || !overlay.focused || overlay.composing || paused()
        || !tl_configuration_current(overlay.generation) || !tl_view_context_matches(overlay.target, overlay.view)
        || (state & (GDK_SHIFT_MASK | GDK_CONTROL_MASK | GDK_MOD1_MASK | GDK_MOD3_MASK | GDK_MOD4_MASK | GDK_MOD5_MASK | GDK_SUPER_MASK | GDK_HYPER_MASK | GDK_META_MASK))) return FALSE;
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
    if (consumed) emit(exit ? "escape" : "return", NULL);
    json_object_unref(view);
    g_object_unref(editor);
    g_free(generation);
    return consumed;
}
