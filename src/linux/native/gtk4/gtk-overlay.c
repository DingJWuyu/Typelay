#include "gtk-overlay.h"
#include <math.h>
#include <string.h>

typedef struct {
    Target *target;
    GtkWidget *top, *popover, *scroll, *editor;
    GtkTextBuffer *buffer;
    JsonObject *view;
    char *generation;
    GdkRectangle anchor;
    GtkAllocation parent;
    gint width, from, to;
    gint64 revision, started;
    guint tick;
} HeightTransition;

/* One source-bound popover per application, never another toplevel or an input grab.
 * This is deliberately in the GTK thread: niri does not publish tiled-window origins.
 * GTK owns the parent surface, clipping, scaling and movement of its own popover. */
static struct {
    Target *target;
    GtkWidget *popover, *top, *card, *header, *language, *status, *scroll, *text;
    GtkWidget *footer, *hint, *actions, *expand, *menu, *source, *destination, *resume, *exit;
    GtkCssProvider *css;
    GdkDisplay *display;
    JsonObject *view, *authority;
    gulong focus_handler, active_handler;
    gboolean setting_text, composing, focused, expanded, pending_edit, pending_resume;
    char *generation;
    gint edit_offset;
    gint64 revision, expires;
    guint expiry_timer;
    HeightTransition *height_transition;
} overlay;

static void cancel_height_transition(void) {
    HeightTransition *transition = overlay.height_transition;
    overlay.height_transition = NULL;
    if (transition) gtk_widget_remove_tick_callback(transition->scroll, transition->tick);
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
gboolean tl_overlay_can_return(Target *target) {
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
static void focus_changed(GObject *object, GParamSpec *property, gpointer data) {
    (void)object; (void)property; (void)data;
    tl_overlay_sync();
    if (active() && tl_focused() != overlay.target && (paused() || tl_focused())) tl_overlay_hide();
}
static void active_changed(GObject *object, GParamSpec *property, gpointer data) {
    (void)object; (void)property; (void)data;
    if (!active()) tl_overlay_hide();
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
    cancel_height_transition();
    /* Clear state before destroy: source destruction and GTK callbacks are reentrant. */
    if (overlay.focused) emit_boolean("focus", FALSE);
    if (overlay.popover) emit_boolean("visible", FALSE);
    if (overlay.composing) emit_boolean("composition", FALSE);
    GtkWidget *popover = overlay.popover, *top = overlay.top;
    gulong focus_handler = overlay.focus_handler, active_handler = overlay.active_handler;
    GtkCssProvider *css = overlay.css;
    GdkDisplay *display = overlay.display;
    JsonObject *view = overlay.view, *authority = overlay.authority;
    char *generation = overlay.generation;
    if (overlay.expiry_timer) g_source_remove(overlay.expiry_timer);
    memset(&overlay, 0, sizeof overlay);
    if (top) {
        if (focus_handler) g_signal_handler_disconnect(top, focus_handler);
        if (active_handler) g_signal_handler_disconnect(top, active_handler);
        g_object_remove_weak_pointer(G_OBJECT(top), (gpointer *)&overlay.top);
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
static const char *selected_language(GtkWidget *widget, gboolean source) {
    static const char *source_codes[] = { "auto", "zh", "en" };
    static const char *target_codes[] = { "zh", "en" };
    guint selected = gtk_drop_down_get_selected(GTK_DROP_DOWN(widget));
    if (source) return selected < G_N_ELEMENTS(source_codes) ? source_codes[selected] : "auto";
    return selected < G_N_ELEMENTS(target_codes) ? target_codes[selected] : "en";
}
static void select_language(GtkWidget *widget, const char *code, gboolean source) {
    guint index = g_strcmp0(code, "zh") == 0 ? 0 : 1;
    if (source) index = g_strcmp0(code, "zh") == 0 ? 1 : g_strcmp0(code, "en") == 0 ? 2 : 0;
    gtk_drop_down_set_selected(GTK_DROP_DOWN(widget), index);
}
static gboolean key_event(GtkEventControllerLegacy *controller, GdkEvent *event, gpointer data) {
    (void)controller; (void)data;
    return tl_dispatch_key(event);
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
        gdk_clipboard_set_text(gtk_widget_get_clipboard(overlay.text), text);
        g_free(text);
        if (overlay.view == clicked_view) gtk_widget_set_visible(overlay.menu, FALSE);
    } else if (strcmp(operation, "languages") == 0) {
        JsonObject *value = json_object_new();
        json_object_set_string_member(value, "source", selected_language(overlay.source, TRUE));
        json_object_set_string_member(value, "target", selected_language(overlay.destination, FALSE));
        JsonNode *node = json_node_new(JSON_NODE_OBJECT);
        json_node_take_object(node, value);
        emit(operation, node);
        if (overlay.view == clicked_view) gtk_widget_set_visible(overlay.menu, FALSE);
    } else {
        gtk_widget_set_visible(overlay.menu, FALSE);
        if (overlay.view == clicked_view && tl_configuration_current(overlay.generation))
            emit(operation, NULL);
    }
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
        "#typelay-overlay > contents { background: %s; color: %s; border: 1px solid %s; border-radius: 14px; padding: 0; }"
        "#typelay-overlay #typelay-header { padding: 8px 12px 2px; color: %s; font-size: 12px; }"
        "#typelay-overlay #typelay-footer { margin: 0 14px 4px; padding: 6px 0 4px; border-top: 1px solid %s; color: %s; font-size: 11px; }"
        "#typelay-overlay button { padding: 2px 3px; min-height: 22px; min-width: 0; border: 1px solid transparent; border-radius: 6px; background: transparent; color: %s; box-shadow: none; text-shadow: none; font-weight: normal; }"
        "#typelay-overlay button:hover { background: %s; color: %s; }"
        "#typelay-overlay #typelay-actions button { font-size: 11px; }"
        "#typelay-overlay #typelay-candidate, #typelay-overlay #typelay-candidate text { background: %s; color: %s; font-size: 15px; }"
        "#typelay-overlay #typelay-candidate { border: 1px solid transparent; border-radius: 7px; outline: none; box-shadow: none; }"
        "#typelay-overlay #typelay-candidate:focus { border-color: %s; }"
        "#typelay-overlay #typelay-header .pending { color: %s; }",
        bg, fg, border, muted, line, muted, muted, hover, fg, bg, fg, accent,
        dark ? "#f2bb8d" : "#99511f");
#if GTK_CHECK_VERSION(4, 12, 0)
    gtk_css_provider_load_from_string(overlay.css, css);
#else
    gtk_css_provider_load_from_data(overlay.css, css, -1);
#endif
    g_free(css);
}
static gboolean animations_enabled(GtkWidget *widget) {
    gboolean enabled = FALSE;
    GtkSettings *settings = gtk_widget_get_settings(widget);
    g_object_get(settings, "gtk-enable-animations", &enabled, NULL);
    if (g_object_class_find_property(G_OBJECT_GET_CLASS(settings), "gtk-interface-reduced-motion")) {
        /* GtkReducedMotion: NO_PREFERENCE = 0, REDUCE = 1 (since 4.22). */
        gint reduced = 0;
        g_object_get(settings, "gtk-interface-reduced-motion", &reduced, NULL);
        enabled = enabled && reduced == 0;
    }
    return enabled;
}
static void free_height_transition(gpointer data) {
    HeightTransition *transition = data;
    if (overlay.height_transition == transition) overlay.height_transition = NULL;
    g_object_unref(transition->buffer);
    json_object_unref(transition->view);
    g_free(transition->generation);
    g_free(transition);
}
static gboolean height_identity_matches(const HeightTransition *transition) {
    return overlay.target == transition->target && overlay.top == transition->top
        && overlay.popover == transition->popover && overlay.scroll == transition->scroll
        && overlay.text == transition->editor
        && g_strcmp0(overlay.generation, transition->generation) == 0
        && tl_configuration_current(transition->generation) && active()
        && tl_now() <= overlay.expires && gtk_widget_get_mapped(overlay.scroll)
        && gtk_widget_get_visible(overlay.popover)
        && gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text)) == transition->buffer;
}
static gboolean height_geometry_matches(const HeightTransition *transition) {
    GtkAllocation parent;
    GdkRectangle anchor;
    gint width = -1;
    gtk_widget_get_allocation(overlay.top, &parent);
    gtk_widget_get_size_request(overlay.card, &width, NULL);
    return width == transition->width && gdk_rectangle_equal(&parent, &transition->parent)
        && gtk_popover_get_pointing_to(GTK_POPOVER(overlay.popover), &anchor)
        && gdk_rectangle_equal(&anchor, &transition->anchor);
}
static gboolean advance_height(GtkWidget *widget, GdkFrameClock *clock, gpointer data) {
    HeightTransition *transition = data;
    if (overlay.height_transition != transition) return G_SOURCE_REMOVE;
    if (overlay.view != transition->view || overlay.revision != transition->revision
        || !height_identity_matches(transition)) {
        overlay.height_transition = NULL;
        return G_SOURCE_REMOVE;
    }
    gint64 now = gdk_frame_clock_get_frame_time(clock);
    if (!transition->started) transition->started = now;
    /* A still-current body lands immediately if its layout changed. Invalid
       identities above must never receive this final setter. Match WPF's
       existing height motion otherwise: 280 ms, ease-out cubic. */
    double progress = height_geometry_matches(transition) && animations_enabled(widget)
        ? CLAMP((double)(now - transition->started) / 280000.0, 0.0, 1.0) : 1.0;
    double remaining = 1.0 - progress;
    double eased = 1.0 - remaining * remaining * remaining;
    gint height = (gint)lround(transition->from + (transition->to - transition->from) * eased);
    gboolean more = progress < 1.0;
    if (!more) overlay.height_transition = NULL;
    /* This setter may synchronously hide or replace the view. Do not access
       transition afterwards: cancellation can release the callback data. */
    gtk_widget_set_size_request(widget, -1, height);
    return more;
}
static void body_height(gint height, gboolean animate) {
    HeightTransition *transition = overlay.height_transition;
    if (animate && transition && transition->to == height
        && height_identity_matches(transition) && height_geometry_matches(transition)
        && animations_enabled(overlay.scroll)) {
        /* An unchanged render adopts its new identity without restarting time. */
        json_object_unref(transition->view);
        transition->view = json_object_ref(overlay.view);
        transition->revision = overlay.revision;
        return;
    }
    cancel_height_transition();
    gint from = gtk_widget_get_height(overlay.scroll);
    if (!animate || !animations_enabled(overlay.scroll) || from <= 0 || from == height) {
        gtk_widget_set_size_request(overlay.scroll, -1, height);
        return;
    }
    transition = g_new0(HeightTransition, 1);
    transition->target = overlay.target;
    transition->top = overlay.top;
    transition->popover = overlay.popover;
    transition->scroll = overlay.scroll;
    transition->editor = overlay.text;
    transition->buffer = g_object_ref(gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text)));
    transition->view = json_object_ref(overlay.view);
    transition->generation = g_strdup(overlay.generation);
    transition->revision = overlay.revision;
    transition->from = from;
    transition->to = height;
    gtk_widget_get_allocation(overlay.top, &transition->parent);
    gtk_widget_get_size_request(overlay.card, &transition->width, NULL);
    gtk_popover_get_pointing_to(GTK_POPOVER(overlay.popover), &transition->anchor);
    overlay.height_transition = transition;
    transition->tick = gtk_widget_add_tick_callback(overlay.scroll, advance_height, transition, free_height_transition);
    gtk_widget_set_size_request(overlay.scroll, -1, from);
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
    overlay.header = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
    gtk_widget_set_name(overlay.header, "typelay-header");
    gtk_box_append(GTK_BOX(overlay.card), overlay.header);
    overlay.language = button(overlay.header, "", "menu");
    overlay.status = gtk_label_new("");
    gtk_label_set_wrap(GTK_LABEL(overlay.status), TRUE);
    gtk_label_set_wrap_mode(GTK_LABEL(overlay.status), PANGO_WRAP_WORD_CHAR);
    gtk_label_set_max_width_chars(GTK_LABEL(overlay.status), 24);
    gtk_widget_set_hexpand(overlay.status, TRUE);
    gtk_widget_set_halign(overlay.status, GTK_ALIGN_END);
    gtk_box_append(GTK_BOX(overlay.header), overlay.status);
    overlay.actions = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 2);
    gtk_widget_set_name(overlay.actions, "typelay-actions");
    gtk_box_append(GTK_BOX(overlay.header), overlay.actions);
    button(overlay.actions, "重译", "regenerate");
    overlay.expand = button(overlay.actions, "展开", "expand");
    overlay.resume = button(overlay.header, "恢复", "resume");
    gtk_widget_set_name(overlay.resume, "typelay-resume");
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
    gtk_text_view_set_bottom_margin(GTK_TEXT_VIEW(overlay.text), 12);
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
    g_signal_connect(keys, "event", G_CALLBACK(key_event), NULL);
    g_object_set_data_full(G_OBJECT(overlay.text), "typelay-session-controller", g_object_ref(keys), g_object_unref);
    gtk_widget_add_controller(overlay.text, keys);
    g_signal_connect(overlay.text, "preedit-changed", G_CALLBACK(preedit), NULL);
    GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text));
    g_signal_connect(buffer, "changed", G_CALLBACK(draft_changed), NULL);
    g_signal_connect(buffer, "insert-text", G_CALLBACK(limit_draft), NULL);
    gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(overlay.scroll), overlay.text);
    gtk_widget_set_vexpand(overlay.scroll, TRUE);
    gtk_box_append(GTK_BOX(overlay.card), overlay.scroll);
    overlay.footer = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
    gtk_widget_set_name(overlay.footer, "typelay-footer");
    overlay.hint = gtk_label_new("");
    gtk_label_set_xalign(GTK_LABEL(overlay.hint), 0);
    gtk_label_set_wrap(GTK_LABEL(overlay.hint), TRUE);
    gtk_label_set_wrap_mode(GTK_LABEL(overlay.hint), PANGO_WRAP_WORD_CHAR);
    gtk_widget_set_hexpand(overlay.hint, TRUE);
    gtk_box_append(GTK_BOX(overlay.footer), overlay.hint);
    overlay.exit = button(overlay.footer, "", "escape");
    gtk_box_append(GTK_BOX(overlay.card), overlay.footer);
    overlay.menu = gtk_box_new(GTK_ORIENTATION_VERTICAL, 6);
    gtk_widget_set_margin_start(overlay.menu, 12);
    gtk_widget_set_margin_end(overlay.menu, 12);
    gtk_widget_set_margin_top(overlay.menu, 12);
    gtk_widget_set_margin_bottom(overlay.menu, 12);
    const char *source_languages[] = { "自动识别", "中文", "英语", NULL };
    const char *target_languages[] = { "中文", "英语", NULL };
    overlay.source = gtk_drop_down_new_from_strings(source_languages);
    overlay.destination = gtk_drop_down_new_from_strings(target_languages);
    gtk_box_append(GTK_BOX(overlay.menu), gtk_label_new("源语言"));
    gtk_box_append(GTK_BOX(overlay.menu), overlay.source);
    gtk_box_append(GTK_BOX(overlay.menu), gtk_label_new("目标语言"));
    gtk_box_append(GTK_BOX(overlay.menu), overlay.destination);
    button(overlay.menu, "应用语言方向", "languages");
    button(overlay.menu, "复制译文", "copy");
    button(overlay.menu, "排除此应用", "exclude");
    button(overlay.menu, "设置…", "settings");
    gtk_box_append(GTK_BOX(overlay.card), overlay.menu);
    overlay.css = gtk_css_provider_new();
    overlay.display = g_object_ref(gtk_widget_get_display(overlay.popover));
    gtk_style_context_add_provider_for_display(overlay.display, GTK_STYLE_PROVIDER(overlay.css),
        GTK_STYLE_PROVIDER_PRIORITY_APPLICATION + 1);
    gtk_widget_set_visible(overlay.menu, FALSE);
    overlay.focus_handler = g_signal_connect_after(overlay.top, "notify::focus-widget", G_CALLBACK(focus_changed), NULL);
    overlay.active_handler = g_signal_connect(overlay.top, "notify::is-active", G_CALLBACK(active_changed), NULL);
}
static const char *language_name(const char *code) {
    if (g_strcmp0(code, "zh") == 0) return "中文";
    if (g_strcmp0(code, "en") == 0) return "英语";
    return "自动";
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
    gint64 request_revision = tl_integer(data, "revision", 0);
    if (!target || !boolean(presentation, "visible"))
        return pause_render_result(is_paused, "target-or-view", request_revision, FALSE);
    if (!tl_metadata_matches(target, authority))
        return pause_render_result(is_paused, "metadata", request_revision, FALSE);
    if (until < now || until - now > 1000)
        return pause_render_result(is_paused, "expiry", request_revision, FALSE);
    if (is_paused ? !pause_current(target, authority) : !retained_editor && !tl_ime_clear(target))
        return pause_render_result(is_paused, "source-observation", request_revision, FALSE);
    GtkWidget *top = GTK_WIDGET(gtk_widget_get_root(target->widget));
    if (!GTK_IS_WINDOW(top) || !gtk_window_is_active(GTK_WINDOW(top)))
        return pause_render_result(is_paused, "window", request_revision, FALSE);
    if (tl_focused() != target && !(!is_paused && overlay.target == target && overlay.focused))
        return pause_render_result(is_paused, "focus", request_revision, FALSE);
    JsonObject *geometry = tl_geometry(target);
    if (!geometry) return pause_render_result(is_paused, "geometry", request_revision, FALSE);
    if (!tl_configuration_current(generation)) { json_object_unref(geometry); return FALSE; }
    GtkWidget *old_popover = overlay.popover, *old_editor = overlay.text, *old_scroll = overlay.scroll;
    GtkTextBuffer *old_buffer = old_editor ? gtk_text_view_get_buffer(GTK_TEXT_VIEW(old_editor)) : NULL;
    gint old_width = -1;
    GtkAllocation old_parent, current_parent;
    gtk_widget_get_allocation(top, &old_parent);
    if (overlay.card) gtk_widget_get_size_request(overlay.card, &old_width, NULL);
    gboolean same_view = overlay.target == target && overlay.top == top && overlay.view
        && g_strcmp0(overlay.generation, generation) == 0 && json_object_equal(overlay.view, value);
    gboolean expansion = same_view && tl_integer(data, "visualExpansionFrom", -1) == overlay.revision
        && overlay.revision < G_MAXINT64 && tl_integer(data, "revision", -1) == overlay.revision + 1;
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
    gtk_widget_set_focusable(overlay.text, editing);
    JsonObject *focus_view = json_object_ref(overlay.view);
    gtk_widget_set_can_focus(overlay.scroll, editing);
    gboolean focus_retained = overlay.view == focus_view && overlay.target == target && !target->dead
        && tl_configuration_current(generation) && g_strcmp0(overlay.generation, generation) == 0
        && overlay.popover && overlay.text;
    json_object_unref(focus_view);
    if (!focus_retained) { json_object_unref(geometry); return FALSE; }
    const char *source = text_or_empty(value, "sourceLanguage"), *destination = text_or_empty(value, "targetLanguage");
    char *languages = g_strdup_printf("%s → %s", language_name(source), language_name(destination));
    gtk_button_set_label(GTK_BUTTON(overlay.language), languages); g_free(languages);
    select_language(overlay.source, source, TRUE);
    select_language(overlay.destination, destination, FALSE);
    gtk_label_set_text(GTK_LABEL(overlay.status), is_paused ? "已暂停" : text_or_empty(value, "status"));
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
    gtk_widget_set_tooltip_text(overlay.text, editable_preview ? "点击译文编辑" : NULL);
    theme(text_or_empty(data, "theme"));
    gboolean capsule = is_paused || g_strcmp0(tl_string(value, "state"), "IdleCapsule") == 0;
    gint width = capsule ? 160 : CLAMP(gtk_widget_get_width(target->widget), 390, 520);
    width = MIN(width, MAX(160, gtk_widget_get_width(top) - 48));
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
    graphene_point_t zero = GRAPHENE_POINT_INIT(0, 0), origin;
    if (!gtk_widget_compute_point(target->widget, top, &zero, &origin)) {
        json_object_unref(geometry);
        return pause_render_result(is_paused, "coordinates", request_revision, FALSE);
    }
    JsonObject *bounds = tl_object(geometry, "bounds");
    GdkRectangle anchor = {
        .x = MAX(0, (gint)tl_integer(bounds, "x", 0) - (gint)origin.x + (gint)tl_integer(bounds, "width", 1) - width / 2),
        .y = (gint)tl_integer(bounds, "y", 0) - (gint)origin.y,
        .width = 1, .height = (gint)tl_integer(bounds, "height", 1)
    };
    gtk_widget_get_allocation(top, &current_parent);
    GdkRectangle previous_anchor;
    gboolean same_surface = overlay.popover == old_popover && overlay.text == old_editor
        && overlay.scroll == old_scroll && gtk_widget_get_mapped(overlay.scroll)
        && gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text)) == old_buffer;
    gboolean same_anchor = gtk_popover_get_pointing_to(GTK_POPOVER(overlay.popover), &previous_anchor)
        && gdk_rectangle_equal(&anchor, &previous_anchor);
    JsonObject *sized_view = json_object_ref(overlay.view);
    body_height(height, !capsule && (editable_preview || editing)
        && same_view && same_surface && same_anchor && old_width == width
        && gdk_rectangle_equal(&old_parent, &current_parent)
        && (expansion || (overlay.height_transition && overlay.height_transition->to == height
            && height_identity_matches(overlay.height_transition)
            && height_geometry_matches(overlay.height_transition))));
    gboolean size_retained = overlay.view == sized_view && overlay.target == target && !target->dead
        && overlay.popover && overlay.text && tl_configuration_current(generation)
        && g_strcmp0(overlay.generation, generation) == 0;
    json_object_unref(sized_view);
    if (!size_retained) { json_object_unref(geometry); return FALSE; }
    gtk_popover_set_pointing_to(GTK_POPOVER(overlay.popover), &anchor);
    json_object_unref(geometry);
    gtk_popover_popup(GTK_POPOVER(overlay.popover));
    gtk_popover_present(GTK_POPOVER(overlay.popover));
    if (!overlay.expiry_timer) overlay.expiry_timer = g_timeout_add(100, expired, NULL);
    tl_overlay_sync();
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
    gboolean visible = FALSE;
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
    return reply;
}
gboolean tl_overlay_key(guint keyval, GdkModifierType state) {
    if (!overlay.popover) return FALSE;
    tl_overlay_sync();
    gboolean accept = keyval == tl_session_keyval(TRUE), exit = keyval == tl_session_keyval(FALSE);
    if ((!accept && !exit) || !overlay.focused || overlay.composing || paused()
        || !tl_configuration_current(overlay.generation) || !tl_view_context_matches(overlay.target, overlay.view)
        || (state & (GDK_SHIFT_MASK | GDK_CONTROL_MASK | GDK_ALT_MASK | GDK_SUPER_MASK | GDK_HYPER_MASK | GDK_META_MASK))) return FALSE;
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
