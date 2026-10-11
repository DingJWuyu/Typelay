#define _GNU_SOURCE
#include "gtk-target.h"
#include <string.h>
#include <unistd.h>

static GHashTable *targets;
static GQuark target_quark;
static guint target_count;

static void changed(Target *target) {
    if (!target->mutating) tl_invalidate(target, TRUE, FALSE);
}
static void entry_changed(GtkEditable *editable, gpointer data) { (void)editable; changed(data); }
static void buffer_changed(GtkTextBuffer *buffer, gpointer data) { (void)buffer; changed(data); }
static void selection_changed(GObject *object, GParamSpec *property, gpointer data) {
    (void)object; (void)property;
    Target *target = data;
    if (!target->mutating) tl_invalidate(target, FALSE, FALSE);
}
static void mark_changed(GtkTextBuffer *buffer, GtkTextIter *where, GtkTextMark *mark, gpointer data) {
    (void)where;
    if (mark == gtk_text_buffer_get_insert(buffer) || mark == gtk_text_buffer_get_selection_bound(buffer))
        selection_changed(G_OBJECT(buffer), NULL, data);
}
static void preedit(GtkWidget *widget, const gchar *text, gpointer data) {
    (void)widget;
    Target *target = data;
    target->ime_known = TRUE;
    target->composing = text != NULL && *text != '\0';
    tl_invalidate(target, FALSE, FALSE);
}
static gboolean focused(GtkWidget *widget, GdkEventFocus *event, gpointer data) {
    (void)widget; (void)event;
    Target *target = data;
    tl_invalidate(target, FALSE, TRUE);
    return FALSE;
}
static void destroyed(GtkWidget *widget, gpointer data) {
    (void)widget;
    Target *target = data;
    target->dead = TRUE;
    target->widget = NULL;
    if (target->buffer) {
        g_signal_handlers_disconnect_by_data(target->buffer, target);
        g_clear_object(&target->buffer);
    }
    tl_forget(target);
    /* Retain a bounded identity tombstone: callbacks can destroy a widget reentrantly
       inside GTK insertion, so no callback may observe freed target state. */
}
static void attach_buffer(Target *target) {
    GObject *buffer = GTK_IS_ENTRY(target->widget)
        ? G_OBJECT(gtk_entry_get_buffer(GTK_ENTRY(target->widget)))
        : G_OBJECT(gtk_text_view_get_buffer(GTK_TEXT_VIEW(target->widget)));
    if (buffer == target->buffer) return;
    if (target->buffer) {
        g_signal_handlers_disconnect_by_data(target->buffer, target);
        g_object_unref(target->buffer);
    }
    target->buffer = g_object_ref(buffer);
    if (GTK_IS_TEXT_BUFFER(buffer)) {
        g_signal_connect(buffer, "changed", G_CALLBACK(buffer_changed), target);
        g_signal_connect(buffer, "mark-set", G_CALLBACK(mark_changed), target);
    }
    tl_invalidate(target, TRUE, FALSE);
}
static void buffer_replaced(GObject *object, GParamSpec *property, gpointer data) {
    (void)object; (void)property;
    Target *target = data;
    if (!target->dead) attach_buffer(target);
}
void tl_discover(GtkWidget *widget, gboolean before_input) {
    if (G_OBJECT_TYPE(widget) != GTK_TYPE_ENTRY && G_OBJECT_TYPE(widget) != GTK_TYPE_TEXT_VIEW) return;
    if (!target_quark) target_quark = g_quark_from_static_string("typelay-gtk3-target");
    if (g_object_get_qdata(G_OBJECT(widget), target_quark) || target_count >= 2048) return;
    if (!targets) targets = g_hash_table_new(g_str_hash, g_str_equal);
    Target *target = g_new0(Target, 1);
    target->widget = widget;
    target->id = g_uuid_string_random();
    target->content_revision = 1;
    target->input_revision = 1;
    target->focus_revision = 1;
    target->ime_known = before_input;
    g_object_set_qdata(G_OBJECT(widget), target_quark, target);
    g_hash_table_insert(targets, target->id, target);
    target_count++;
    g_signal_connect(widget, "destroy", G_CALLBACK(destroyed), target);
    g_signal_connect(widget, "focus-in-event", G_CALLBACK(focused), target);
    g_signal_connect(widget, "focus-out-event", G_CALLBACK(focused), target);
    g_signal_connect(widget, "preedit-changed", G_CALLBACK(preedit), target);
    g_signal_connect(widget, "notify::buffer", G_CALLBACK(buffer_replaced), target);
    if (GTK_IS_ENTRY(widget)) {
        g_signal_connect(widget, "changed", G_CALLBACK(entry_changed), target);
        g_signal_connect(widget, "notify::cursor-position", G_CALLBACK(selection_changed), target);
        g_signal_connect(widget, "notify::selection-bound", G_CALLBACK(selection_changed), target);
    }
    attach_buffer(target);
}
Target *tl_lookup(const char *id) {
    Target *target = targets && id ? g_hash_table_lookup(targets, id) : NULL;
    return target && !target->dead ? target : NULL;
}
Target *tl_focused(void) {
    GList *windows = gtk_window_list_toplevels();
    Target *result = NULL;
    for (GList *item = windows; item; item = item->next) {
        GtkWindow *window = GTK_WINDOW(item->data);
        if (!gtk_window_is_active(window)) continue;
        GtkWidget *widget = gtk_window_get_focus(window);
        if (widget && target_quark) result = g_object_get_qdata(G_OBJECT(widget), target_quark);
        if (result && !result->dead) break;
    }
    g_list_free(windows);
    return result && !result->dead ? result : NULL;
}
static gboolean supported(Target *target) {
    if (!target || target->dead || !gtk_widget_get_realized(target->widget)) return FALSE;
    if (GTK_IS_ENTRY(target->widget))
        return G_OBJECT_TYPE(target->buffer) == GTK_TYPE_ENTRY_BUFFER;
    return G_OBJECT_TYPE(target->buffer) == GTK_TYPE_TEXT_BUFFER
        && gtk_text_tag_table_get_size(gtk_text_buffer_get_tag_table(GTK_TEXT_BUFFER(target->buffer))) == 0;
}
static gboolean sensitive(Target *target) {
    if (GTK_IS_ENTRY(target->widget)) {
        GtkEntry *entry = GTK_ENTRY(target->widget);
        GtkInputPurpose purpose = gtk_entry_get_input_purpose(entry);
        return !gtk_entry_get_visibility(entry) || purpose == GTK_INPUT_PURPOSE_PASSWORD
            || purpose == GTK_INPUT_PURPOSE_PIN || (gtk_entry_get_input_hints(entry) & GTK_INPUT_HINT_PRIVATE) != 0;
    }
    GtkInputPurpose purpose = gtk_text_view_get_input_purpose(GTK_TEXT_VIEW(target->widget));
    return purpose == GTK_INPUT_PURPOSE_PASSWORD || purpose == GTK_INPUT_PURPOSE_PIN
        || (gtk_text_view_get_input_hints(GTK_TEXT_VIEW(target->widget)) & GTK_INPUT_HINT_PRIVATE) != 0;
}
static gboolean editable(Target *target) {
    return GTK_IS_ENTRY(target->widget) ? gtk_editable_get_editable(GTK_EDITABLE(target->widget))
        : gtk_text_view_get_editable(GTK_TEXT_VIEW(target->widget));
}
static void find_titlebar(GtkWidget *widget, gpointer data) {
    GtkWidget **titlebar = data;
    if (gtk_widget_get_visible(widget) && gtk_style_context_has_class(gtk_widget_get_style_context(widget), "titlebar"))
        *titlebar = widget;
}
static JsonObject *rectangle(gint x, gint y, gint width, gint height) {
    JsonObject *value = json_object_new();
    json_object_set_int_member(value, "x", x);
    json_object_set_int_member(value, "y", y);
    json_object_set_int_member(value, "width", width);
    json_object_set_int_member(value, "height", height);
    return value;
}
static JsonObject *geometry(Target *target) {
    GtkWidget *widget = target->widget;
    GtkWidget *top = gtk_widget_get_toplevel(widget);
    if (!GTK_IS_WINDOW(top) || !gtk_widget_get_mapped(widget)) return NULL;
    GtkWidget *content = gtk_bin_get_child(GTK_BIN(top)), *titlebar = NULL;
    if (!content) return NULL;
    gtk_container_forall(GTK_CONTAINER(top), find_titlebar, &titlebar);
    gint border = (gint)gtk_container_get_border_width(GTK_CONTAINER(top));
    GtkAllocation body, header;
    gtk_widget_get_allocation(content, &body);
    gint left, upper, frame_width;
    if (titlebar) {
        gtk_widget_get_allocation(titlebar, &header);
        left = header.x - gtk_widget_get_margin_start(titlebar);
        upper = header.y - gtk_widget_get_margin_top(titlebar);
        frame_width = header.width + gtk_widget_get_margin_start(titlebar) + gtk_widget_get_margin_end(titlebar);
    } else {
        left = body.x - gtk_widget_get_margin_start(content) - border;
        upper = body.y - gtk_widget_get_margin_top(content) - border;
        frame_width = body.width + gtk_widget_get_margin_start(content) + gtk_widget_get_margin_end(content) + 2 * border;
    }
    gint bottom = body.y + body.height + gtk_widget_get_margin_bottom(content) + border;
    gint frame_height = bottom - upper;
    gint x, y;
    if (left < 0 || upper < 0 || frame_width <= 0 || frame_height <= 0
        || !gtk_widget_translate_coordinates(widget, top, 0, 0, &x, &y)) return NULL;
    gint width = gtk_widget_get_allocated_width(widget), height = gtk_widget_get_allocated_height(widget);
    /* Clip through scroll/view containers; never anchor an entirely occluded field. */
    gint right = x + width, lower = y + height;
    for (GtkWidget *parent = gtk_widget_get_parent(widget); parent && parent != top; parent = gtk_widget_get_parent(parent)) {
        gint px, py;
        if (!gtk_widget_translate_coordinates(parent, top, 0, 0, &px, &py)) return NULL;
        x = MAX(x, px); y = MAX(y, py);
        right = MIN(right, px + gtk_widget_get_allocated_width(parent));
        lower = MIN(lower, py + gtk_widget_get_allocated_height(parent));
    }
    x = MAX(x, left); y = MAX(y, upper);
    right = MIN(right, left + frame_width); lower = MIN(lower, upper + frame_height);
    if (right <= x || lower <= y) return NULL;
    JsonObject *result = json_object_new();
    json_object_set_object_member(result, "bounds", rectangle(x - left, y - upper, right - x, lower - y));
    json_object_set_int_member(result, "frameWidth", frame_width);
    json_object_set_int_member(result, "frameHeight", frame_height);
    return result;
}
static JsonObject *metadata(Target *target) {
    if (!supported(target)) return NULL;
    GtkWidget *widget = target->widget;
    GString *labels = g_string_new(NULL);
    GList *list = gtk_widget_list_mnemonic_labels(widget);
    for (GList *item = list; item && labels->len < 4096; item = item->next) {
        if (GTK_IS_LABEL(item->data)) {
            g_string_append(labels, gtk_label_get_text(GTK_LABEL(item->data)));
            g_string_append_c(labels, ' ');
        }
    }
    g_list_free(list);
    const char *name = GTK_IS_BUILDABLE(widget) ? gtk_buildable_get_name(GTK_BUILDABLE(widget)) : NULL;
    char *tooltip = gtk_widget_get_tooltip_text(widget);
    JsonObject *value = json_object_new();
    json_object_set_string_member(value, "process", ""); /* authoritative /proc identity is added by the server */
    json_object_set_string_member(value, "label", labels->str);
    json_object_set_string_member(value, "automationId", name ? name : "");
    json_object_set_string_member(value, "help", tooltip ? tooltip : "");
    json_object_set_string_member(value, "aria", G_OBJECT_TYPE_NAME(widget));
    json_object_set_boolean_member(value, "password", sensitive(target));
    json_object_set_boolean_member(value, "enabled", gtk_widget_is_sensitive(widget));
    json_object_set_boolean_member(value, "readOnly", !editable(target));
    json_object_set_boolean_member(value, "offscreen", !gtk_widget_get_mapped(widget));
    json_object_set_boolean_member(value, "inDocument", FALSE);
    json_object_set_boolean_member(value, "multiline", GTK_IS_TEXT_VIEW(widget));
    json_object_set_boolean_member(value, "hasValue", TRUE);
    json_object_set_boolean_member(value, "hasTextEdit", target->ime_known);
    g_string_free(labels, TRUE); g_free(tooltip);
    return value;
}
static char *metadata_key(JsonObject *value) {
    JsonNode *node = json_node_new(JSON_NODE_OBJECT);
    json_node_set_object(node, value);
    char *text = json_to_string(node, FALSE);
    char *hash = g_compute_checksum_for_string(G_CHECKSUM_SHA256, text, -1);
    json_node_free(node); g_free(text);
    return hash;
}
static JsonObject *authority(Target *target, const char *key) {
    JsonObject *value = json_object_new();
    json_object_set_string_member(value, "target", target->id);
    json_object_set_string_member(value, "metadataKey", key);
    json_object_set_int_member(value, "contentRevision", (gint64)target->content_revision);
    json_object_set_int_member(value, "inputRevision", (gint64)target->input_revision);
    json_object_set_int_member(value, "focusRevision", (gint64)target->focus_revision);
    json_object_set_int_member(value, "observedAtMs", tl_now());
    return value;
}
JsonObject *tl_describe(Target *target) {
    JsonObject *fields = metadata(target);
    if (!fields) return NULL;
    char *key = metadata_key(fields);
    JsonObject *value = json_object_new(), *bounds = geometry(target);
    json_object_set_string_member(value, "target", target->id);
    json_object_set_object_member(value, "metadata", fields);
    json_object_set_object_member(value, "authority", authority(target, key));
    if (bounds) json_object_set_object_member(value, "geometry", bounds);
    else json_object_set_null_member(value, "geometry");
    json_object_set_boolean_member(value, "focused", tl_focused() == target);
    g_free(key);
    return value;
}
gboolean tl_metadata_matches(Target *target, JsonObject *approved) {
    if (!approved || !supported(target) || g_strcmp0(tl_string(approved, "target"), target->id) != 0) return FALSE;
    JsonObject *fields = metadata(target);
    if (!fields) return FALSE;
    char *key = metadata_key(fields);
    gboolean allowed = g_strcmp0(key, tl_string(approved, "metadataKey")) == 0 && !sensitive(target)
        && editable(target) && gtk_widget_is_sensitive(target->widget);
    json_object_unref(fields); g_free(key);
    return allowed;
}
static gint utf16_length(const char *text) {
    if (!text || !g_utf8_validate(text, -1, NULL)) return -1;
    gint length = 0;
    for (const char *p = text; *p; p = g_utf8_next_char(p)) {
        length += g_utf8_get_char(p) > 0xffff ? 2 : 1;
        if (length > TL_MAX_TEXT) return -1;
    }
    return length;
}
static gint char_to_utf16(const char *text, gint offset) {
    gint count = 0, units = 0;
    for (const char *p = text; *p && count < offset; p = g_utf8_next_char(p), count++)
        units += g_utf8_get_char(p) > 0xffff ? 2 : 1;
    return count == offset ? units : -1;
}
static gint utf16_to_char(const char *text, gint offset) {
    gint count = 0, units = 0;
    const char *p = text;
    while (*p && units < offset) {
        units += g_utf8_get_char(p) > 0xffff ? 2 : 1;
        p = g_utf8_next_char(p); count++;
    }
    return units == offset ? count : -1;
}
static char *read_text(Target *target, gint *start, gint *end) {
    char *text;
    if (GTK_IS_ENTRY(target->widget)) {
        text = g_strdup(gtk_entry_get_text(GTK_ENTRY(target->widget)));
        if (!gtk_editable_get_selection_bounds(GTK_EDITABLE(target->widget), start, end))
            *start = *end = gtk_editable_get_position(GTK_EDITABLE(target->widget));
    } else {
        GtkTextBuffer *buffer = GTK_TEXT_BUFFER(target->buffer);
        GtkTextIter first, last;
        if (gtk_text_buffer_get_char_count(buffer) > TL_MAX_TEXT) return NULL;
        gtk_text_buffer_get_bounds(buffer, &first, &last);
        text = gtk_text_buffer_get_text(buffer, &first, &last, TRUE);
        gtk_text_buffer_get_selection_bounds(buffer, &first, &last);
        *start = gtk_text_iter_get_offset(&first); *end = gtk_text_iter_get_offset(&last);
    }
    if (utf16_length(text) < 0) { g_free(text); return NULL; }
    *start = char_to_utf16(text, *start); *end = char_to_utf16(text, *end);
    if (*start < 0 || *end < 0) { g_free(text); return NULL; }
    return text;
}
JsonObject *tl_capture(Target *target, JsonObject *approved) {
    gint64 age = tl_now() - tl_integer(approved, "observedAtMs", 0);
    if (age < 0 || age > 150 || !tl_metadata_matches(target, approved)) return NULL;
    gint start, end;
    char *text = read_text(target, &start, &end);
    if (!text) return NULL;
    JsonObject *description = tl_describe(target);
    if (!description || !tl_metadata_matches(target, approved)) {
        if (description) json_object_unref(description);
        g_free(text); return NULL;
    }
    json_object_set_string_member(description, "text", text);
    JsonObject *selection = json_object_new();
    json_object_set_int_member(selection, "start", start); json_object_set_int_member(selection, "end", end);
    json_object_set_object_member(description, "selection", selection);
    json_object_set_string_member(description, "ime", !target->ime_known ? "Unknown" : target->composing ? "Composing" : "Clear");
    json_object_set_boolean_member(description, "writeSupported", target->ime_known && !target->composing && !target->quarantined);
    json_object_set_int_member(description, "observedAtMs", tl_now());
    g_free(text);
    return description;
}
static JsonObject *receipt(const char *status, const char *reason) {
    JsonObject *value = json_object_new();
    json_object_set_string_member(value, "status", status);
    json_object_set_string_member(value, "reason", reason);
    return value;
}
JsonObject *tl_write(Target *target, JsonObject *request, gboolean direct) {
    JsonObject *plan = tl_object(request, "plan"), *basis = tl_object(request, "authority");
    JsonObject *approved = direct ? tl_object(request, "approved") : basis;
    JsonObject *expected = tl_object(plan, "expected");
    if (!target || !plan || !expected || !basis || !approved)
        return receipt("RejectedBeforeMutation", "BadPlan");
    gint64 now = tl_now(), until = tl_integer(request, "expiresAtMs", 0);
    if (until < now || until - now > (direct ? 250 : 100))
        return receipt("RejectedBeforeMutation", "StaleObservation");
    if (!tl_metadata_matches(target, approved) || !tl_metadata_matches(target, basis))
        return receipt("RejectedBeforeMutation", "Sensitive");
    if (!target->ime_known || target->composing || target->quarantined)
        return receipt("RejectedBeforeMutation", "ImeUnclear");
    if ((gint64)target->content_revision != tl_integer(basis, "contentRevision", -1))
        return receipt("RejectedBeforeMutation", "SourceChanged");
    if (!direct && (tl_focused() != target
        || (gint64)target->input_revision != tl_integer(basis, "inputRevision", -1)
        || (gint64)target->focus_revision != tl_integer(basis, "focusRevision", -1)))
        return receipt("RejectedBeforeMutation", "EpochChanged");
    gint start, end;
    char *text = read_text(target, &start, &end);
    const char *original = tl_string(expected, "text"), *replacement = tl_string(plan, "replacement");
    const char *result = tl_string(plan, "resultText");
    if (!text || !original || !replacement || !result || strcmp(original, text) != 0) {
        g_free(text); return receipt("RejectedBeforeMutation", "SourceChanged");
    }
    JsonObject *selection = tl_object(expected, "selection");
    if (!direct && (start != tl_integer(selection, "start", -1) || end != tl_integer(selection, "end", -1))) {
        g_free(text); return receipt("RejectedBeforeMutation", "SelectionChanged");
    }
    gint units = utf16_length(text), replacement_units = utf16_length(replacement);
    JsonObject *scope = tl_object(plan, "scope");
    gint first = scope ? (gint)tl_integer(scope, "start", -1) : 0;
    gint last = scope ? (gint)tl_integer(scope, "end", -1) : units;
    if (first < 0 || last < first || last > units || (scope && first == last)
        || replacement_units <= 0 || utf16_length(result) < 0
        || (GTK_IS_ENTRY(target->widget) && strpbrk(replacement, "\r\n"))) {
        g_free(text); return receipt("RejectedBeforeMutation", "InvalidText");
    }
    gint from = utf16_to_char(text, first), to = utf16_to_char(text, last);
    gint caret = (gint)tl_integer(plan, "caret", -1);
    if (from < 0 || to < 0 || caret != first + replacement_units) {
        g_free(text); return receipt("RejectedBeforeMutation", "BadPlan");
    }
    const char *begin = g_utf8_offset_to_pointer(text, from), *finish = g_utf8_offset_to_pointer(text, to);
    GString *deleted = g_string_new_len(text, (gssize)(begin - text));
    g_string_append(deleted, finish);
    char *after_delete = g_string_free(deleted, FALSE);
    GString *computed = g_string_new_len(text, (gssize)(begin - text));
    g_string_append(computed, replacement); g_string_append(computed, finish);
    gboolean matches = strcmp(computed->str, result) == 0 && utf16_length(computed->str) >= 0;
    g_string_free(computed, TRUE);
    if (!matches) { g_free(after_delete); g_free(text); return receipt("RejectedBeforeMutation", "BadPlan"); }
    if (GTK_IS_ENTRY(target->widget)) {
        gint limit = gtk_entry_get_max_length(GTK_ENTRY(target->widget));
        if (limit > 0 && g_utf8_strlen(result, -1) > limit) {
            g_free(after_delete); g_free(text); return receipt("RejectedBeforeMutation", "InvalidText");
        }
    }
    /* All validation and the mutation execute in the target's GTK thread. No RPC,
       event-loop iteration, focus change, clipboard paste or retry belongs here. */
    GtkWidget *widget = g_object_ref(target->widget);
    GObject *buffer = g_object_ref(target->buffer);
    target->mutating = TRUE;
    tl_clear_permit();
    if (GTK_IS_ENTRY(widget)) {
        gtk_editable_delete_text(GTK_EDITABLE(widget), from, to);
        if (!target->dead && target->buffer == buffer
            && strcmp(gtk_entry_get_text(GTK_ENTRY(widget)), after_delete) == 0
            && tl_metadata_matches(target, approved) && !target->composing) {
            gint position = from;
            gtk_editable_insert_text(GTK_EDITABLE(widget), replacement, -1, &position);
            gtk_editable_set_position(GTK_EDITABLE(widget), position);
        }
    } else {
        GtkTextBuffer *text_buffer = GTK_TEXT_BUFFER(buffer);
        GtkTextIter a, b;
        gtk_text_buffer_begin_user_action(text_buffer);
        gtk_text_buffer_get_iter_at_offset(text_buffer, &a, from);
        gtk_text_buffer_get_iter_at_offset(text_buffer, &b, to);
        gtk_text_buffer_delete(text_buffer, &a, &b);
        gint intermediate_start, intermediate_end;
        char *intermediate = target->dead ? NULL : read_text(target, &intermediate_start, &intermediate_end);
        if (intermediate && target->buffer == buffer && strcmp(intermediate, after_delete) == 0
            && tl_metadata_matches(target, approved) && !target->composing) {
            gtk_text_buffer_get_iter_at_offset(text_buffer, &a, from);
            gtk_text_buffer_insert(text_buffer, &a, replacement, -1);
            gtk_text_buffer_place_cursor(text_buffer, &a);
        }
        g_free(intermediate);
        gtk_text_buffer_end_user_action(text_buffer);
    }
    target->mutating = FALSE;
    target->content_revision++;
    char *after = target->dead ? NULL : read_text(target, &start, &end);
    gboolean verified = after && target->buffer == buffer && strcmp(after, result) == 0 && start == caret && end == caret
        && target->ime_known && !target->composing && tl_metadata_matches(target, approved);
    JsonObject *reply = receipt(verified ? "Verified" : "OutcomeUnknown", verified ? "None" : "UnknownOutcome");
    if (verified) {
        JsonObject *snapshot = tl_copy(expected);
        json_object_set_string_member(snapshot, "text", after);
        JsonObject *at = json_object_new();
        json_object_set_int_member(at, "start", caret); json_object_set_int_member(at, "end", caret);
        json_object_set_object_member(snapshot, "selection", at);
        json_object_set_int_member(snapshot, "observedAtMs", tl_now());
        json_object_set_string_member(snapshot, "ime", "Clear");
        json_object_set_string_member(snapshot, "access", "Supported");
        json_object_set_object_member(reply, "verifiedSnapshot", snapshot);
    } else target->quarantined = TRUE;
    g_free(after_delete); g_free(after); g_free(text); g_object_unref(buffer); g_object_unref(widget);
    return reply;
}
gboolean tl_return(Target *target, JsonObject *basis) {
    if (!tl_metadata_matches(target, basis) || target->composing || !target->ime_known
        || (gint64)target->content_revision != tl_integer(basis, "contentRevision", -1)) return FALSE;
    GtkWidget *top = gtk_widget_get_toplevel(target->widget);
    if (!GTK_IS_WINDOW(top) || !gtk_window_is_active(GTK_WINDOW(top))) return FALSE;
    if (GTK_IS_ENTRY(target->widget)) gtk_entry_grab_focus_without_selecting(GTK_ENTRY(target->widget));
    else gtk_widget_grab_focus(target->widget);
    return tl_focused() == target;
}
