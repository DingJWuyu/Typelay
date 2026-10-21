#include <gtk/gtk.h>
#include <json-glib/json-glib.h>
#include "../../../src/linux/native/fcitx5/typelay-fcitx-v1.h"

/* Only the isolated test executable writes this evidence. Production has no
 * command to dump source text, inject input or change a test model. */
static GtkWidget *report_window, *report_first, *report_second, *other_window, *other_entry;
static GtkWidget *protected_fields[2], *report_multiline, *excluded_views[3];
static const char *report_path;
static char *last_report;
typedef struct {
    guint insertions, deletions, focus_in, focus_out;
} EntryEvidence;
static EntryEvidence first_evidence, second_evidence;
static JsonObject *first_insert_context, *first_delete_context;
#include "gtk_reentry.inc"

static guint function_down[24], function_up[24], tab_down, tab_up;
static JsonObject *focus_transitions[16];
static guint focus_sequence;
static void observe_focus(GObject *object, GParamSpec *spec, gpointer data) {
    (void)spec; (void)data;
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(object));
    const char *scope = "other";
    for (GtkWidget *at = focus; at; at = gtk_widget_get_parent(at)) {
        if (at == report_first) { scope = "first"; break; }
        if (at == report_second) { scope = "second"; break; }
        if (g_strcmp0(gtk_widget_get_name(at), "typelay-overlay") == 0) { scope = "overlay"; break; }
    }
    guint index = focus_sequence++ % G_N_ELEMENTS(focus_transitions);
    if (focus_transitions[index]) json_object_unref(focus_transitions[index]);
    JsonObject *value = focus_transitions[index] = json_object_new();
    json_object_set_int_member(value, "sequence", focus_sequence);
    json_object_set_int_member(value, "atMonoMs", g_get_monotonic_time() / 1000);
    json_object_set_string_member(value, "type", focus ? G_OBJECT_TYPE_NAME(focus) : "none");
    json_object_set_string_member(value, "name", focus ? gtk_widget_get_name(focus) : "");
    json_object_set_string_member(value, "scope", scope);
    json_object_set_boolean_member(value, "canFocus", focus && gtk_widget_get_can_focus(focus));
    json_object_set_boolean_member(value, "focusable", focus && gtk_widget_get_focusable(focus));
}
static gboolean observe_key(GtkEventControllerLegacy *controller, GdkEvent *event, gpointer data) {
    (void)controller; (void)data;
    GdkEventType type = gdk_event_get_event_type(event);
    if (type != GDK_KEY_PRESS && type != GDK_KEY_RELEASE) return FALSE;
    guint keyval = gdk_key_event_get_keyval(event);
    if (keyval == GDK_KEY_Tab || keyval == GDK_KEY_ISO_Left_Tab) {
        if (type == GDK_KEY_PRESS) tab_down++;
        else tab_up++;
    }
    if (keyval >= GDK_KEY_F1 && keyval <= GDK_KEY_F24) {
        guint index = keyval - GDK_KEY_F1;
        if (type == GDK_KEY_PRESS) function_down[index]++;
        else function_up[index]++;
    }
    return FALSE;
}
static GtkWidget *entry_client(GtkWidget *widget) {
    return GTK_WIDGET(gtk_editable_get_delegate(GTK_EDITABLE(widget)));
}
static void focus_changed(GObject *object, GParamSpec *spec, gpointer data) {
    (void)spec;
    EntryEvidence *evidence = data;
    if (gtk_widget_has_focus(GTK_WIDGET(object))) evidence->focus_in++;
    else evidence->focus_out++;
}
static void remember_entry_change(EntryEvidence *evidence, JsonObject **observation) {
    if (evidence != &first_evidence) return;
    if (*observation) json_object_unref(*observation);
    JsonObject *context = *observation = json_object_new();
    gint start, end;
    gtk_editable_get_selection_bounds(GTK_EDITABLE(report_second), &start, &end);
    json_object_set_boolean_member(context, "secondFocused",
        gtk_widget_has_focus(entry_client(report_second)));
    json_object_set_string_member(context, "secondText", gtk_editable_get_text(GTK_EDITABLE(report_second)));
    json_object_set_int_member(context, "secondStart", start);
    json_object_set_int_member(context, "secondEnd", end);
    json_object_set_int_member(context, "firstFocusIn", first_evidence.focus_in);
    json_object_set_int_member(context, "firstFocusOut", first_evidence.focus_out);
}
static void entry_inserted(GtkEntryBuffer *buffer, guint position, const char *text, guint count, gpointer data) {
    (void)buffer; (void)position; (void)text; (void)count;
    EntryEvidence *evidence = data;
    evidence->insertions++;
    remember_entry_change(evidence, &first_insert_context);
    if (evidence == &first_evidence) mutation_barrier("insert");
}
static void entry_deleted(GtkEntryBuffer *buffer, guint position, guint count, gpointer data) {
    (void)buffer; (void)position; (void)count;
    EntryEvidence *evidence = data;
    evidence->deletions++;
    remember_entry_change(evidence, &first_delete_context);
    if (evidence == &first_evidence) mutation_barrier("delete");
}
static void watch_entry(GtkWidget *widget, EntryEvidence *evidence) {
    GtkEntryBuffer *buffer = gtk_entry_get_buffer(GTK_ENTRY(widget));
    g_signal_connect_after(buffer, "inserted-text", G_CALLBACK(entry_inserted), evidence);
    g_signal_connect_after(buffer, "deleted-text", G_CALLBACK(entry_deleted), evidence);
    g_signal_connect(entry_client(widget), "notify::has-focus", G_CALLBACK(focus_changed), evidence);
    g_signal_connect_after(widget, "notify::cursor-position", G_CALLBACK(selection_changed), evidence);
    g_signal_connect_after(widget, "notify::selection-bound", G_CALLBACK(selection_changed), evidence);
}

static gboolean inspect_ime(GtkWidget *widget, TlFcitxStateV1 *state) {
    if (GTK_IS_ENTRY(widget)) widget = GTK_WIDGET(gtk_editable_get_delegate(GTK_EDITABLE(widget)));
    GtkIMContext *context = widget ? g_object_get_data(G_OBJECT(widget), TL_FCITX_CONTEXT_V1) : NULL;
    if (!context || !GTK_IS_IM_CONTEXT(context)) return FALSE;
    g_object_ref(context);
    const TlFcitxCapabilityV1 *cap = g_object_get_data(G_OBJECT(context), TL_FCITX_CAPABILITY_V1);
    gboolean valid = cap && cap->size == sizeof(*cap) && cap->version == TL_FCITX_ABI_V1
        && cap->inspect && cap->inspect(context, widget, state);
    g_object_unref(context);
    return valid;
}
static gboolean ime_clear(const TlFcitxStateV1 *state) {
    return state->flags == (TL_FCITX_KNOWN | TL_FCITX_FOCUSED)
        && state->server_serial == state->applied_serial;
}
/* These are observations of the real widgets, never inputs to the provider. */
static void report_entry(JsonBuilder *builder, const char *name, GtkWidget *widget, const EntryEvidence *evidence) {
    gint start, end;
    gtk_editable_get_selection_bounds(GTK_EDITABLE(widget), &start, &end);
    json_builder_set_member_name(builder, name);
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "focused");
    json_builder_add_boolean_value(builder, gtk_widget_has_focus(entry_client(widget)));
    json_builder_set_member_name(builder, "start"); json_builder_add_int_value(builder, start);
    json_builder_set_member_name(builder, "end"); json_builder_add_int_value(builder, end);
    json_builder_set_member_name(builder, "insertions"); json_builder_add_int_value(builder, evidence->insertions);
    json_builder_set_member_name(builder, "deletions"); json_builder_add_int_value(builder, evidence->deletions);
    json_builder_set_member_name(builder, "focusIn"); json_builder_add_int_value(builder, evidence->focus_in);
    json_builder_set_member_name(builder, "focusOut"); json_builder_add_int_value(builder, evidence->focus_out);
    graphene_point_t origin = GRAPHENE_POINT_INIT(0, 0), point;
    if (gtk_widget_compute_point(widget, report_window, &origin, &point)) {
        json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, point.x);
        json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, point.y);
        json_builder_set_member_name(builder, "width");
        json_builder_add_int_value(builder, gtk_widget_get_width(widget));
        json_builder_set_member_name(builder, "height");
        json_builder_add_int_value(builder, gtk_widget_get_height(widget));
    }
    TlFcitxStateV1 state = { .size = sizeof(state), .version = TL_FCITX_ABI_V1 };
    gboolean valid = inspect_ime(widget, &state);
    json_builder_set_member_name(builder, "ime");
    if (valid) {
        json_builder_begin_object(builder);
        json_builder_set_member_name(builder, "flags"); json_builder_add_int_value(builder, state.flags);
        json_builder_set_member_name(builder, "context"); json_builder_add_int_value(builder, state.context_generation);
        json_builder_set_member_name(builder, "input"); json_builder_add_int_value(builder, state.input_epoch);
        json_builder_set_member_name(builder, "server"); json_builder_add_int_value(builder, state.server_serial);
        json_builder_set_member_name(builder, "applied"); json_builder_add_int_value(builder, state.applied_serial);
        json_builder_end_object(builder);
    } else json_builder_add_null_value(builder);
    json_builder_end_object(builder);
}
static void find_candidate(GtkWidget *widget, gpointer data) {
    watch_session_button(widget);
    GtkWidget **candidate = data;
    if (GTK_IS_TEXT_VIEW(widget)
        && g_strcmp0(gtk_widget_get_name(widget), "typelay-candidate") == 0) {
        *candidate = widget;
        remove_session_controller(widget, TRUE);
    } else for (GtkWidget *child = gtk_widget_get_first_child(widget); child; child = gtk_widget_get_next_sibling(child))
        find_candidate(child, data);
}

/* Test-local identities survive observations without exposing native addresses. */
static guint language_instance(GObject *object) {
    static guint next;
    if (!object) return 0;
    guint instance = GPOINTER_TO_UINT(g_object_get_data(object, "fixture-language-instance"));
    if (!instance) {
        instance = ++next;
        g_object_set_data(object, "fixture-language-instance", GUINT_TO_POINTER(instance));
    }
    return instance;
}
/* Bounded diagnostic for the first language popup transitions. Signals only
 * sample public state: no measure, focus operation or event-loop iteration. */
static JsonObject *language_transitions[16];
static guint language_transition_count;
static void observe_language_popup(GtkWidget *button, const char *event) {
    if (language_transition_count == G_N_ELEMENTS(language_transitions)) return;
    GtkPopover *popup = gtk_menu_button_get_popover(GTK_MENU_BUTTON(button));
    if (!popup) return;
    JsonObject *value = language_transitions[language_transition_count++] = json_object_new();
    json_object_set_int_member(value, "atMonoMs", g_get_monotonic_time() / 1000);
    json_object_set_string_member(value, "control", gtk_widget_get_name(button));
    json_object_set_string_member(value, "event", event);
    json_object_set_boolean_member(value, "active", gtk_menu_button_get_active(GTK_MENU_BUTTON(button)));
    json_object_set_boolean_member(value, "visible", gtk_widget_get_visible(GTK_WIDGET(popup)));
    json_object_set_boolean_member(value, "mapped", gtk_widget_get_mapped(GTK_WIDGET(popup)));
}
static void language_active_changed(GObject *button, GParamSpec *spec, gpointer data) {
    (void)spec; (void)data;
    observe_language_popup(GTK_WIDGET(button), "active");
}
static void language_visible_changed(GObject *popup, GParamSpec *spec, gpointer data) {
    (void)popup; (void)spec;
    observe_language_popup(data, "visible");
}
static void language_mapped(GtkWidget *popup, gpointer data) {
    (void)popup;
    observe_language_popup(data, "map");
}
static void language_unmapped(GtkWidget *popup, gpointer data) {
    (void)popup;
    observe_language_popup(data, "unmap");
}
static void watch_language_popup(GtkWidget *button, GtkPopover *popup) {
    if (g_object_get_data(G_OBJECT(button), "fixture-language-popup-watched")) return;
    g_object_set_data(G_OBJECT(button), "fixture-language-popup-watched", GINT_TO_POINTER(1));
    g_signal_connect_after(button, "notify::active", G_CALLBACK(language_active_changed), NULL);
    g_signal_connect_object(popup, "notify::visible", G_CALLBACK(language_visible_changed), button, G_CONNECT_AFTER);
    g_signal_connect_object(popup, "map", G_CALLBACK(language_mapped), button, G_CONNECT_AFTER);
    g_signal_connect_object(popup, "unmap", G_CALLBACK(language_unmapped), button, G_CONNECT_AFTER);
}
static gboolean language_control(const char *name) {
    return g_strcmp0(name, "typelay-menu-trigger") == 0
        || g_strcmp0(name, "typelay-source-language") == 0
        || g_strcmp0(name, "typelay-target-language") == 0
        || g_strcmp0(name, "typelay-apply-languages") == 0
        || g_strcmp0(name, "typelay-language-menu") == 0;
}
static void report_language_control(GtkWidget *widget, gpointer data) {
    JsonBuilder *builder = data;
    const char *name = gtk_widget_get_name(widget);
    /* Public label discovery matches the existing reentry watcher. Report its
     * current allocation on every frame, never freeze a pre-layout center. */
    gboolean expand = GTK_IS_BUTTON(widget)
        && g_strcmp0(g_getenv("TYPELAY_FIXTURE_REENTRY_STAGE"), "expand") == 0
        && g_strcmp0(gtk_button_get_label(GTK_BUTTON(widget)), "展开") == 0;
    if (expand) name = "fixture-expand";
    if (expand || language_control(name)) {
        json_builder_set_member_name(builder, name);
        json_builder_begin_object(builder);
        if (expand) {
            GtkEventControllerMotion *motion = g_object_get_data(G_OBJECT(widget), "fixture-expand-motion");
            json_builder_set_member_name(builder, "pointerInside");
            json_builder_add_boolean_value(builder, gtk_widget_get_mapped(widget) && motion
                && gtk_event_controller_motion_contains_pointer(motion));
            json_builder_set_member_name(builder, "pointerEnteredAtMs");
            json_builder_add_int_value(builder,
                (gintptr)g_object_get_data(G_OBJECT(widget), "fixture-expand-enter-at"));
            json_builder_set_member_name(builder, "scale");
            json_builder_add_int_value(builder, gtk_widget_get_scale_factor(widget));
        }
        json_builder_set_member_name(builder, "instance");
        json_builder_add_int_value(builder, language_instance(G_OBJECT(widget)));
        json_builder_set_member_name(builder, "visible");
        json_builder_add_boolean_value(builder, gtk_widget_get_mapped(widget));
        json_builder_set_member_name(builder, "enabled");
        json_builder_add_boolean_value(builder, gtk_widget_is_sensitive(widget));
        GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(report_window));
        json_builder_set_member_name(builder, "focused");
        json_builder_add_boolean_value(builder, focus && (focus == widget || gtk_widget_is_ancestor(focus, widget)));
        if (GTK_IS_BUTTON(widget)) {
            const char *label = gtk_button_get_label(GTK_BUTTON(widget));
            json_builder_set_member_name(builder, "text");
            json_builder_add_string_value(builder, label ? label : "");
        }
        graphene_point_t origin = GRAPHENE_POINT_INIT(0, 0), point;
        if (gtk_widget_compute_point(widget, report_window, &origin, &point)) {
            json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, point.x);
            json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, point.y);
            json_builder_set_member_name(builder, "width"); json_builder_add_int_value(builder, gtk_widget_get_width(widget));
            json_builder_set_member_name(builder, "height"); json_builder_add_int_value(builder, gtk_widget_get_height(widget));
        }
        if (GTK_IS_MENU_BUTTON(widget)) {
            GtkPopover *popup = gtk_menu_button_get_popover(GTK_MENU_BUTTON(widget));
            GtkWidget *scroll = popup ? gtk_popover_get_child(popup) : NULL;
            GtkWidget *list = GTK_IS_SCROLLED_WINDOW(scroll) ? gtk_scrolled_window_get_child(GTK_SCROLLED_WINDOW(scroll)) : NULL;
            if (GTK_IS_LIST_VIEW(list)) {
                watch_language_popup(widget, popup);
                json_builder_set_member_name(builder, "popupActive");
                json_builder_add_boolean_value(builder, gtk_menu_button_get_active(GTK_MENU_BUTTON(widget)));
                json_builder_set_member_name(builder, "popupShown");
                json_builder_add_boolean_value(builder, gtk_widget_get_visible(GTK_WIDGET(popup)));
                GtkSingleSelection *selection = GTK_SINGLE_SELECTION(gtk_list_view_get_model(GTK_LIST_VIEW(list)));
                GListModel *model = gtk_single_selection_get_model(selection);
                json_builder_set_member_name(builder, "modelInstance");
                json_builder_add_int_value(builder, language_instance(G_OBJECT(model)));
                json_builder_set_member_name(builder, "selected");
                json_builder_add_int_value(builder, gtk_single_selection_get_selected(selection));
                json_builder_set_member_name(builder, "popupVisible");
                json_builder_add_boolean_value(builder, gtk_widget_get_mapped(GTK_WIDGET(popup)));
                json_builder_set_member_name(builder, "buttonFocused");
                json_builder_add_boolean_value(builder, focus && gtk_widget_get_mapped(focus)
                    && (focus == widget || gtk_widget_is_ancestor(focus, widget))
                    && focus != GTK_WIDGET(popup) && !gtk_widget_is_ancestor(focus, GTK_WIDGET(popup)));
                json_builder_set_member_name(builder, "popupFocused");
                json_builder_add_boolean_value(builder, focus && gtk_widget_get_mapped(GTK_WIDGET(popup))
                    && (focus == list || gtk_widget_is_ancestor(focus, list)));
                json_builder_set_member_name(builder, "items");
                json_builder_begin_array(builder);
                if (model) for (guint index = 0; index < g_list_model_get_n_items(model); index++) {
                    GObject *item = g_list_model_get_item(model, index);
                    if (GTK_IS_STRING_OBJECT(item))
                        json_builder_add_string_value(builder, gtk_string_object_get_string(GTK_STRING_OBJECT(item)));
                    g_object_unref(item);
                }
                json_builder_end_array(builder);
            }
        }
        json_builder_end_object(builder);
    }
    for (GtkWidget *child = gtk_widget_get_first_child(widget); child; child = gtk_widget_get_next_sibling(child))
        report_language_control(child, data);
}

#include "gtk_motion.inc"

static gboolean report_state(gpointer unused) {
    (void)unused;
    remove_session_controller(report_window, FALSE);
    GtkWidget *active = other_window && gtk_window_is_active(GTK_WINDOW(other_window)) ? other_window : report_window;
    GtkWidget *focus = gtk_window_get_focus(GTK_WINDOW(active));
    GtkWidget *focus_field = focus;
    while (focus_field && !GTK_IS_ENTRY(focus_field) && !GTK_IS_TEXT_VIEW(focus_field) && !GTK_IS_PASSWORD_ENTRY(focus_field))
        focus_field = gtk_widget_get_parent(focus_field);
    JsonBuilder *builder = json_builder_new();
    json_builder_begin_object(builder);
    motion_report(builder);
    json_builder_set_member_name(builder, "observedAtMs");
    json_builder_add_int_value(builder, g_get_monotonic_time() / 1000);
    json_builder_set_member_name(builder, "first");
    json_builder_add_string_value(builder, gtk_editable_get_text(GTK_EDITABLE(report_first)));
    json_builder_set_member_name(builder, "second");
    json_builder_add_string_value(builder, gtk_editable_get_text(GTK_EDITABLE(report_second)));
    if (other_entry) {
        json_builder_set_member_name(builder, "other");
        json_builder_add_string_value(builder, gtk_editable_get_text(GTK_EDITABLE(other_entry)));
    }
    json_builder_set_member_name(builder, "focus");
    json_builder_add_string_value(builder, focus_field ? gtk_widget_get_name(focus_field) : "");
    if (g_getenv("TYPELAY_FIXTURE_COOPERATIVE")) {
        report_entry(builder, "firstEntry", report_first, &first_evidence);
        report_entry(builder, "secondEntry", report_second, &second_evidence);
        const char *names[] = {"firstInsertContext", "firstDeleteContext"};
        JsonObject *contexts[] = {first_insert_context, first_delete_context};
        for (guint i = 0; i < G_N_ELEMENTS(contexts); i++) {
            json_builder_set_member_name(builder, names[i]);
            if (contexts[i]) {
                JsonNode *change = json_node_new(JSON_NODE_OBJECT);
                json_node_set_object(change, contexts[i]);
                json_builder_add_value(builder, change);
            } else json_builder_add_null_value(builder);
        }
        TlFcitxStateV1 state = { .size = sizeof(state), .version = TL_FCITX_ABI_V1 };
        gboolean valid = inspect_ime(report_first, &state);
        json_builder_set_member_name(builder, "cooperative");
        json_builder_add_boolean_value(builder, valid);
        json_builder_set_member_name(builder, "imeFocused");
        json_builder_add_boolean_value(builder, valid && (state.flags & TL_FCITX_FOCUSED));
        json_builder_set_member_name(builder, "imeClear");
        json_builder_add_boolean_value(builder, valid && ime_clear(&state));
        json_builder_set_member_name(builder, "imeComposing");
        json_builder_add_boolean_value(builder, valid && (state.flags & TL_FCITX_COMPOSING));
        state = (TlFcitxStateV1){ .size = sizeof(state), .version = TL_FCITX_ABI_V1 };
        valid = inspect_ime(focus, &state);
        json_builder_set_member_name(builder, "focusedImeClear");
        json_builder_add_boolean_value(builder, valid && ime_clear(&state));
        json_builder_set_member_name(builder, "focusedImeComposing");
        json_builder_add_boolean_value(builder, valid && (state.flags & TL_FCITX_COMPOSING));
        json_builder_set_member_name(builder, "fullscreen");
        json_builder_add_boolean_value(builder,
            gtk_window_is_fullscreen(GTK_WINDOW(report_window)));
        json_builder_set_member_name(builder, "width");
        json_builder_add_int_value(builder, gtk_widget_get_width(report_window));
        json_builder_set_member_name(builder, "height");
        json_builder_add_int_value(builder, gtk_widget_get_height(report_window));
        json_builder_set_member_name(builder, "scale");
        json_builder_add_int_value(builder, gtk_widget_get_scale_factor(report_window));
        json_builder_set_member_name(builder, "overlayControls");
        json_builder_begin_object(builder);
        report_language_control(report_window, builder);
        json_builder_end_object(builder);
        GtkWidget *candidate = NULL;
        find_candidate(report_window, &candidate);
        graphene_point_t origin = GRAPHENE_POINT_INIT(0, 0), point;
        json_builder_set_member_name(builder, "candidate");
        if (candidate && gtk_widget_get_mapped(candidate)
            && gtk_widget_compute_point(candidate, report_window, &origin, &point)) {
            json_builder_begin_object(builder);
            json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, point.x);
            json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, point.y);
            json_builder_set_member_name(builder, "width");
            json_builder_add_int_value(builder, gtk_widget_get_width(candidate));
            json_builder_set_member_name(builder, "height");
            json_builder_add_int_value(builder, gtk_widget_get_height(candidate));
            json_builder_set_member_name(builder, "editablePreview");
            json_builder_add_boolean_value(builder, gtk_widget_has_css_class(candidate, "editable-preview"));
            GdkRGBA foreground;
#if GTK_CHECK_VERSION(4, 10, 0)
            gtk_widget_get_color(candidate, &foreground);
#else
            gtk_style_context_get_color(gtk_widget_get_style_context(candidate), &foreground);
#endif
            char *color = gdk_rgba_to_string(&foreground);
            json_builder_set_member_name(builder, "foreground"); json_builder_add_string_value(builder, color);
            g_free(color);
            GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(candidate));
            GtkTextIter first, last;
            gtk_text_buffer_get_bounds(buffer, &first, &last);
            char *text = gtk_text_buffer_get_text(buffer, &first, &last, TRUE);
            json_builder_set_member_name(builder, "text"); json_builder_add_string_value(builder, text);
            g_free(text);
            json_builder_end_object(builder);
        } else json_builder_add_null_value(builder);
    }
    json_builder_set_member_name(builder, "multiline");
    json_builder_begin_object(builder);
    GtkTextBuffer *multiline = gtk_text_view_get_buffer(GTK_TEXT_VIEW(report_multiline));
    GtkTextIter begin, end, selected_begin, selected_end;
    gtk_text_buffer_get_bounds(multiline, &begin, &end);
    char *body = gtk_text_buffer_get_text(multiline, &begin, &end, TRUE);
    json_builder_set_member_name(builder, "text"); json_builder_add_string_value(builder, body);
    g_free(body);
    gtk_text_buffer_get_selection_bounds(multiline, &selected_begin, &selected_end);
    json_builder_set_member_name(builder, "start"); json_builder_add_int_value(builder, gtk_text_iter_get_offset(&selected_begin));
    json_builder_set_member_name(builder, "end"); json_builder_add_int_value(builder, gtk_text_iter_get_offset(&selected_end));
    json_builder_set_member_name(builder, "canUndo"); json_builder_add_boolean_value(builder, gtk_text_buffer_get_can_undo(multiline));
    graphene_point_t origin = GRAPHENE_POINT_INIT(0, 0), point;
    if (gtk_widget_compute_point(report_multiline, report_window, &origin, &point)) {
        json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, point.x);
        json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, point.y);
    }
    json_builder_end_object(builder);
    json_builder_set_member_name(builder, "excludedTextViews");
    json_builder_begin_array(builder);
    for (guint i = 0; i < G_N_ELEMENTS(excluded_views); i++) {
        if (gtk_widget_compute_point(excluded_views[i], report_window, &origin, &point)) {
            json_builder_begin_object(builder);
            json_builder_set_member_name(builder, "name"); json_builder_add_string_value(builder, gtk_widget_get_name(excluded_views[i]));
            json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, point.x);
            json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, point.y);
            json_builder_end_object(builder);
        }
    }
    json_builder_end_array(builder);
    json_builder_set_member_name(builder, "protectedFields");
    json_builder_begin_array(builder);
    for (guint i = 0; i < G_N_ELEMENTS(protected_fields); i++) {
        graphene_point_t origin = GRAPHENE_POINT_INIT(0, 0), point;
        GtkWidget *field = protected_fields[i];
        if (field && gtk_widget_compute_point(field, report_window, &origin, &point)) {
            json_builder_begin_object(builder);
            json_builder_set_member_name(builder, "name");
            json_builder_add_string_value(builder, gtk_widget_get_name(field));
            json_builder_set_member_name(builder, "x"); json_builder_add_double_value(builder, point.x);
            json_builder_set_member_name(builder, "y"); json_builder_add_double_value(builder, point.y);
            json_builder_set_member_name(builder, "width"); json_builder_add_int_value(builder, gtk_widget_get_width(field));
            json_builder_set_member_name(builder, "height"); json_builder_add_int_value(builder, gtk_widget_get_height(field));
            json_builder_end_object(builder);
        }
    }
    json_builder_end_array(builder);
    json_builder_set_member_name(builder, "tabKeys");
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "down"); json_builder_add_int_value(builder, tab_down);
    json_builder_set_member_name(builder, "up"); json_builder_add_int_value(builder, tab_up);
    json_builder_end_object(builder);
    json_builder_set_member_name(builder, "languageTransitions");
    json_builder_begin_array(builder);
    for (guint index = 0; index < language_transition_count; index++) {
        JsonNode *transition = json_node_new(JSON_NODE_OBJECT);
        json_node_set_object(transition, language_transitions[index]);
        json_builder_add_value(builder, transition);
    }
    json_builder_end_array(builder);
    json_builder_set_member_name(builder, "focusSequence"); json_builder_add_int_value(builder, focus_sequence);
    json_builder_set_member_name(builder, "focusTransitions");
    json_builder_begin_array(builder);
    guint first = focus_sequence > G_N_ELEMENTS(focus_transitions) ? focus_sequence - G_N_ELEMENTS(focus_transitions) : 0;
    for (guint index = first; index < focus_sequence; index++) {
        JsonNode *transition = json_node_new(JSON_NODE_OBJECT);
        json_node_set_object(transition, focus_transitions[index % G_N_ELEMENTS(focus_transitions)]);
        json_builder_add_value(builder, transition);
    }
    json_builder_end_array(builder);
    json_builder_set_member_name(builder, "functionKeys");
    json_builder_begin_array(builder);
    for (guint index = 0; index < G_N_ELEMENTS(function_down); index++) {
        json_builder_begin_object(builder);
        json_builder_set_member_name(builder, "down"); json_builder_add_int_value(builder, function_down[index]);
        json_builder_set_member_name(builder, "up"); json_builder_add_int_value(builder, function_up[index]);
        json_builder_end_object(builder);
    }
    json_builder_end_array(builder);
    json_builder_end_object(builder);
    JsonNode *node = json_builder_get_root(builder);
    JsonGenerator *generator = json_generator_new();
    json_generator_set_root(generator, node);
    char *data = json_generator_to_data(generator, NULL);
    if (g_strcmp0(data, last_report) != 0) {
        g_file_set_contents(report_path, data, -1, NULL);
        g_free(last_report);
        last_report = g_strdup(data);
    }
    g_free(data);
    g_object_unref(generator);
    json_node_free(node);
    g_object_unref(builder);
    return G_SOURCE_CONTINUE;
}

/* An actual GTK client, not a production backend or a source-string assertion. */
static gboolean close_fixture(GtkWindow *window, gpointer data) {
    (void)window; g_main_loop_quit(data); return FALSE;
}
int main(void) {
    gtk_init();
    GMainLoop *loop = g_main_loop_new(NULL, FALSE);
    GtkWidget *window = gtk_window_new();
    gtk_window_set_title(GTK_WINDOW(window), "TypeLay GTK4 contract fixture");
    gtk_window_set_default_size(GTK_WINDOW(window), 650, 480);
    GtkWidget *title = gtk_header_bar_new();
    gtk_header_bar_set_title_widget(GTK_HEADER_BAR(title), gtk_label_new("TypeLay input contracts"));
    gtk_header_bar_set_show_title_buttons(GTK_HEADER_BAR(title), TRUE);
    gtk_window_set_titlebar(GTK_WINDOW(window), title);
    GtkWidget *box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 16);
    gtk_widget_set_margin_start(box, 32); gtk_widget_set_margin_end(box, 32);
    gtk_widget_set_margin_top(box, 32); gtk_widget_set_margin_bottom(box, 32);
    gtk_window_set_child(GTK_WINDOW(window), box);
    GtkWidget *first = gtk_entry_new(), *second = gtk_entry_new(), *password = gtk_entry_new();
    gtk_widget_set_name(first, "fixture-first");
    gtk_widget_set_name(second, "fixture-second");
    gtk_widget_set_name(password, "fixture-password");
    const char *initial = g_getenv("TYPELAY_FIXTURE_INITIAL_TEXT");
    gtk_editable_set_text(GTK_EDITABLE(first), initial ? initial : "alpha 😀 omega");
    gtk_editable_set_text(GTK_EDITABLE(second), "second field");
    gtk_entry_set_visibility(GTK_ENTRY(password), FALSE);
    gtk_editable_set_text(GTK_EDITABLE(password), "fixture-only-private");
    gtk_box_append(GTK_BOX(box), gtk_label_new("Ordinary entries and excluded password field"));
    gtk_box_append(GTK_BOX(box), first);
    gtk_box_append(GTK_BOX(box), second);
    /* Native automationId is the buildable ID, not the CSS/widget name. */
    GtkBuilder *identities = gtk_builder_new();
    gtk_builder_expose_object(identities, "fixture-second", G_OBJECT(second));
    g_object_unref(identities);
    g_assert_cmpstr(gtk_buildable_get_buildable_id(GTK_BUILDABLE(second)), ==, "fixture-second");
    gtk_box_append(GTK_BOX(box), password);
    GtkWidget *password_entry = gtk_password_entry_new();
    gtk_widget_set_name(password_entry, "fixture-password-entry");
    gtk_editable_set_text(GTK_EDITABLE(password_entry), "synthetic-secret");
    gtk_box_append(GTK_BOX(box), password_entry);
    protected_fields[0] = password;
    protected_fields[1] = password_entry;
    GtkWidget *view = gtk_text_view_new();
    gtk_widget_set_name(view, "fixture-multiline");
    GtkTextBuffer *text_buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(view));
    gtk_text_buffer_set_enable_undo(text_buffer, TRUE);
    gtk_text_buffer_set_text(text_buffer, "Before 😀\nPlease review this note.\nPlease send me the final version.\nAfter untouched", -1);
    gtk_widget_set_size_request(view, -1, 100);
    report_multiline = view;
    gtk_box_append(GTK_BOX(box), view);
    const char *names[] = {"fixture-tagged", "fixture-object", "fixture-readonly"};
    for (guint i = 0; i < G_N_ELEMENTS(excluded_views); i++) {
        GtkWidget *excluded = gtk_text_view_new();
        excluded_views[i] = excluded;
        gtk_widget_set_name(excluded, names[i]);
        GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(excluded));
        gtk_text_buffer_set_text(buffer, "Excluded fixture content", -1);
        GtkTextIter first, last;
        gtk_text_buffer_get_bounds(buffer, &first, &last);
        if (i == 0) {
            GtkTextTag *tag = gtk_text_buffer_create_tag(buffer, "fixture-tag", "weight", PANGO_WEIGHT_BOLD, NULL);
            gtk_text_buffer_apply_tag(buffer, tag, &first, &last);
        } else if (i == 1) {
            GtkTextChildAnchor *anchor = gtk_text_buffer_create_child_anchor(buffer, &first);
            gtk_text_view_add_child_at_anchor(GTK_TEXT_VIEW(excluded), gtk_label_new("Embedded object"), anchor);
        } else gtk_text_view_set_editable(GTK_TEXT_VIEW(excluded), FALSE);
        gtk_widget_set_size_request(excluded, -1, 32);
        gtk_box_append(GTK_BOX(box), excluded);
    }
    g_signal_connect(window, "close-request", G_CALLBACK(close_fixture), loop);
    gtk_window_present(GTK_WINDOW(window));
    if (g_getenv("TYPELAY_FIXTURE_FULLSCREEN")) gtk_window_fullscreen(GTK_WINDOW(window));
    gtk_widget_grab_focus(first);
    gtk_editable_set_position(GTK_EDITABLE(first), -1);
    if (g_getenv("TYPELAY_FIXTURE_TWO_WINDOWS")) {
        other_window = gtk_window_new();
        gtk_window_set_title(GTK_WINDOW(other_window), "TypeLay GTK4 second window");
        gtk_window_set_default_size(GTK_WINDOW(other_window), 600, 300);
        GtkWidget *other_box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 16);
        gtk_widget_set_margin_start(other_box, 32); gtk_widget_set_margin_end(other_box, 32);
        other_entry = gtk_entry_new();
        gtk_widget_set_name(other_entry, "fixture-other-window");
        gtk_editable_set_text(GTK_EDITABLE(other_entry), "The other window is unchanged.");
        gtk_box_append(GTK_BOX(other_box), other_entry);
        gtk_window_set_child(GTK_WINDOW(other_window), other_box);
        g_signal_connect(other_window, "close-request", G_CALLBACK(close_fixture), loop);
        gtk_window_present(GTK_WINDOW(other_window));
    }
    report_path = g_getenv("TYPELAY_FIXTURE_STATE");
    if (report_path && g_path_is_absolute(report_path)) {
        report_window = window;
        report_first = first;
        report_second = second;
        g_signal_connect(window, "notify::focus-widget", G_CALLBACK(observe_focus), NULL);
        GtkWidget *key_clients[] = {entry_client(first), entry_client(second), view};
        for (guint index = 0; index < G_N_ELEMENTS(key_clients); index++) {
            GtkEventController *keys = gtk_event_controller_legacy_new();
            gtk_event_controller_set_propagation_phase(keys, GTK_PHASE_CAPTURE);
            g_signal_connect(keys, "event", G_CALLBACK(observe_key), NULL);
            gtk_widget_add_controller(key_clients[index], keys);
        }
        watch_entry(first, &first_evidence);
        watch_entry(second, &second_evidence);
        motion_init();
        g_timeout_add(25, report_state, NULL);
    }
    g_main_loop_run(loop);
    g_main_loop_unref(loop);
    g_free(last_report);
    g_free(motion_mode); g_free(motion_body);
    if (motion_frames) json_array_unref(motion_frames);
    for (guint index = 0; index < G_N_ELEMENTS(focus_transitions); index++)
        if (focus_transitions[index]) json_object_unref(focus_transitions[index]);
    for (guint index = 0; index < language_transition_count; index++)
        json_object_unref(language_transitions[index]);
    if (first_insert_context) json_object_unref(first_insert_context);
    if (first_delete_context) json_object_unref(first_delete_context);
    return 0;
}
