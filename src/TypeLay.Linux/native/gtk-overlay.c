#include "gtk-overlay.h"
#include <string.h>

/* One source-bound popover per application, never another toplevel or an input grab.
 * This is deliberately in the GTK thread: niri does not publish tiled-window origins.
 * GTK owns the parent surface, clipping, scaling and movement of its own popover. */
static struct {
    Target *target;
    GtkWidget *popover, *top, *card, *header, *language, *status, *scroll, *text;
    GtkWidget *footer, *hint, *expand, *menu, *source, *destination;
    GtkCssProvider *css;
    JsonObject *view;
    gulong focus_handler, active_handler;
    gboolean setting_text, composing, focused, expanded, pending_edit;
    guint held_key;
    gint edit_offset;
    gint64 revision, expires;
    guint expiry_timer;
} overlay;

static gboolean boolean(JsonObject *object, const char *key) {
    JsonNode *node = object ? json_object_get_member(object, key) : NULL;
    return node && JSON_NODE_HOLDS_VALUE(node) && json_node_get_value_type(node) == G_TYPE_BOOLEAN
        && json_node_get_boolean(node);
}
static const char *text_or_empty(JsonObject *object, const char *key) {
    const char *text = tl_string(object, key);
    return text ? text : "";
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
static void emit(const char *operation, JsonNode *value) {
    if (!overlay.target) { if (value) json_node_free(value); return; }
    JsonObject *event = json_object_new();
    json_object_set_string_member(event, "target", overlay.target->id);
    json_object_set_string_member(event, "operation", operation);
    json_object_set_int_member(event, "revision", overlay.revision);
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
static void focus_changed(GtkWindow *window, GtkWidget *widget, gpointer data) {
    (void)window; (void)widget; (void)data;
    tl_overlay_sync();
    if (active() && tl_focused() && tl_focused() != overlay.target) tl_overlay_hide();
}
static void active_changed(GObject *object, GParamSpec *property, gpointer data) {
    (void)object; (void)property; (void)data;
    if (!active()) tl_overlay_hide();
}
static gboolean expired(gpointer data) {
    (void)data;
    if (!overlay.popover || tl_now() > overlay.expires) {
        overlay.expiry_timer = 0;
        tl_overlay_hide();
        return G_SOURCE_REMOVE;
    }
    return G_SOURCE_CONTINUE;
}
void tl_overlay_hide(void) {
    /* Clear state before destroy: source destruction and GTK callbacks are reentrant. */
    if (overlay.focused) emit_boolean("focus", FALSE);
    if (overlay.popover) emit_boolean("visible", FALSE);
    if (overlay.composing) emit_boolean("composition", FALSE);
    GtkWidget *popover = overlay.popover, *top = overlay.top;
    gulong focus_handler = overlay.focus_handler, active_handler = overlay.active_handler;
    GtkCssProvider *css = overlay.css;
    JsonObject *view = overlay.view;
    if (overlay.expiry_timer) g_source_remove(overlay.expiry_timer);
    memset(&overlay, 0, sizeof overlay);
    if (top) {
        if (focus_handler) g_signal_handler_disconnect(top, focus_handler);
        if (active_handler) g_signal_handler_disconnect(top, active_handler);
        g_object_remove_weak_pointer(G_OBJECT(top), (gpointer *)&overlay.top);
    }
    if (popover) { gtk_widget_destroy(popover); g_object_unref(popover); }
    if (css) g_object_unref(css);
    if (view) json_object_unref(view);
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
    if (!overlay.setting_text && overlay.text && gtk_text_view_get_editable(GTK_TEXT_VIEW(overlay.text))) {
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
    (void)widget; (void)data;
    overlay.composing = text && *text;
    emit_boolean("composition", overlay.composing);
}
static gboolean begin_edit(GtkWidget *widget, GdkEventButton *event, gpointer data) {
    (void)data;
    if (event->button != 1 || gtk_text_view_get_editable(GTK_TEXT_VIEW(widget))) return FALSE;
    if (!boolean(overlay.view, "editable") || overlay.pending_edit) return TRUE;
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
    (void)button;
    const char *operation = data;
    if (strcmp(operation, "menu") == 0) {
        gtk_widget_set_visible(overlay.menu, !gtk_widget_get_visible(overlay.menu));
    } else if (strcmp(operation, "expand") == 0) {
        overlay.expanded = !overlay.expanded;
        gtk_button_set_label(GTK_BUTTON(overlay.expand), overlay.expanded ? "收起" : "展开");
        emit_boolean("expand", overlay.expanded);
    } else if (strcmp(operation, "copy") == 0) {
        char *text = candidate();
        gtk_clipboard_set_text(gtk_widget_get_clipboard(overlay.text, GDK_SELECTION_CLIPBOARD), text, -1);
        g_free(text);
        gtk_widget_hide(overlay.menu);
    } else if (strcmp(operation, "languages") == 0) {
        JsonObject *value = json_object_new();
        json_object_set_string_member(value, "source", gtk_combo_box_get_active_id(GTK_COMBO_BOX(overlay.source)));
        json_object_set_string_member(value, "target", gtk_combo_box_get_active_id(GTK_COMBO_BOX(overlay.destination)));
        JsonNode *node = json_node_new(JSON_NODE_OBJECT);
        json_node_take_object(node, value);
        emit(operation, node);
        gtk_widget_hide(overlay.menu);
    } else {
        gtk_widget_hide(overlay.menu);
        emit(operation, NULL);
    }
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
    const char *bg = dark ? "#252a32" : "#ffffff", *fg = dark ? "#eef1f6" : "#252a31";
    const char *border = dark ? "#48515e" : "#cdd3de", *muted = dark ? "#aeb7c4" : "#737b89";
    char *css = g_strdup_printf(
        "#typelay-overlay { background: %s; color: %s; border: 1px solid %s; border-radius: 14px; padding: 0; }"
        "#typelay-header, #typelay-footer { padding: 2px 10px; color: %s; font-size: 11px; }"
        "#typelay-header { border-bottom: 1px solid %s; } #typelay-footer { border-top: 1px solid %s; }"
        "#typelay-overlay button { padding: 2px 3px; min-height: 22px; min-width: 0; }"
        "#typelay-candidate, #typelay-candidate text { background: %s; color: %s; font-size: 14px; }"
        "#typelay-header .ready { color: %s; } #typelay-header .pending { color: %s; }",
        bg, fg, border, muted, border, border, bg, fg,
        dark ? "#67d49b" : "#19764a", dark ? "#edbd6d" : "#a86400");
    gtk_css_provider_load_from_data(overlay.css, css, -1, NULL);
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
    gtk_label_set_ellipsize(GTK_LABEL(overlay.status), PANGO_ELLIPSIZE_END);
    gtk_box_pack_end(GTK_BOX(overlay.header), overlay.status, FALSE, FALSE, 0);
    overlay.scroll = gtk_scrolled_window_new(NULL, NULL);
    gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(overlay.scroll), GTK_POLICY_NEVER, GTK_POLICY_AUTOMATIC);
    overlay.text = gtk_text_view_new();
    gtk_widget_set_name(overlay.text, "typelay-candidate");
    gtk_buildable_set_name(GTK_BUILDABLE(overlay.text), "typelay-candidate");
    gtk_text_view_set_wrap_mode(GTK_TEXT_VIEW(overlay.text), GTK_WRAP_WORD_CHAR);
    gtk_text_view_set_left_margin(GTK_TEXT_VIEW(overlay.text), 14);
    gtk_text_view_set_right_margin(GTK_TEXT_VIEW(overlay.text), 14);
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
    gtk_box_pack_start(GTK_BOX(overlay.footer), overlay.hint, TRUE, TRUE, 0);
    button(overlay.footer, "Esc 隐藏", "escape");
    button(overlay.footer, "重译", "regenerate");
    overlay.expand = button(overlay.footer, "展开", "expand");
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
    gboolean retained_editor = overlay.target == target && boolean(presentation, "editorOwnsText")
        && target && !target->dead
        && (gint64)target->content_revision == tl_integer(authority, "contentRevision", -1)
        && (gint64)target->input_revision == tl_integer(authority, "inputRevision", -1);
    if (!target || !boolean(presentation, "visible") || !tl_metadata_matches(target, authority)
        || until < now || until - now > 1000 || (!retained_editor && !tl_ime_clear(target))) return FALSE;
    GtkWidget *top = gtk_widget_get_toplevel(target->widget);
    if (!GTK_IS_WINDOW(top) || !gtk_window_is_active(GTK_WINDOW(top))) return FALSE;
    if (tl_focused() != target && !(overlay.target == target && overlay.focused)) return FALSE;
    JsonObject *geometry = tl_geometry(target);
    if (!geometry) return FALSE;
    if (overlay.target != target || overlay.top != top) { tl_overlay_hide(); create(target); }
    overlay.expires = until;
    overlay.revision = tl_integer(data, "revision", 0);
    if (overlay.view) json_object_unref(overlay.view);
    overlay.view = tl_copy(value);
    gboolean editing = g_strcmp0(tl_string(value, "state"), "EditingTranslation") == 0;
    if (!(boolean(presentation, "editorOwnsText") && gtk_text_view_get_editable(GTK_TEXT_VIEW(overlay.text)))) {
        overlay.setting_text = TRUE;
        gtk_text_buffer_set_text(gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text)), text_or_empty(value, "text"), -1);
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
    gtk_label_set_text(GTK_LABEL(overlay.status), text_or_empty(value, "status"));
    gtk_label_set_text(GTK_LABEL(overlay.hint), text_or_empty(presentation, "tabHint"));
    GtkStyleContext *context = gtk_widget_get_style_context(overlay.status);
    gtk_style_context_remove_class(context, "ready"); gtk_style_context_remove_class(context, "pending");
    if (g_strcmp0(tl_string(presentation, "dot"), "Green") == 0) gtk_style_context_add_class(context, "ready");
    if (g_strcmp0(tl_string(presentation, "dot"), "Yellow") == 0) gtk_style_context_add_class(context, "pending");
    theme(text_or_empty(data, "theme"));
    gboolean capsule = g_strcmp0(tl_string(value, "state"), "IdleCapsule") == 0;
    gint width = capsule ? 160 : CLAMP(gtk_widget_get_allocated_width(target->widget), 390, 520);
    width = MIN(width, MAX(160, gtk_widget_get_allocated_width(top) - 48));
    gtk_widget_set_size_request(overlay.card, width, -1);
    PangoLayout *layout = gtk_widget_create_pango_layout(overlay.text, text_or_empty(value, "text"));
    pango_layout_set_width(layout, MAX(1, width - 30) * PANGO_SCALE);
    pango_layout_set_wrap(layout, PANGO_WRAP_WORD_CHAR);
    gint height;
    pango_layout_get_pixel_size(layout, NULL, &height); g_object_unref(layout);
    height = CLAMP(height + 24, 54, overlay.expanded ? 360 : 160);
    gtk_widget_set_size_request(overlay.scroll, -1, height);
    gtk_widget_set_visible(overlay.scroll, !capsule);
    gtk_widget_set_visible(overlay.footer, !capsule);
    gtk_widget_set_visible(overlay.status, !capsule);
    gint x, y;
    gtk_widget_translate_coordinates(target->widget, top, 0, 0, &x, &y);
    JsonObject *bounds = tl_object(geometry, "bounds");
    GdkRectangle anchor = {
        .x = MAX(0, (gint)tl_integer(bounds, "x", 0) - x + (gint)tl_integer(bounds, "width", 1) - width / 2),
        .y = (gint)tl_integer(bounds, "y", 0) - y,
        .width = 1, .height = (gint)tl_integer(bounds, "height", 1)
    };
    gtk_popover_set_pointing_to(GTK_POPOVER(overlay.popover), &anchor);
    json_object_unref(geometry);
    gtk_widget_show(overlay.popover);
    if (!overlay.expiry_timer) overlay.expiry_timer = g_timeout_add(100, expired, NULL);
    tl_overlay_sync();
    return TRUE;
}
JsonObject *tl_overlay_command(const char *operation, Target *target, JsonObject *data) {
    gboolean visible = FALSE;
    if (strcmp(operation, "overlay-render") == 0) {
        visible = render(target, data);
        if (!visible) tl_overlay_hide();
    } else if (strcmp(operation, "overlay-renew") == 0) {
        gint64 until = tl_integer(data, "expiresAtMs", 0), now = tl_now();
        if (overlay.target == target && active() && until >= now && until - now <= 1000
            && (tl_focused() == target || overlay.focused) && tl_metadata_matches(target, tl_object(data, "authority"))) {
            JsonObject *geometry = tl_geometry(target);
            if (geometry) { overlay.expires = until; json_object_unref(geometry); }
            else tl_overlay_hide();
        } else tl_overlay_hide();
    } else if (strcmp(operation, "overlay-hide") == 0) tl_overlay_hide();
    else if (strcmp(operation, "overlay-edit") == 0) {
        if (overlay.target == target && active() && overlay.pending_edit && boolean(data, "allowed")
            && g_strcmp0(tl_string(overlay.view, "state"), "EditingTranslation") == 0) {
            GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(overlay.text));
            GtkTextIter position;
            gtk_text_buffer_get_iter_at_offset(buffer, &position, MIN(overlay.edit_offset, gtk_text_buffer_get_char_count(buffer)));
            gtk_text_buffer_place_cursor(buffer, &position);
            gtk_widget_grab_focus(overlay.text);
        }
        overlay.pending_edit = FALSE;
        tl_overlay_sync();
    } else if (strcmp(operation, "overlay-resume") == 0 && overlay.target == target && active()) {
        gtk_label_set_text(GTK_LABEL(overlay.status), "无法返回原输入框，请点击译文继续编辑");
        if (tl_focused() == target || overlay.focused) {
            gtk_text_view_set_editable(GTK_TEXT_VIEW(overlay.text), TRUE);
            gtk_text_view_set_cursor_visible(GTK_TEXT_VIEW(overlay.text), TRUE);
            gtk_widget_set_can_focus(overlay.text, TRUE);
            gtk_widget_grab_focus(overlay.text);
            tl_overlay_sync();
        }
    }
    JsonObject *reply = json_object_new();
    json_object_set_boolean_member(reply, "visible", visible || (overlay.popover && gtk_widget_get_visible(overlay.popover)));
    json_object_set_boolean_member(reply, "editing", overlay.text && gtk_text_view_get_editable(GTK_TEXT_VIEW(overlay.text)));
    return reply;
}
gboolean tl_overlay_event(GdkEvent *event) {
    if (!overlay.popover) return FALSE;
    tl_overlay_sync();
    if (event->type != GDK_KEY_PRESS && event->type != GDK_KEY_RELEASE) return FALSE;
    GdkEventKey *key = &event->key;
    if (overlay.held_key && key->hardware_keycode == overlay.held_key) {
        if (event->type == GDK_KEY_RELEASE) overlay.held_key = 0;
        return TRUE;
    }
    if (!overlay.focused || event->type != GDK_KEY_PRESS || key->send_event || overlay.composing
        || (key->state & (GDK_CONTROL_MASK | GDK_MOD1_MASK | GDK_SHIFT_MASK | GDK_SUPER_MASK))) return FALSE;
    /* The local GtkTextView is an IM client too. Empty preedit alone cannot
       authorize stealing Tab/Escape from a pending candidate or async commit. */
    if ((key->keyval == GDK_KEY_Tab || key->keyval == GDK_KEY_Escape) && !tl_widget_ime_clear(overlay.text))
        return FALSE;
    if (key->keyval == GDK_KEY_Tab && gtk_text_view_get_editable(GTK_TEXT_VIEW(overlay.text))) {
        overlay.held_key = key->hardware_keycode;
        emit("return", NULL);
        return TRUE;
    }
    if (key->keyval == GDK_KEY_Escape) {
        overlay.held_key = key->hardware_keycode;
        emit("escape", NULL);
        return TRUE;
    }
    return FALSE;
}
